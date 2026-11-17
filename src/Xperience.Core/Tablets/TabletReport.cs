using System.Globalization;
using System.Text;

namespace Xperience.Core.Tablets;

/// <summary>One of the tablet's controls in the key and dial check, and whether it has been seen working.</summary>
public sealed record CheckedControl(string Name, bool Seen);

/// <summary>What goes into a report about a tablet, gathered by the window.</summary>
public sealed record ReportFacts(
    string Version,
    string Kernel,
    TabletModel Model,
    IReadOnlyDictionary<string, string> Answer,
    string? Firmware,
    IReadOnlyList<string> State,
    IReadOnlyList<CheckedControl> Checks,
    IReadOnlyList<string> RecordTail);

/// <summary>
/// The text file an owner can send so a model can be confirmed: which tablet it is and what it said it is, the
/// driver and the desktop as they are, the keys and dials seen working, and the end of the session record. It
/// holds nothing of the person beyond what the record holds, and stays on the computer until they send it.
/// </summary>
public static class TabletReport
{
    public static string Build(ReportFacts facts)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append('\n');

        Line("Xperience tablet report");
        Line($"Xperience {facts.Version}, kernel {facts.Kernel}");
        Line("");
        Line("TABLET");
        var model = facts.Model;
        Line(string.Create(culture, $"  {model.Name}, USB 28bd:{model.ProductId:x4}, {model.Keys.Count} keys, {model.Dials.Count} dials, {(model.HasScreen ? "pen display" : "no screen")}"));
        Line($"  firmware {facts.Firmware ?? "not read"}");
        Line(facts.Answer.Count == 0 ? "  answer   none (the driver's probe has not asked it)"
            : "  answer   " + string.Join(", ", facts.Answer.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key["XPERIENCE_TABLET_".Length..].ToLowerInvariant()} {p.Value}")));
        Line("");
        Line("KEYS AND DIALS SEEN");
        if (facts.Checks.Count == 0)
            Line("  this tablet has none");
        foreach (var check in facts.Checks)
            Line($"  {(check.Seen ? "seen    " : "not seen")} {check.Name}");
        Line("");
        Line("STATE");
        foreach (string line in facts.State)
            Line("  " + line);
        Line("");
        Line("RECORD (the end of this session's)");
        foreach (string line in facts.RecordTail)
            Line("  " + line);
        return text.ToString();
    }

    /// <summary>The controls of a model to check: each key, and each dial both ways.</summary>
    public static IReadOnlyList<string> Controls(TabletModel model) =>
    [
        .. Enumerable.Range(1, model.Keys.Count).Select(i => string.Create(CultureInfo.InvariantCulture, $"Key {i}")),
        .. Enumerable.Range(1, model.Dials.Count).SelectMany(d => new[]
        {
            string.Create(CultureInfo.InvariantCulture, $"Dial {d} one way"),
            string.Create(CultureInfo.InvariantCulture, $"Dial {d} other way"),
        }),
    ];

    /// <summary>The last lines of a text file; none when it cannot be read.</summary>
    public static IReadOnlyList<string> Tail(string path, int lines)
    {
        try
        {
            var queue = new Queue<string>();
            foreach (string line in File.ReadLines(path))
            {
                queue.Enqueue(line);
                if (queue.Count > lines)
                    queue.Dequeue();
            }
            return [.. queue];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
