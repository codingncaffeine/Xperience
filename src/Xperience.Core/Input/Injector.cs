using System.Buffers.Binary;
using Xperience.Core.Actions;

namespace Xperience.Core.Input;

/// <summary>Types keys, clicks mouse buttons and turns the wheel on the user's behalf.</summary>
public interface IInjector : IDisposable
{
    /// <summary>Gets everything ready; false when this account may not create a virtual keyboard.</summary>
    bool Prepare();

    /// <summary>Presses or releases a key or a mouse button, by the kernel's code for it.</summary>
    void Key(int code, bool down);

    /// <summary>Turns the mouse wheel: positive is up.</summary>
    void Wheel(int steps);
}

/// <summary>The kernel's numbers this program uses to talk to its input layer. A test checks them against the kernel's headers.</summary>
public static class KernelInput
{
    public const ushort EvSyn = 0, EvKey = 1, EvRel = 2;
    public const ushort RelX = 0, RelY = 1, RelHWheel = 6, RelWheel = 8;
    public const ushort BtnMisc = 0x100, BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112;
    public const int EventSize = 24;          // struct input_event on a 64-bit kernel
    public const int SetupSize = 92;          // struct uinput_setup
    public const int NameLength = 80;         // UINPUT_MAX_NAME_SIZE
    public const ushort BusVirtual = 6;       // BUS_VIRTUAL
    public const uint UiSetEvBit = 0x40045564, UiSetKeyBit = 0x40045565, UiSetRelBit = 0x40045566;
    public const uint UiDevSetup = 0x405c5503, UiDevCreate = 0x5501, UiDevDestroy = 0x5502;
    public const int AbsInfoSize = 24;        // struct input_absinfo
    public const uint EviocgAbsX = 0x80184540, EviocgAbsY = 0x80184541;

    // A control transfer on a USB device's node (usbdevice_fs.h), and the size of the block it takes.
    public const uint UsbdevfsControl = 0xC0185500;
    public const int UsbControlSize = 24;
}

/// <summary>
/// A keyboard and mouse made through the kernel's uinput device. It is created the first time something has
/// to be typed, so a setup that never needs it never adds a device to the desktop.
/// </summary>
public sealed class UinputInjector : IInjector
{
    const string Node = "/dev/uinput";

    readonly Lock _gate = new();
    FileStream? _device;
    bool _failed;

    public bool Prepare()
    {
        lock (_gate)
        {
            if (_device != null)
                return true;
            if (_failed)
                return false;
            FileStream? device = null;
            try
            {
                device = new FileStream(Node, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
                var handle = device.SafeFileHandle;
                Check(Libc.Control(handle, KernelInput.UiSetEvBit, KernelInput.EvKey, out int error), error);
                Check(Libc.Control(handle, KernelInput.UiSetEvBit, KernelInput.EvRel, out error), error);
                foreach (int code in KeyTable.Keys.Select(k => k.Code).Concat(
                    [KeyTable.LeftCtrl, KeyTable.LeftShift, KeyTable.LeftAlt, KeyTable.RightAlt, KeyTable.LeftSuper, KernelInput.BtnLeft, KernelInput.BtnRight, KernelInput.BtnMiddle]).Distinct())
                    Check(Libc.Control(handle, KernelInput.UiSetKeyBit, code, out error), error);
                // A device with mouse buttons is only taken for a mouse when it can also move.
                foreach (ushort axis in (ushort[])[KernelInput.RelX, KernelInput.RelY, KernelInput.RelWheel])
                    Check(Libc.Control(handle, KernelInput.UiSetRelBit, axis, out error), error);

                byte[] setup = new byte[KernelInput.SetupSize];
                BinaryPrimitives.WriteUInt16LittleEndian(setup, KernelInput.BusVirtual);
                System.Text.Encoding.ASCII.GetBytes("Xperience virtual keys", setup.AsSpan(8, KernelInput.NameLength - 1));
                Check(Libc.Control(handle, KernelInput.UiDevSetup, setup, out error), error);
                Check(Libc.Control(handle, KernelInput.UiDevCreate, 0, out error), error);
                _device = device;
                device = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                _failed = true;
                return false;
            }
            finally
            {
                device?.Dispose();
            }
        }
        // The desktop needs a moment to take the new device up; what is typed before that is lost.
        Thread.Sleep(400);
        return true;
    }

    static void Check(bool ok, int error)
    {
        if (!ok)
            throw new IOException($"The kernel refused to set up the virtual keyboard (error {error}).");
    }

    public void Key(int code, bool down)
    {
        Write(KernelInput.EvKey, (ushort)code, down ? 1 : 0);
        Write(KernelInput.EvSyn, 0, 0);
    }

    public void Wheel(int steps)
    {
        Write(KernelInput.EvRel, KernelInput.RelWheel, steps);
        Write(KernelInput.EvSyn, 0, 0);
    }

    void Write(ushort type, ushort code, int value)
    {
        Span<byte> record = stackalloc byte[KernelInput.EventSize];
        record.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(record[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(record[18..], code);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], value);
        lock (_gate)
        {
            try
            {
                _device?.Write(record);
                _device?.Flush();
            }
            catch (IOException)
            {
                // The virtual device is gone; the next Prepare makes a new one.
                _device?.Dispose();
                _device = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_device != null)
            {
                Libc.Control(_device.SafeFileHandle, KernelInput.UiDevDestroy, 0, out _);
                _device.Dispose();
                _device = null;
            }
        }
    }
}

/// <summary>An injector that accepts everything and types nothing, for showing the program where no virtual keyboard may exist.</summary>
public sealed class NoInjector : IInjector
{
    public bool Prepare() => true;
    public void Key(int code, bool down) { }
    public void Wheel(int steps) { }
    public void Dispose() { }
}
