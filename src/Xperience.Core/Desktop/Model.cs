namespace Xperience.Core.Desktop;

/// <summary>How a screen is turned, clockwise.</summary>
public enum ScreenRotation { None = 0, Quarter = 90, Half = 180, ThreeQuarter = 270 }

/// <summary>One screen of the desktop. Bounds are in the desktop's own coordinates, after scaling and rotation.</summary>
public sealed record ScreenInfo(string Name, int Number, RectD Bounds, double Scale, ScreenRotation Rotation)
{
    public bool IsPrimary => Number == 1;

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
    bool CanSetPressureRange);

/// <summary>One reading of the desktop: its screens and, when the tablet is connected, its pen.</summary>
public sealed record DesktopSnapshot(IReadOnlyList<ScreenInfo> Screens, PenState? Pen)
{
    public static readonly DesktopSnapshot Empty = new([], null);

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
}
