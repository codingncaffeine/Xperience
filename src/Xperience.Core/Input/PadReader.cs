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
    volatile bool _stopped;

    PadReader(FileStream stream, Action<int, bool> onKey)
    {
        _stream = stream;
        _onKey = onKey;
        new Thread(Run) { IsBackground = true, Name = "pad reader" }.Start();
    }

    /// <summary>
    /// Opens the keys' input node; null when it cannot be read. <paramref name="onKey"/> gets the key's number,
    /// counted from 0, and whether it went down, on the reader's thread.
    /// </summary>
    public static PadReader? Open(string? node, Action<int, bool> onKey)
    {
        if (node == null)
            return null;
        try
        {
            return new PadReader(new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, KernelInput.EventSize * 16, FileOptions.None), onKey);
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
        if (type != KernelInput.EvKey || code < KernelInput.BtnMisc || code >= KernelInput.BtnMisc + 16 || value is not (0 or 1))
            return null;
        return (code - KernelInput.BtnMisc, value == 1);
    }

    public void Dispose()
    {
        _stopped = true;
        _stream.Dispose();
    }
}
