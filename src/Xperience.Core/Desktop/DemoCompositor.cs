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
    string? _padName;
    readonly Dictionary<string, Rebind> _rebinds = [];

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
            new("DP-5", 4, new RectD(8800, 0, 1423, 800), 1.35, ScreenRotation.Half) { WidthMm = 340, HeightMm = 192, Brightness = 0.8 },
        ];
        var pen = new PenState("event29", "XPPen Artist 16 3rd", ScreenTargetKind.OneScreen, "DP-2", RectD.Full, Flipped: true,
            MouseMode: false, PressureSettings.Default, CanFlip: true, CanSetScreenArea: true, CanSetPressureRange: true)
        {
            CanSetMatrix = true,
            CanSetMouseSpeed = true,
        };
        return new DemoCompositor(screens, pen);
    }

    public string Name => "Demo desktop";

    public event Action? Changed;
    public event Action<string>? ActiveAppChanged;

    /// <summary>Every write, in order, for tests to read.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Every hint shown, in order.</summary>
    public List<string> Hints { get; } = [];

    /// <summary>The applications a real desktop would list as running.</summary>
    public IReadOnlyList<AppInfo> Apps { get; set; } = [new("krita", "Krita"), new("org.kde.dolphin", "Home — Dolphin"), new("firefox", "Mozilla Firefox")];

    public bool WatchingActiveApp { get; private set; }

    /// <summary>The tablet's keys and dials become real controls under this name, as when the driver is loaded.</summary>
    public void SetPad(string? name)
    {
        lock (_gate)
            _padName = name;
        Changed?.Invoke();
    }

    /// <summary>Another window becomes the active one.</summary>
    public void Activate(string app) => ActiveAppChanged?.Invoke(app);

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
            return Task.FromResult(new DesktopSnapshot(_screens, _pen) { PadName = _padName });
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

    public Task SetMatrixAsync(string penId, PenMatrix matrix, CancellationToken cancel = default) =>
        Change($"matrix {matrix.A:0.###} {matrix.B:0.###} {matrix.C:0.###} {matrix.D:0.###} {matrix.E:0.###} {matrix.F:0.###}", p => p with { Matrix = matrix });

    public Task SetMouseSpeedAsync(string penId, double speed, CancellationToken cancel = default) =>
        Change($"speed {speed:0.##}", p => p with { MouseSpeed = speed });

    public Task<IReadOnlyList<Rebind>> ReadRebindsAsync(CancellationToken cancel = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Rebind>>([.. _rebinds.Values]);
    }

    public Task WriteRebindsAsync(IReadOnlyList<Rebind> changes, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            foreach (var change in changes)
            {
                Writes.Add($"bind {change.Group} {change.Key} = {change.Value}".TrimEnd());
                if (change.Value.Length == 0)
                    _rebinds.Remove(change.Id);
                else
                    _rebinds[change.Id] = change;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>The binding the desktop holds for one control; empty when it holds none.</summary>
    public string Binding(string group, string key)
    {
        lock (_gate)
            return _rebinds.Values.FirstOrDefault(r => r.Group == group && r.Key == key)?.Value ?? "";
    }

    public Task ShowHintAsync(string text, CancellationToken cancel = default)
    {
        lock (_gate)
            Hints.Add(text);
        return Task.CompletedTask;
    }

    public Task SetScreenBrightnessAsync(string screenName, double share, CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (_screens.All(s => s.Name != screenName))
                throw new IOException($"There is no screen called {screenName}.");
            double kept = Math.Round(Math.Clamp(share, 0, 1), 2);
            _screens = [.. _screens.Select(s => s.Name == screenName ? s with { Brightness = kept } : s)];
            Writes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"brightness {screenName} {kept:0.##}"));
        }
        return Task.CompletedTask;
    }

    public Task OpenDisplaySettingsAsync(CancellationToken cancel = default)
    {
        lock (_gate)
            Writes.Add("display settings");
        return Task.CompletedTask;
    }

    public Task ToggleShowDesktopAsync(CancellationToken cancel = default)
    {
        lock (_gate)
            Writes.Add("show desktop");
        return Task.CompletedTask;
    }

    public Task ToggleScreenKeyboardAsync(CancellationToken cancel = default)
    {
        lock (_gate)
            Writes.Add("screen keyboard");
        return Task.CompletedTask;
    }

    public Task<bool> WatchActiveAppAsync(bool on, CancellationToken cancel = default)
    {
        WatchingActiveApp = on;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AppInfo>> RunningAppsAsync(CancellationToken cancel = default) => Task.FromResult(Apps);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
