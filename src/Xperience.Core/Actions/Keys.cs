namespace Xperience.Core.Actions;

/// <summary>The modifier keys a shortcut can hold.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Super = 8,
}

/// <summary>One key of the keyboard: the name shortcuts are written with, the desktop's name for it and the kernel's code.</summary>
public sealed record KeyInfo(string Name, string DesktopName, int Code);

/// <summary>The keys a shortcut can use. Names hold no space and no plus sign, so a shortcut splits cleanly.</summary>
public static class KeyTable
{
    // The kernel's codes for the modifier keys (input-event-codes.h).
    public const int LeftCtrl = 29, LeftShift = 42, LeftAlt = 56, RightAlt = 100, LeftSuper = 125, Space = 57;

    static readonly KeyInfo[] All =
    [
        .. "QWERTYUIOP".Select((c, i) => new KeyInfo(c.ToString(), c.ToString(), 16 + i)),
        .. "ASDFGHJKL".Select((c, i) => new KeyInfo(c.ToString(), c.ToString(), 30 + i)),
        .. "ZXCVBNM".Select((c, i) => new KeyInfo(c.ToString(), c.ToString(), 44 + i)),
        .. "1234567890".Select((c, i) => new KeyInfo(c.ToString(), c.ToString(), 2 + i)),
        .. Enumerable.Range(1, 10).Select(n => new KeyInfo($"F{n}", $"F{n}", 58 + n)),
        new("F11", "F11", 87), new("F12", "F12", 88),
        new("Space", "Space", Space), new("Tab", "Tab", 15), new("Enter", "Return", 28), new("Esc", "Esc", 1),
        new("Backspace", "Backspace", 14), new("Delete", "Del", 111), new("Insert", "Ins", 110),
        new("Home", "Home", 102), new("End", "End", 107), new("PageUp", "PgUp", 104), new("PageDown", "PgDown", 109),
        new("Up", "Up", 103), new("Down", "Down", 108), new("Left", "Left", 105), new("Right", "Right", 106),
        new("-", "-", 12), new("=", "=", 13), new("[", "[", 26), new("]", "]", 27), new(";", ";", 39), new("'", "'", 40),
        new("`", "`", 41), new("\\", "\\", 43), new(",", ",", 51), new(".", ".", 52), new("/", "/", 53),
    ];

    static readonly Dictionary<string, KeyInfo> ByName = All.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<KeyInfo> Keys => All;

    public static KeyInfo? Find(string name) => ByName.GetValueOrDefault(name);
}

/// <summary>
/// One key press with its modifiers, such as Ctrl+Z, or modifiers alone, such as Alt. Written the way people
/// write shortcuts; an empty key means the modifiers alone.
/// </summary>
public readonly record struct KeyCombo(KeyMods Mods, string Key)
{
    static readonly (KeyMods Mod, string Name, string DesktopName, int Code)[] ModOrder =
    [
        (KeyMods.Super, "Super", "Meta", KeyTable.LeftSuper),
        (KeyMods.Ctrl, "Ctrl", "Ctrl", KeyTable.LeftCtrl),
        (KeyMods.Alt, "Alt", "Alt", KeyTable.LeftAlt),
        (KeyMods.Shift, "Shift", "Shift", KeyTable.LeftShift),
    ];

    public bool IsEmpty => Mods == KeyMods.None && string.IsNullOrEmpty(Key);

    /// <summary>True for modifiers with no key, such as Alt.</summary>
    public bool IsModifiersOnly => string.IsNullOrEmpty(Key) && Mods != KeyMods.None;

    /// <summary>The shortcut as people write it: Ctrl+Shift+Z.</summary>
    public override string ToString() => Join(m => m.Name, KeyTable.Find(Key ?? "")?.Name ?? "");

    /// <summary>
    /// The shortcut in the form KDE stores, or null when KDE cannot hold it: it takes a key with any modifiers,
    /// or exactly one modifier alone.
    /// </summary>
    public string? ToDesktop()
    {
        if (IsEmpty)
            return null;
        if (IsModifiersOnly)
            return System.Numerics.BitOperations.PopCount((uint)Mods) == 1 ? Join(m => m.DesktopName, "") : null;
        return KeyTable.Find(Key) is { } key ? Join(m => m.DesktopName, key.DesktopName) : null;
    }

    /// <summary>The kernel's key codes in the order to press them; released in reverse.</summary>
    public IReadOnlyList<int> Codes()
    {
        var mods = Mods;
        var codes = ModOrder.Where(m => mods.HasFlag(m.Mod)).Select(m => m.Code).ToList();
        if (KeyTable.Find(Key ?? "") is { } key)
            codes.Add(key.Code);
        return codes;
    }

    string Join(Func<(KeyMods Mod, string Name, string DesktopName, int Code), string> name, string key)
    {
        var mods = Mods;
        var parts = ModOrder.Where(m => mods.HasFlag(m.Mod)).Select(name).ToList();
        if (key.Length > 0)
            parts.Add(key);
        return string.Join('+', parts);
    }

    /// <summary>Reads a shortcut as written by <see cref="ToString"/>; null when a part is not a known key.</summary>
    public static KeyCombo? Parse(string text)
    {
        var mods = KeyMods.None;
        string key = "";
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int mod = Array.FindIndex(ModOrder, m => m.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (mod >= 0)
                mods |= ModOrder[mod].Mod;
            else if (key.Length == 0 && KeyTable.Find(part) is { } found)
                key = found.Name;
            else
                return null;
        }
        var combo = new KeyCombo(mods, key);
        return combo.IsEmpty ? null : combo;
    }

    /// <summary>Reads shortcuts written one after another with spaces between; unknown ones are left out.</summary>
    public static IReadOnlyList<KeyCombo> ParseAll(string? text) =>
        [.. (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<KeyCombo>()];

    public static string Write(IEnumerable<KeyCombo> combos) => string.Join(' ', combos.Where(c => !c.IsEmpty));
}
