using System.Buffers.Binary;

namespace Xperience.Core.Pen;

/// <summary>What the pen reported last. Coordinates and pressure are the tablet's own numbers.</summary>
public readonly record struct PenSample(bool InRange, bool Touching, bool Eraser, bool LowerButton, bool UpperButton, int X, int Y, int Pressure, int TiltX, int TiltY);

/// <summary>
/// Listens to the pen's input node on a thread of its own and keeps the latest report. It only listens: the pen
/// still reaches the desktop the usual way.
/// </summary>
public sealed class PenReader : IDisposable
{
    const int EventSize = 24;   // two 64-bit time fields, then type, code and value
    const ushort EvSyn = 0, EvKey = 1, EvAbs = 3;
    const ushort AbsX = 0, AbsY = 1, AbsPressure = 0x18, AbsTiltX = 0x1a, AbsTiltY = 0x1b;
    const ushort BtnToolPen = 0x140, BtnToolRubber = 0x141, BtnTouch = 0x14a, BtnStylus = 0x14b, BtnStylus2 = 0x14c;

    readonly FileStream _stream;
    readonly Thread _thread;
    readonly Lock _gate = new();
    PenSample _latest;
    volatile bool _stopped;

    PenReader(FileStream stream)
    {
        _stream = stream;
        _thread = new Thread(Run) { IsBackground = true, Name = "pen reader" };
        _thread.Start();
    }

    /// <summary>Opens the node; null when it cannot be read, which leaves the live readings empty.</summary>
    public static PenReader? Open(string? node)
    {
        if (node == null)
            return null;
        try
        {
            return new PenReader(new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, EventSize * 64, FileOptions.None));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The newest report. Safe from any thread.</summary>
    public PenSample Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>True once the node has gone away or been closed.</summary>
    public bool Ended => _stopped;

    void Run()
    {
        Span<byte> buffer = stackalloc byte[EventSize];
        var pending = default(PenSample);
        bool pen = false, rubber = false;
        try
        {
            while (!_stopped)
            {
                _stream.ReadExactly(buffer);
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(buffer[16..]);
                ushort code = BinaryPrimitives.ReadUInt16LittleEndian(buffer[18..]);
                int value = BinaryPrimitives.ReadInt32LittleEndian(buffer[20..]);
                switch (type)
                {
                    case EvAbs:
                        pending = code switch
                        {
                            AbsX => pending with { X = value },
                            AbsY => pending with { Y = value },
                            AbsPressure => pending with { Pressure = value },
                            AbsTiltX => pending with { TiltX = value },
                            AbsTiltY => pending with { TiltY = value },
                            _ => pending,
                        };
                        break;
                    case EvKey:
                        bool down = value != 0;
                        switch (code)
                        {
                            case BtnToolPen: pen = down; break;
                            case BtnToolRubber: rubber = down; break;
                            case BtnTouch: pending = pending with { Touching = down }; break;
                            case BtnStylus: pending = pending with { LowerButton = down }; break;
                            case BtnStylus2: pending = pending with { UpperButton = down }; break;
                        }
                        break;
                    case EvSyn:
                        pending = pending with { InRange = pen || rubber, Eraser = rubber };
                        if (!pending.InRange)
                            pending = pending with { Pressure = 0, Touching = false };
                        lock (_gate)
                            _latest = pending;
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or UnauthorizedAccessException)
        {
            // The tablet was unplugged or the reader was closed.
        }
        _stopped = true;
        lock (_gate)
            _latest = default;
    }

    public void Dispose()
    {
        _stopped = true;
        _stream.Dispose();
    }
}
