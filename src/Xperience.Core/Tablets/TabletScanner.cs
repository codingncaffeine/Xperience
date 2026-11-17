using System.Globalization;

namespace Xperience.Core.Tablets;

/// <summary>How the tablet is being driven right now.</summary>
public enum DriveMode
{
    /// <summary>No tablet found.</summary>
    Absent,
    /// <summary>The tablet's own mode: the keys and dials type fixed shortcuts and cannot be told apart.</summary>
    BuiltIn,
    /// <summary>The Xperience driver is loaded: the keys and dials are real controls.</summary>
    Driver,
}

/// <summary>What was found of the tablet on this machine.</summary>
public sealed record TabletPresence(DriveMode Mode, string? PenNode, string? PadNode, string? KeyboardNode, bool VendorAppRunning)
{
    public static readonly TabletPresence None = new(DriveMode.Absent, null, null, null, false);
}

/// <summary>Finds the tablet's input nodes by reading what the kernel publishes about each input device.</summary>
public static class TabletScanner
{
    const int BtnMisc = 0x100;      // the first button of a tablet pad
    const int BtnToolPen = 0x140;
    const int KeyA = 30;

    public static TabletPresence Scan(TabletModel tablet, string sysRoot = "/sys/class/input", string procRoot = "/proc")
    {
        string? pen = null, pad = null, keyboard = null;
        try
        {
            foreach (string node in Directory.EnumerateDirectories(sysRoot, "event*"))
            {
                string device = Path.Combine(node, "device");
                if (ReadHex(Path.Combine(device, "id", "vendor")) != tablet.VendorId || ReadHex(Path.Combine(device, "id", "product")) != tablet.ProductId)
                    continue;
                string keys = ReadText(Path.Combine(device, "capabilities", "key"));
                string path = "/dev/input/" + Path.GetFileName(node);
                if (HasBit(keys, BtnToolPen))
                    pen ??= path;
                else if (HasBit(keys, BtnMisc))
                    pad ??= path;
                else if (HasBit(keys, KeyA))
                    keyboard ??= path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the kernel's device list there is nothing to report.
        }

        // The driver is what puts the pen on the tablet's vendor interface, so a pen there means the driver, even
        // on a tablet with no keys and so no pad.
        var mode = pen == null && pad == null && keyboard == null ? DriveMode.Absent
            : pad != null || (pen != null && PenOnVendorInterface(sysRoot, pen)) ? DriveMode.Driver : DriveMode.BuiltIn;
        return new TabletPresence(mode, pen, pad, keyboard, VendorAppRunning(procRoot));
    }

    /// <summary>True when the pen's HID device describes the vendor page ff0a first: the tablet's vendor interface.</summary>
    static bool PenOnVendorInterface(string sysRoot, string penNode)
    {
        try
        {
            string descriptor = Path.Combine(sysRoot, Path.GetFileName(penNode), "device", "device", "report_descriptor");
            using var file = File.OpenRead(descriptor);
            Span<byte> head = stackalloc byte[3];
            return file.Read(head) == 3 && head[0] == 0x06 && head[1] == 0x0a && head[2] == 0xff;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when the maker's own driver program is running: it and the kernel cannot both drive the pen.</summary>
    static bool VendorAppRunning(string procRoot)
    {
        try
        {
            foreach (string process in Directory.EnumerateDirectories(procRoot))
            {
                if (!char.IsAsciiDigit(Path.GetFileName(process)[0]))
                    continue;
                if (ReadText(Path.Combine(process, "comm")) == "PenTablet")
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A process list that cannot be read reports no conflict.
        }
        return false;
    }

    /// <summary>
    /// Tests one bit of a capability list as the kernel prints it: 64-bit words in hex, the highest first.
    /// </summary>
    internal static bool HasBit(string capabilities, int bit)
    {
        string[] words = capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int index = words.Length - 1 - bit / 64;
        return index >= 0 && ulong.TryParse(words[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong word) && (word >> (bit % 64) & 1) == 1;
    }

    static int ReadHex(string path) =>
        int.TryParse(ReadText(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : -1;

    static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
