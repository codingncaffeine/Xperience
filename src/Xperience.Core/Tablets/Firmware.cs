using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xperience.Core.Input;

namespace Xperience.Core.Tablets;

/// <summary>
/// The tablet's firmware version, as its maker's app shows it: two pieces of text the tablet keeps among its USB
/// descriptors, one naming the firmware and one dating it. No other descriptor refers to them, so the kernel does
/// not publish them; they are asked for with the same standard request that reads a device's name. Asking
/// changes nothing on the tablet. It needs leave to open the tablet's USB node, which the driver's rules give
/// the person at the seat.
/// </summary>
public sealed class FirmwareReader
{
    /// <summary>The places of the two pieces of text among the tablet's string descriptors.</summary>
    public const int NameIndex = 4, DateIndex = 5;

    /// <summary>A reader on a machine with no USB devices at all: it never finds a tablet. For tests and demos.</summary>
    public static FirmwareReader Absent { get; } = new("/nonexistent/usb/devices", "/nonexistent/usb/nodes", static (_, _) => null);

    readonly string _usbRoot, _nodeRoot;
    readonly Func<string, int, string?> _text;
    readonly Lock _gate = new();
    string _node = "";
    string? _known;

    /// <param name="usbRoot">Where the kernel lists USB devices.</param>
    /// <param name="nodeRoot">Where their nodes are, by bus and device number.</param>
    /// <param name="text">Reads one string descriptor from a node; null when it cannot. The default asks the device.</param>
    public FirmwareReader(string usbRoot = "/sys/bus/usb/devices", string nodeRoot = "/dev/bus/usb", Func<string, int, string?>? text = null)
    {
        _usbRoot = usbRoot;
        _nodeRoot = nodeRoot;
        _text = text ?? UsbText.Read;
    }

    /// <summary>
    /// The version, or null when the tablet is not plugged in or this account may not open its USB node. The
    /// tablet is asked once each time it is plugged in; a refusal is asked again, since leave can be given later.
    /// </summary>
    public string? Read(TabletModel tablet)
    {
        string? node = Node(tablet);
        lock (_gate)
        {
            if (node == null)
            {
                (_node, _known) = ("", null);
                return null;
            }
            if (node == _node && _known != null)
                return _known;
            string?[] parts = [_text(node, NameIndex), _text(node, DateIndex)];
            string version = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
            (_node, _known) = (node, version.Length > 0 ? version : null);
            return _known;
        }
    }

    /// <summary>The tablet's USB node, found by its maker and product numbers among the kernel's devices.</summary>
    string? Node(TabletModel tablet)
    {
        try
        {
            foreach (string device in Directory.EnumerateDirectories(_usbRoot))
            {
                if (Hex(Path.Combine(device, "idVendor")) != tablet.VendorId || Hex(Path.Combine(device, "idProduct")) != tablet.ProductId)
                    continue;
                if (int.TryParse(Text(Path.Combine(device, "busnum")), NumberStyles.None, CultureInfo.InvariantCulture, out int bus) &&
                    int.TryParse(Text(Path.Combine(device, "devnum")), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                    return Path.Combine(_nodeRoot, bus.ToString("000", CultureInfo.InvariantCulture), number.ToString("000", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the kernel's device list there is no tablet to ask.
        }
        return null;
    }

    static string Text(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    static int Hex(string file) => int.TryParse(Text(file), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : -1;
}

/// <summary>Reads a USB device's string descriptors through its node, with the standard request for them.</summary>
static class UsbText
{
    // The standard request "get descriptor", device to host, and the descriptor kind "string" (USB 2.0, chapter 9).
    const byte DeviceToHost = 0x80, GetDescriptor = 0x06, StringKind = 0x03;
    const ushort English = 0x0409;
    const int Longest = 255;

    /// <summary>One string descriptor as text, or null when the node cannot be opened or the device has none there.</summary>
    public static string? Read(string node, int index)
    {
        try
        {
            using var handle = File.OpenHandle(node, FileMode.Open, FileAccess.ReadWrite);
            // Index 0 lists the languages the device has its text in; it is asked for the first of them.
            ushort language = Fetch(handle, 0, 0) is { Length: >= 4 } languages ? (ushort)(languages[2] | languages[3] << 8) : English;
            if (Fetch(handle, index, language) is not { Length: >= 2 } raw)
                return null;
            int length = Math.Min(raw[0], raw.Length);
            return length > 2 ? Encoding.Unicode.GetString(raw, 2, (length - 2) & ~1) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static byte[]? Fetch(SafeFileHandle handle, int index, ushort language)
    {
        byte[] buffer = new byte[Longest];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var transfer = new UsbControl
            {
                RequestType = DeviceToHost,
                Request = GetDescriptor,
                Value = (ushort)(StringKind << 8 | index),
                Index = language,
                Length = Longest,
                TimeoutMs = 500,
                Data = pinned.AddrOfPinnedObject(),
            };
            int got = Libc.Transfer(handle, KernelInput.UsbdevfsControl, ref transfer);
            return got >= 2 ? buffer[..got] : null;
        }
        finally
        {
            pinned.Free();
        }
    }
}

/// <summary>The kernel's block for one control transfer on a USB node (struct usbdevfs_ctrltransfer).</summary>
[StructLayout(LayoutKind.Sequential)]
struct UsbControl
{
    public byte RequestType, Request;
    public ushort Value, Index, Length;
    public uint TimeoutMs;
    public nint Data;
}
