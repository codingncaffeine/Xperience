namespace Xperience.Core.Desktop;

/// <summary>How a screen is turned, clockwise.</summary>
public enum ScreenRotation { None = 0, Quarter = 90, Half = 180, ThreeQuarter = 270 }

/// <summary>One screen of the desktop. Bounds are in the desktop's own coordinates, after scaling and rotation.</summary>
public sealed record ScreenInfo(string Name, int Number, RectD Bounds, double Scale, ScreenRotation Rotation)
{
    public bool IsPrimary => Number == 1;

    /// <summary>True when the screen stands on its side, so the pen's width runs along the screen's height.</summary>
    public bool OnItsSide => Rotation is ScreenRotation.Quarter or ScreenRotation.ThreeQuarter;

    /// <summary>
    /// The shape of the screen as the pen meets it. The desktop maps the pen onto the panel as it was built and
    /// then turns it with the screen, so on a screen standing on its side the pen's width is the screen's height.
    /// </summary>
    public double PenAspect => OnItsSide ? (Bounds.Width > 0 ? Bounds.Height / Bounds.Width : 0) : Bounds.Aspect;

    /// <summary>The label the lists show: the number the desktop gives the screen, its connector and its size.</summary>
    public string Label => $"Monitor {Number}  ·  {Name}  ·  {Bounds.Width:0} × {Bounds.Height:0}";
}

/// <summary>Which part of the desktop the pen reaches.</summary>
public enum ScreenTargetKind
{
    /// <summary>Whichever screen has the active window. This is what the desktop does until told otherwise.</summary>
    ActiveScreen,
    /// <summary>One named screen.</summary>
    OneScreen,
    /// <summary>The whole desktop, every screen at once.</summary>
    AllScreens,
}

/// <summary>The pressure response the desktop applies: where output starts and ends, and how it bends between.</summary>
public sealed record PressureSettings(Pen.PressureCurve Curve, double RangeMin, double RangeMax)
{
    public static readonly PressureSettings Default = new(Pen.PressureCurve.Linear, 0, 1);
}

/// <summary>The pen as the desktop has it right now. Everything here was read from the desktop, never assumed.</summary>
public sealed record PenState(
    string Id,
    string Name,
    ScreenTargetKind TargetKind,
    string TargetScreen,
    RectD ScreenArea,
    bool Flipped,
    bool MouseMode,
    PressureSettings Pressure,
    bool CanFlip,
    bool CanSetScreenArea,
    bool CanSetPressureRange)
{
    /// <summary>The calibration transform in effect: quarter turns, the part of the surface in use and the fine correction.</summary>
    public PenMatrix Matrix { get; init; } = PenMatrix.Identity;

    public bool CanSetMatrix { get; init; }

    /// <summary>How fast the pointer moves in mouse mode, from -1 (slowest) to 1 (fastest).</summary>
    public double MouseSpeed { get; init; }

    public bool CanSetMouseSpeed { get; init; }

    /// <summary>How far the tablet is turned in all: the desktop's flip is a half turn, the transform adds quarters.</summary>
    public TabletTurn Turn => (TabletTurn)(((int)Matrix.QuarterPart() + (Flipped ? 180 : 0)) % 360);
}

/// <summary>A window's application as the desktop names it, with a title to recognize it by.</summary>
public sealed record AppInfo(string Id, string Title);

/// <summary>One reading of the desktop: its screens and, when the tablet is connected, its pen.</summary>
public sealed record DesktopSnapshot(IReadOnlyList<ScreenInfo> Screens, PenState? Pen)
{
    public static readonly DesktopSnapshot Empty = new([], null);

    /// <summary>The name the desktop files the tablet's keys and dials under; null while they are not real controls.</summary>
    public string? PadName { get; init; }

    public TabletDevices Devices => new(Pen?.Name, PadName);

    public ScreenInfo? Screen(string name) => Screens.FirstOrDefault(s => s.Name == name);
}

/// <summary>The desktop's own pen settings. Reads return what is in effect; writes go to the desktop, which keeps them.</summary>
public interface ICompositor : IAsyncDisposable
{
    /// <summary>What to call this desktop in messages.</summary>
    string Name { get; }

    /// <summary>Raised on a pool thread when a device comes or goes.</summary>
    event Action? Changed;

    Task<DesktopSnapshot> ReadAsync(Tablets.TabletModel tablet, CancellationToken cancel = default);
    Task SetTargetAsync(string penId, ScreenTargetKind kind, string screenName, CancellationToken cancel = default);
    Task SetScreenAreaAsync(string penId, RectD area, CancellationToken cancel = default);
    Task SetFlippedAsync(string penId, bool flipped, CancellationToken cancel = default);
    Task SetMouseModeAsync(string penId, bool on, CancellationToken cancel = default);
    Task SetPressureAsync(string penId, PressureSettings pressure, CancellationToken cancel = default);

    /// <summary>Shows each screen's name on that screen for a moment.</summary>
    Task IdentifyScreensAsync(CancellationToken cancel = default);

    Task SetMatrixAsync(string penId, PenMatrix matrix, CancellationToken cancel = default);
    Task SetMouseSpeedAsync(string penId, double speed, CancellationToken cancel = default);

    /// <summary>The desktop's own bindings for tablet keys, dials and pen buttons, whatever device they are for.</summary>
    Task<IReadOnlyList<Rebind>> ReadRebindsAsync(CancellationToken cancel = default);

    /// <summary>Sets each binding given; one with an empty value is removed. The desktop takes them up at once.</summary>
    Task WriteRebindsAsync(IReadOnlyList<Rebind> changes, CancellationToken cancel = default);

    /// <summary>Shows a line of text on screen for a moment, the way the desktop shows volume changes.</summary>
    Task ShowHintAsync(string text, CancellationToken cancel = default);

    /// <summary>Opens the desktop's own display settings, where brightness and color are.</summary>
    Task OpenDisplaySettingsAsync(CancellationToken cancel = default);

    Task ToggleShowDesktopAsync(CancellationToken cancel = default);
    Task ToggleScreenKeyboardAsync(CancellationToken cancel = default);

    /// <summary>Raised on a pool thread with the application of the window that just became active.</summary>
    event Action<string>? ActiveAppChanged;

    /// <summary>Starts or stops reporting the active application. False when this desktop cannot report it.</summary>
    Task<bool> WatchActiveAppAsync(bool on, CancellationToken cancel = default);

    /// <summary>The applications that have a window open right now.</summary>
    Task<IReadOnlyList<AppInfo>> RunningAppsAsync(CancellationToken cancel = default);
}
