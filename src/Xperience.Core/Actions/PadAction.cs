namespace Xperience.Core.Actions;

/// <summary>The kinds of thing a key, a pen button or a floating-menu button can do.</summary>
public enum ActionKind
{
    /// <summary>A pen button left as the plain pen button each application knows.</summary>
    Default,
    /// <summary>One shortcut held while the control is held, or several typed one after another.</summary>
    Shortcut,
    /// <summary>A mouse click or a turn of the wheel, with modifiers held.</summary>
    Mouse,
    /// <summary>Starts a program.</summary>
    RunApp,
    /// <summary>One of the things only Xperience can do.</summary>
    Special,
    Disabled,
}

public enum MouseAct { LeftClick, DoubleClick, MiddleClick, RightClick, ScrollUp, ScrollDown }

/// <summary>What a mouse action holds down while it clicks.</summary>
[Flags]
public enum MouseMods
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    LeftAlt = 4,
    RightAlt = 8,
    Space = 16,
}

public enum SpecialAct
{
    ShowXperience,
    SwitchMonitor,
    /// <summary>Types B and E in turn: brush, eraser, brush.</summary>
    BrushEraser,
    PrecisionMode,
    SwitchDial1,
    SwitchDial2,
    FloatingMenu,
    ShowDesktop,
    ScreenKeyboard,
}

/// <summary>What one control does. Only the fields of its kind matter; the rest stay at their defaults.</summary>
public sealed record PadAction
{
    public ActionKind Kind { get; init; }

    /// <summary>The shortcuts, written with spaces between: <c>Ctrl+C Ctrl+V</c>.</summary>
    public string Keys { get; init; } = "";

    public MouseAct Mouse { get; init; }
    public MouseMods MouseMods { get; init; }

    /// <summary>The program to start, with its arguments.</summary>
    public string App { get; init; } = "";

    public SpecialAct Special { get; init; }

    /// <summary>A name of the user's own to show instead of the description.</summary>
    public string Name { get; init; } = "";

    public static readonly PadAction PenButton = new() { Kind = ActionKind.Default };
    public static readonly PadAction Off = new() { Kind = ActionKind.Disabled };

    public static PadAction Shortcut(string keys) => new() { Kind = ActionKind.Shortcut, Keys = KeyCombo.Write(KeyCombo.ParseAll(keys)) };
    public static PadAction Click(MouseAct act, MouseMods mods = MouseMods.None) => new() { Kind = ActionKind.Mouse, Mouse = act, MouseMods = mods };
    public static PadAction Run(string app) => new() { Kind = ActionKind.RunApp, App = app };
    public static PadAction Do(SpecialAct act) => new() { Kind = ActionKind.Special, Special = act };

    public IReadOnlyList<KeyCombo> Combos => KeyCombo.ParseAll(Keys);

    /// <summary>What to show on the tablet's picture and in hints: the user's name for it, or a description.</summary>
    public string Label => Name.Length > 0 ? Name : Describe();

    public string Describe() => Kind switch
    {
        ActionKind.Default => "Pen button",
        ActionKind.Shortcut => Combos is { Count: > 0 } combos ? string.Join(", ", combos) : "No shortcut",
        ActionKind.Mouse => string.Join('+', [.. ModNames(MouseMods), MouseName(Mouse)]),
        ActionKind.RunApp => App.Length > 0 ? "Run " + Path.GetFileName(App.Split(' ')[0]) : "Run application",
        ActionKind.Special => SpecialName(Special),
        _ => "Disabled",
    };

    public static string MouseName(MouseAct act) => act switch
    {
        MouseAct.LeftClick => "Left click",
        MouseAct.DoubleClick => "Left double-click",
        MouseAct.MiddleClick => "Middle click",
        MouseAct.RightClick => "Right click",
        MouseAct.ScrollUp => "Scroll up",
        _ => "Scroll down",
    };

    public static string SpecialName(SpecialAct act) => act switch
    {
        SpecialAct.ShowXperience => "Show Xperience",
        SpecialAct.SwitchMonitor => "Switch monitor",
        SpecialAct.BrushEraser => "[B]/[E]",
        SpecialAct.PrecisionMode => "Precision mode",
        SpecialAct.SwitchDial1 => "Switch dial 1 function",
        SpecialAct.SwitchDial2 => "Switch dial 2 function",
        SpecialAct.FloatingMenu => "Floating menu",
        SpecialAct.ShowDesktop => "Show desktop",
        _ => "Screen keyboard",
    };

    static IEnumerable<string> ModNames(MouseMods mods)
    {
        if (mods.HasFlag(MouseMods.Shift)) yield return "Shift";
        if (mods.HasFlag(MouseMods.Ctrl)) yield return "Ctrl";
        if (mods.HasFlag(MouseMods.LeftAlt)) yield return "Left Alt";
        if (mods.HasFlag(MouseMods.RightAlt)) yield return "Right Alt";
        if (mods.HasFlag(MouseMods.Space)) yield return "Space";
    }

    /// <summary>The kernel's key codes for what a mouse action holds down.</summary>
    public static IReadOnlyList<int> ModCodes(MouseMods mods)
    {
        var codes = new List<int>();
        if (mods.HasFlag(MouseMods.Shift)) codes.Add(KeyTable.LeftShift);
        if (mods.HasFlag(MouseMods.Ctrl)) codes.Add(KeyTable.LeftCtrl);
        if (mods.HasFlag(MouseMods.LeftAlt)) codes.Add(KeyTable.LeftAlt);
        if (mods.HasFlag(MouseMods.RightAlt)) codes.Add(KeyTable.RightAlt);
        if (mods.HasFlag(MouseMods.Space)) codes.Add(KeyTable.Space);
        return codes;
    }
}

/// <summary>What turning a dial does.</summary>
public enum DialKind { Zoom, Scroll, BrushSize, Rotate, Custom, Disabled }

/// <summary>One of a dial's functions. Custom ones carry a shortcut for each direction.</summary>
public sealed record DialFunction
{
    public DialKind Kind { get; init; }
    public string Counterclockwise { get; init; } = "";
    public string Clockwise { get; init; } = "";
    public string Name { get; init; } = "";

    public static DialFunction Of(DialKind kind) => new() { Kind = kind };

    public string Label => Name.Length > 0 ? Name : Kind switch
    {
        DialKind.Zoom => "Zoom",
        DialKind.Scroll => "Scroll",
        DialKind.BrushSize => "Brush size",
        DialKind.Rotate => "Rotate",
        DialKind.Custom => Turns() is (var back, var forward) ? $"{back} / {forward}" : "Custom",
        _ => "Disabled",
    };

    /// <summary>The shortcut for each direction, counterclockwise first; null for scrolling, disabled or unset.</summary>
    public (KeyCombo Counterclockwise, KeyCombo Clockwise)? Turns() => Kind switch
    {
        DialKind.Zoom => (new KeyCombo(KeyMods.Ctrl, "-"), new KeyCombo(KeyMods.Ctrl, "=")),
        DialKind.BrushSize => (new KeyCombo(KeyMods.None, "["), new KeyCombo(KeyMods.None, "]")),
        DialKind.Rotate => (new KeyCombo(KeyMods.Ctrl, "["), new KeyCombo(KeyMods.Ctrl, "]")),
        DialKind.Custom when KeyCombo.Parse(Counterclockwise) is { } back && KeyCombo.Parse(Clockwise) is { } forward => (back, forward),
        _ => null,
    };
}
