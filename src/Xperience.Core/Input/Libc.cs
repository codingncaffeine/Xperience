using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Xperience.Core.Input;

/// <summary>The one C library call this program needs: device control requests on an open device node.</summary>
static class Libc
{
    static Libc() => NativeLibrary.SetDllImportResolver(typeof(Libc).Assembly, Resolve);

    /// <summary>The C library goes by different file names on glibc and musl systems.</summary>
    static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "libc")
            return 0;
        foreach (string candidate in (string[])["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc.so"])
        {
            if (NativeLibrary.TryLoad(candidate, out nint handle))
                return handle;
        }
        return 0;
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    static extern int IoctlValue(SafeFileHandle fd, nuint request, nint argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    static extern int IoctlBuffer(SafeFileHandle fd, nuint request, byte[] argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    static extern int IoctlUsb(SafeFileHandle fd, nuint request, ref Tablets.UsbControl argument);

    /// <summary>A control transfer on a USB node. Returns how many bytes came back, or -1 when the kernel or the device refuses.</summary>
    public static int Transfer(SafeFileHandle fd, uint request, ref Tablets.UsbControl transfer) => IoctlUsb(fd, request, ref transfer);

    /// <summary>A request that takes a number. Returns false, with the error number, when the kernel refuses.</summary>
    public static bool Control(SafeFileHandle fd, uint request, int argument, out int error)
    {
        bool ok = IoctlValue(fd, request, argument) >= 0;
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    /// <summary>A request that reads or fills a block of bytes.</summary>
    public static bool Control(SafeFileHandle fd, uint request, byte[] argument, out int error)
    {
        bool ok = IoctlBuffer(fd, request, argument) >= 0;
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }
}
