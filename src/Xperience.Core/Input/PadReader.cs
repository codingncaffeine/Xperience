using System.Buffers.Binary;

namespace Xperience.Core.Input;

/// <summary>
/// Listens to the tablet's keys on a thread of its own. It only listens: the keys still reach the desktop,
/// which carries out the bindings it holds for them.
/// </summary>
public sealed class PadReader : IDisposable
{
    readonly FileStream _stream;
    readonly Action<int, bool> _onKey;
    const ushort EvMsc = 4;

    readonly Action<ushort, ushort, int>? _onEvent;
    volatile bool _stopped;

    PadReader(FileStream stream, Action<int, bool> onKey, Action<ushort, ushort, int>? onEvent)
    {
        _stream = stream;
        _onKey = onKey;
        _onEvent = onEvent;
        new Thread(Run) { IsBackground = true, Name = "pad reader" }.Start();
    }

    /// <summary>
    /// Opens the keys' input node; null when it cannot be read. <paramref name="onKey"/> gets the key's number,
    /// counted from 0, and whether it went down, on the reader's thread. <paramref name="onEvent"/>, when given,
    /// gets every event of the node as the kernel wrote it (type, code, value), dial turns included, for the record.
    /// </summary>
    public static PadReader? Open(string? node, Action<int, bool> onKey, Action<ushort, ushort, int>? onEvent = null)
    {
        if (node == null)
            return null;
        try
        {
            return new PadReader(new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, KernelInput.EventSize * 16, FileOptions.None), onKey, onEvent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True once the node has gone away or been closed.</summary>
    public bool Ended => _stopped;

    void Run()
    {
        Span<byte> record = stackalloc byte[KernelInput.EventSize];
        try
        {
            while (!_stopped)
            {
                _stream.ReadExactly(record);
                // The scan code the kernel sends along with every key says nothing the key itself does not.
                if (_onEvent != null && Raw(record) is var (type, code, value) && type != KernelInput.EvSyn && type != EvMsc)
                    _onEvent(type, code, value);
                if (Decode(record) is var (key, down))
                    _onKey(key, down);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or UnauthorizedAccessException)
        {
            // The tablet was unplugged or the reader was closed.
        }
        _stopped = true;
    }

    /// <summary>A key going down or up, from one record of the node; null for anything else, repeats included.</summary>
    internal static (int Key, bool Down)? Decode(ReadOnlySpan<byte> record)
    {
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(record[18..]);
        int value = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
        if (type != KernelInput.EvKey || value is not (0 or 1) || KeyIndex(code) is not { } key)
            return null;
        return (key, value == 1);
    }

    /// <summary>
    /// Which key a code is, from 0, as the driver numbers them: keys 1 to 10 are BTN_0 to BTN_9, keys 11 to 16 are
    /// BTN_A and on (the order user space numbers a pad's buttons in); null for any other code.
    /// </summary>
    internal static int? KeyIndex(ushort code) => code switch
    {
        >= KernelInput.BtnMisc and < KernelInput.BtnMisc + 10 => code - KernelInput.BtnMisc,
        >= BtnA and < BtnA + 6 => 10 + code - BtnA,
        _ => null,
    };

    const ushort BtnA = 0x130;

    /// <summary>One record of the node as the kernel wrote it: type, code and value.</summary>
    internal static (ushort Type, ushort Code, int Value) Raw(ReadOnlySpan<byte> record) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(record[16..]), BinaryPrimitives.ReadUInt16LittleEndian(record[18..]), BinaryPrimitives.ReadInt32LittleEndian(record[20..]));

    /// <summary>The kernel's names for what a tablet's keys and dials send, for the record; other codes as numbers.</summary>
    public static string Name(ushort type, ushort code) => (type, code) switch
    {
        (KernelInput.EvKey, _) when KeyIndex(code) is { } key => $"key {key + 1}",
        (KernelInput.EvKey, _) => $"key code {code}",
        (2, 6) => "REL_HWHEEL",
        (2, 8) => "REL_WHEEL",
        (2, 7) => "REL_DIAL",
        (2, 11) => "REL_WHEEL_HI_RES",
        (2, 12) => "REL_HWHEEL_HI_RES",
        (2, _) => $"relative code {code}",
        (3, _) => $"absolute code {code}",
        (4, _) => $"misc code {code}",
        _ => $"type {type} code {code}",
    };

    public void Dispose()
    {
        _stopped = true;
        _stream.Dispose();
    }
}
