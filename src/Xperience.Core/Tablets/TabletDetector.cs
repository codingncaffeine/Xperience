using System.Globalization;

namespace Xperience.Core.Tablets;

/// <summary>One line of driver/models.tsv: what the model list says of a product.</summary>
public sealed record ListedModel(ushort ProductId, string Name, int Keys, int Dials, bool Screen);

/// <summary>
/// Finds which tablet is plugged in. The driver's probe asks a tablet what it is when it appears and udev keeps the
/// answer in its record of each of the tablet's HID devices; the model list adds what a tablet cannot say.
/// </summary>
public static class TabletDetector
{
    /// <summary>The model list the driver was built with, embedded from driver/models.tsv.</summary>
    public static IReadOnlyList<ListedModel> Models { get; } = ReadModels();

    static IReadOnlyList<ListedModel> ReadModels()
    {
        using var stream = typeof(TabletDetector).Assembly.GetManifestResourceStream("models.tsv");
        if (stream == null)
            return [];
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The model list's lines; comment lines, the header and short lines are skipped.</summary>
    public static IReadOnlyList<ListedModel> Parse(string text)
    {
        var models = new List<ListedModel>();
        foreach (string line in text.Split('\n'))
        {
            string[] cells = line.TrimEnd('\r').Split('\t');
            if (line.StartsWith('#') || cells.Length < 6 ||
                !ushort.TryParse(cells[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort product))
                continue;
            models.Add(new ListedModel(product, cells[1], Int(cells[2]), Int(cells[3]), cells[4] == "yes"));
        }
        return models;
    }

    /// <summary>
    /// The plugged-in tablet the driver takes, as a model; null when there is none. Without the probe's answer
    /// (the driver not installed yet) the size is not known and the picture is drawn from the list alone.
    /// </summary>
    public static TabletModel? Detect(string hidRoot = "/sys/bus/hid/devices", string udevRoot = "/run/udev/data")
    {
        try
        {
            foreach (string device in Directory.EnumerateDirectories(hidRoot, "0003:28BD:*").Order(StringComparer.Ordinal))
            {
                string id = Path.GetFileName(device)["0003:28BD:".Length..].Split('.')[0];
                if (!ushort.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort product))
                    continue;
                var listed = Models.FirstOrDefault(m => m.ProductId == product);
                if (listed == null)
                    continue;
                var answer = Record(Path.Combine(udevRoot, "+hid:" + Path.GetFileName(device)));
                string name = UsbName(device) ?? listed.Name;
                if (answer.GetValueOrDefault("XPERIENCE_TABLET_PROBE") == "ok")
                    return TabletModel.FromAnswer(name, product, Int(answer["XPERIENCE_TABLET_X_MAX"]), Int(answer["XPERIENCE_TABLET_Y_MAX"]),
                        Int(answer["XPERIENCE_TABLET_PRESSURE_MAX"]), Int(answer["XPERIENCE_TABLET_RESOLUTION"]),
                        Int(answer["XPERIENCE_TABLET_KEYS"]), listed.Dials, listed.Screen);
                if (product == TabletModel.Artist16Third.ProductId)
                    return TabletModel.Artist16Third;
                // Not yet asked: the list's keys and dials on a common 16:10 area until the driver is installed.
                return TabletModel.FromAnswer(name, product, 16000, 10000, 8191, 5080, listed.Keys, listed.Dials, listed.Screen);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the kernel's device list no tablet is found.
        }
        return null;
    }

    /// <summary>What a plugged-in tablet of this product said it is: the XPERIENCE_TABLET_ properties udev keeps.</summary>
    public static IReadOnlyDictionary<string, string> AnswerOf(ushort productId, string hidRoot = "/sys/bus/hid/devices", string udevRoot = "/run/udev/data")
    {
        try
        {
            string pattern = "0003:28BD:" + productId.ToString("X4", CultureInfo.InvariantCulture) + ".*";
            foreach (string device in Directory.EnumerateDirectories(hidRoot, pattern).Order(StringComparer.Ordinal))
            {
                var answer = Record(Path.Combine(udevRoot, "+hid:" + Path.GetFileName(device)))
                    .Where(p => p.Key.StartsWith("XPERIENCE_TABLET_", StringComparison.Ordinal)).ToDictionary();
                if (answer.Count > 0)
                    return answer;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the kernel's device list there is no answer to read.
        }
        return new Dictionary<string, string>();
    }

    /// <summary>The properties udev keeps for a device: its record's "E:NAME=VALUE" lines.</summary>
    public static Dictionary<string, string> Record(string path)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                int equals = line.IndexOf('=', StringComparison.Ordinal);
                if (line.StartsWith("E:", StringComparison.Ordinal) && equals > 2)
                    properties[line[2..equals]] = line[(equals + 1)..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No record yet: nothing is known.
        }
        return properties;
    }

    /// <summary>The product's name as the tablet gives it: the HID name less the maker's.</summary>
    static string? UsbName(string device)
    {
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(device, "uevent")))
                if (line.StartsWith("HID_NAME=", StringComparison.Ordinal))
                {
                    string name = line["HID_NAME=".Length..].Trim();
                    return name.StartsWith("XPPen ", StringComparison.Ordinal) ? name["XPPen ".Length..] : name;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable: the list's name stands in.
        }
        return null;
    }

    static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
