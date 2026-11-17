using System.Globalization;
using Xperience.Core.Actions;
using Xperience.Core.Settings;

namespace Xperience.Core.Desktop;

/// <summary>
/// One binding in the desktop's own list of tablet button bindings: which control of which device, and what
/// the desktop does when it is used. An empty value means no binding.
/// </summary>
public sealed record Rebind(string Group, string Device, string Key, string Value)
{
    /// <summary>The groups the desktop keeps: the tablet's keys, the pen's buttons and the tablet's dials.</summary>
    public const string Keys = "Tablet", PenButtons = "TabletTool", Dials = "TabletDial";

    public string Id => $"{Group}/{Device}/{Key}";
}

/// <summary>Which of the tablet's devices the desktop lists, by the names it files their bindings under.</summary>
public sealed record TabletDevices(string? PenName, string? PadName);

/// <summary>
/// Turns assignments into the desktop's bindings. The desktop does what it can do by itself: one shortcut held
/// with a key, a plain mouse click, scrolling. Everything else is switched off in the desktop and done by
/// Xperience when it sees the control used.
/// </summary>
public static class Assignments
{
    // The kernel's codes for the pen's two buttons and for the mouse buttons (input-event-codes.h).
    public const int PenLowerButton = 0x14b, PenUpperButton = 0x14c;
    const int MouseLeft = 0x110, MouseRight = 0x111, MouseMiddle = 0x112;

    // The desktop's numbers for the modifiers a mouse binding can hold.
    const int DesktopShift = 0x02000000, DesktopCtrl = 0x04000000, DesktopAlt = 0x08000000;

    const string Off = "Disabled";

    /// <summary>The binding the desktop can carry out by itself for an action, or null when Xperience must do it.</summary>
    public static string? Native(PadAction action)
    {
        switch (action.Kind)
        {
            case ActionKind.Disabled:
                return Off;
            case ActionKind.Special when action.Special == SpecialAct.PenEraser:
                // The driver switches the pen when it sees the key; the desktop has nothing to do for it.
                return Off;
            case ActionKind.Shortcut:
                return action.Combos is [var only] && only.ToDesktop() is { } keys ? "Key," + Escape(keys) : null;
            case ActionKind.Mouse:
                int button = action.Mouse switch
                {
                    MouseAct.LeftClick => MouseLeft,
                    MouseAct.MiddleClick => MouseMiddle,
                    MouseAct.RightClick => MouseRight,
                    _ => 0,
                };
                if (button == 0 || (action.MouseMods & (MouseMods.RightAlt | MouseMods.Space)) != 0)
                    return null;
                int mods = (action.MouseMods.HasFlag(MouseMods.Shift) ? DesktopShift : 0) |
                    (action.MouseMods.HasFlag(MouseMods.Ctrl) ? DesktopCtrl : 0) |
                    (action.MouseMods.HasFlag(MouseMods.LeftAlt) ? DesktopAlt : 0);
                return string.Create(CultureInfo.InvariantCulture, $"MouseButton,{button}") + (mods != 0 ? string.Create(CultureInfo.InvariantCulture, $",{mods}") : "");
            default:
                return null;
        }
    }

    /// <summary>True when Xperience itself has to act when the control is used.</summary>
    public static bool NeedsXperience(PadAction action) => action.Kind != ActionKind.Default && Native(action) == null;

    /// <summary>The binding for a dial function: a shortcut per direction, the wheel, or nothing.</summary>
    public static string Native(DialFunction function) => function.Kind switch
    {
        DialKind.Scroll => "Scroll",
        _ when function.Turns() is (var back, var forward) && back.ToDesktop() is { } down && forward.ToDesktop() is { } up =>
            // Up is the turn the desktop reports as positive; any turn at all triggers it.
            $"AxisKey,{Escape(up)},{Escape(down)},1",
        _ => Off,
    };

