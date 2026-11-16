namespace Xperience.Core.Desktop;

/// <summary>
/// A desktop held in memory. It stands in for the real one in tests and screenshots, and behaves like it:
/// what is written is what the next read returns.
/// </summary>
public sealed class DemoCompositor : ICompositor
{
    readonly Lock _gate = new();
    IReadOnlyList<ScreenInfo> _screens;
    PenState? _pen;

    public DemoCompositor(IReadOnlyList<ScreenInfo> screens, PenState? pen)
    {
        _screens = screens;
        _pen = pen;
    }

    /// <summary>Four screens and a connected tablet whose pen follows the active screen, as a desktop has it at first.</summary>
    public static DemoCompositor Sample()
    {
        IReadOnlyList<ScreenInfo> screens =
        [
            new("DP-2", 1, new RectD(0, 967, 3440, 1440), 1, ScreenRotation.None),
            new("DP-3", 2, new RectD(3440, 967, 3440, 1440), 1, ScreenRotation.None),
            new("HDMI-A-2", 3, new RectD(6880, 0, 1920, 1080), 1, ScreenRotation.None),
            new("DP-5", 4, new RectD(8800, 0, 1423, 800), 1.35, ScreenRotation.Half),
        ];
        var pen = new PenState("event29", "XPPen Artist 16 3rd", ScreenTargetKind.OneScreen, "DP-2", RectD.Full, Flipped: true,
            MouseMode: false, PressureSettings.Default, CanFlip: true, CanSetScreenArea: true, CanSetPressureRange: true);
        return new DemoCompositor(screens, pen);
    }

    public string Name => "Demo desktop";

    public event Action? Changed;

    /// <summary>Every write, in order, for tests to read.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Unplugs or plugs the tablet.</summary>
    public void SetPen(PenState? pen)
    {
        lock (_gate)
            _pen = pen;
        Changed?.Invoke();
    }

    public void SetScreens(IReadOnlyList<ScreenInfo> screens)
    {
        lock (_gate)
            _screens = screens;
        Changed?.Invoke();
    }

    public Task<DesktopSnapshot> ReadAsync(Tablets.TabletModel tablet, CancellationToken cancel = default)
    {
        lock (_gate)
            return Task.FromResult(new DesktopSnapshot(_screens, _pen));
    }

    Task Change(string note, Func<PenState, PenState> change)
    {
        lock (_gate)
        {
            Writes.Add(note);
            if (_pen != null)
                _pen = change(_pen);
        }
        return Task.CompletedTask;
    }

    public Task SetTargetAsync(string penId, ScreenTargetKind kind, string screenName, CancellationToken cancel = default) =>
        Change($"target {kind} {screenName}".TrimEnd(), p => p with { TargetKind = kind, TargetScreen = kind == ScreenTargetKind.OneScreen ? screenName : "" });

    public Task SetScreenAreaAsync(string penId, RectD area, CancellationToken cancel = default) =>
        Change($"area {area.X:0.###} {area.Y:0.###} {area.Width:0.###} {area.Height:0.###}", p => p with { ScreenArea = area });

    public Task SetFlippedAsync(string penId, bool flipped, CancellationToken cancel = default) =>
        Change($"flipped {flipped}", p => p with { Flipped = flipped });

    public Task SetMouseModeAsync(string penId, bool on, CancellationToken cancel = default) =>
        Change($"mouse {on}", p => p with { MouseMode = on });

    public Task SetPressureAsync(string penId, PressureSettings pressure, CancellationToken cancel = default) =>
        Change($"pressure {pressure.Curve.ToDesktopString()} {pressure.RangeMin:0.###}-{pressure.RangeMax:0.###}", p => p with { Pressure = pressure });

    public Task IdentifyScreensAsync(CancellationToken cancel = default)
    {
        lock (_gate)
            Writes.Add("identify");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