    /// <summary>
    /// Every binding the desktop should hold for a profile. Entries with an empty value are bindings that must
    /// not exist, so that leftovers get removed.
    /// </summary>
    /// <param name="config">The settings as a whole.</param>
    /// <param name="profile">The profile in effect.</param>
    /// <param name="dialSlots">Which function each dial is on, in the order the dials run as the tablet is held.</param>
    /// <param name="devices">The names the desktop files the bindings under.</param>
    /// <param name="turn">How the tablet is turned, which decides which of its own keys is the first as held.</param>
    public static IReadOnlyList<Rebind> Desired(XperienceConfig config, Profile profile, IReadOnlyList<int> dialSlots, TabletDevices devices, TabletTurn turn = TabletTurn.None)
    {
        var wanted = new List<Rebind>();
        if (devices.PadName is { Length: > 0 } pad)
        {
            // The desktop knows the keys and dials by the tablet's own numbers; the profile has them as held.
            for (int own = 0; own < Profile.KeyCount; own++)
                wanted.Add(new Rebind(Rebind.Keys, pad, own.ToString(CultureInfo.InvariantCulture),
                    config.KeysDisabled ? Off : Native(profile.Key(Tablets.HeldOrder.Key(own, turn))) ?? Off));
            for (int own = 0; own < Profile.DialCount; own++)
            {
                int held = Tablets.HeldOrder.Dial(own, turn);
                int slot = held < dialSlots.Count ? dialSlots[held] : 0;
                wanted.Add(new Rebind(Rebind.Dials, pad, own.ToString(CultureInfo.InvariantCulture), config.KeysDisabled ? Off : Native(profile.Dial(held, slot))));
            }
        }
        if (devices.PenName is { Length: > 0 } pen)
        {
            wanted.Add(new Rebind(Rebind.PenButtons, pen, PenLowerButton.ToString(CultureInfo.InvariantCulture), PenValue(profile.PenLower)));
            wanted.Add(new Rebind(Rebind.PenButtons, pen, PenUpperButton.ToString(CultureInfo.InvariantCulture), PenValue(profile.PenUpper)));
        }
        return wanted;
    }

    static string PenValue(PadAction action) => action.Kind == ActionKind.Default ? "" : Native(action) ?? Off;

    /// <summary>What has to change to get from what the desktop holds to what it should hold.</summary>
    public static IReadOnlyList<Rebind> Changes(IReadOnlyList<Rebind> current, IReadOnlyList<Rebind> desired)
    {
        var have = current.ToDictionary(r => r.Id, r => r.Value);
        return [.. desired.Where(d => have.GetValueOrDefault(d.Id, "") != d.Value)];
    }

    /// <summary>
    /// The tablet's keys the driver has to be told of because they are set to switch between pen and eraser:
    /// the tablet's own numbers, 1 to 8, as digits in rising order. Only the profile for every application
    /// counts, since the driver cannot change with the application in front.
    /// </summary>
    public static string SwitchKeys(XperienceConfig config, TabletTurn turn)
    {
        if (config.KeysDisabled)
            return "";
        var profile = config.DefaultProfile;
        return string.Concat(Enumerable.Range(0, Profile.KeyCount)
            .Where(held => profile.Key(held) is { Kind: ActionKind.Special, Special: SpecialAct.PenEraser })
            .Select(held => Tablets.HeldOrder.Key(held, turn) + 1).Order().Select(own => (char)('0' + own)));
    }

    /// <summary>The next function of a dial that does something, after the one it is on.</summary>
    public static int NextSlot(Profile profile, int dial, int current)
    {
        for (int step = 1; step <= Profile.DialSlots; step++)
        {
            int slot = (current + step) % Profile.DialSlots;
            if (profile.Dial(dial, slot).Kind != DialKind.Disabled)
                return slot;
        }
        return current;
    }

    /// <summary>A comma or a backslash inside one item of the desktop's comma-separated lists.</summary>
    static string Escape(string item) => item.Replace("\\", "\\\\").Replace(",", "\\,");
}

/// <summary>Reads the desktop's bindings file: sections like <c>[A][B][C]</c> and <c>key=value</c> lines.</summary>
public static class RebindFile
{
    public static IReadOnlyList<Rebind> Parse(string text)
    {
        var found = new List<Rebind>();
        string[]? section = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('['))
            {
                section = SplitSection(line);
                continue;
            }
            if (section is not ["ButtonRebinds", var group, var device] || line.Length == 0 || line[0] == '#')
                continue;
            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            // A key can carry a locale or flags in brackets; bindings never do.
            string key = line[..equals];
            if (key.Contains('['))
                continue;
            found.Add(new Rebind(group, device, key, Unescape(line[(equals + 1)..])));
        }
        return found;
    }

    static string[]? SplitSection(string line)
    {
        var names = new List<string>();
        int at = 0;
        while (at < line.Length && line[at] == '[')
        {
            int end = line.IndexOf(']', at);
            if (end < 0)
                return null;
            names.Add(Unescape(line[(at + 1)..end]));
            at = end + 1;
        }
        return [.. names];
    }

    /// <summary>The file's own escapes: a backslash before s, t, n, r or another backslash.</summary>
    static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var text = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                text.Append(value[i]);
                continue;
            }
            char next = value[++i];
            text.Append(next switch { 's' => ' ', 't' => '\t', 'n' => '\n', 'r' => '\r', '\\' => '\\', _ => next });
        }
        return text.ToString();
    }
}
