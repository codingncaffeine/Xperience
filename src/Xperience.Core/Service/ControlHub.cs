using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Input;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.Core.Service;

/// <summary>What the hub asks the windows to do.</summary>
public interface IHubUi
{
    void ShowWindow();

    /// <summary>Shows the window, or puts it away when it is the one in front: what a key set to show it does.</summary>
    void ToggleWindow();
    void ToggleFloatingMenu();
}

/// <summary>
/// Holds the settings and makes them real. It keeps the desktop's bindings in step with the active profile,
/// and does itself what the desktop cannot: runs of shortcuts, starting programs, switching a dial's function,
/// precision mode and the rest. Everything it does runs one thing at a time, in the order it was asked.
/// </summary>
public sealed class ControlHub : IDisposable
{
    /// <summary>How much finer the pen moves in precision mode.</summary>
    public const double PrecisionFactor = 3;

    readonly ICompositor? _compositor;
    readonly ConfigStore _store;
    readonly IInjector _injector;
    readonly TabletModel _model;
    readonly Func<string, bool> _launch;
    readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _worker;
    readonly Lock _gate = new();
    readonly HashSet<string> _held = [];
    // The controls whose two alternating shortcuts stand at the second one.
    readonly HashSet<string> _onSecond = [];

    XperienceConfig _config;
    RunState _state;
    string _activeApp = "";
    bool _eraserNext;
    bool _watching;

    public ControlHub(ICompositor? compositor, ConfigStore store, IInjector injector, TabletModel model, Func<string, bool>? launch = null)
    {
        _compositor = compositor;
        _store = store;
        _injector = injector;
        _model = model;
        _launch = launch ?? Launch;
        _config = store.Load();
        _state = store.LoadState();
        if (compositor != null)
            compositor.ActiveAppChanged += OnActiveApp;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Where the record of what the tablet did and what was done about it goes.</summary>
    public Diagnostics.ILog Log { get; init; } = Diagnostics.NoLog.Instance;

    /// <summary>How the tablet is turned, as last read from the desktop: it decides which of its keys is the first as held.</summary>
    public TabletTurn Turn { get; private set; }

    /// <summary>Who shows windows for the hub. Set once the windows exist.</summary>
    public IHubUi? Ui { get; set; }

    /// <summary>Where the pen is on its surface right now, as shares of it, or null when that is not known.</summary>
    public Func<(double X, double Y)?>? PenPosition { get; set; }

    /// <summary>Raised on a pool thread after the settings, the active profile or a dial's function changed.</summary>
    public event Action? Changed;

    /// <summary>Raised on a pool thread when something could not be done, with words for the user.</summary>
    public event Action<string>? Failed;

    public XperienceConfig Config
    {
        get
        {
            lock (_gate)
                return _config;
        }
    }

    public RunState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>The application whose window is active, as far as the desktop has said.</summary>
    public string ActiveApp
    {
        get
        {
            lock (_gate)
                return _activeApp;
        }
    }

    public Profile ActiveProfile
    {
        get
        {
            lock (_gate)
                return _config.ProfileFor(_activeApp);
        }
    }

    /// <summary>Which function each dial of a profile is on.</summary>
    public IReadOnlyList<int> SlotsOf(Profile profile)
    {
        lock (_gate)
            return _state.SlotsFor(profile.App);
    }

    /// <summary>True when precision mode is on.</summary>
    public bool Precise => State.AreaBeforePrecision != null;

    // ---- what callers ask for; each returns when it has been done ----

    /// <summary>Brings the desktop's bindings in step with the settings as they are. Called once at start.</summary>
    public Task StartAsync() => Post(async () =>
    {
        await WatchAsync().ConfigureAwait(false);
        await ApplyBindingsAsync().ConfigureAwait(false);
    });

    /// <summary>Takes new settings: saves them, then brings the desktop's bindings in step.</summary>
    public Task UseAsync(XperienceConfig config) => Post(async () =>
    {
        var completed = config.Completed();
        lock (_gate)
            _config = completed;
        if (!_store.Save(completed))
            Failed?.Invoke($"The settings could not be saved to {_store.ConfigPath}. They apply until Xperience is closed.");
        await WatchAsync().ConfigureAwait(false);
        await ApplyBindingsAsync().ConfigureAwait(false);
        Changed?.Invoke();
    });

    /// <summary>Brings the bindings in step again, for when the tablet's devices changed.</summary>
    public Task SyncAsync() => Post(ApplyBindingsAsync);

    /// <summary>A key of the tablet went down or up; keys count from 0, by the tablet's own numbers.</summary>
    public Task KeyAsync(int own, bool down) => Post(async () =>
    {
        XperienceConfig config = Config;
        if (own < 0 || own >= Profile.KeyCount)
            return;
        int index = HeldOrder.Key(own, Turn);
        if (config.KeysDisabled)
        {
            if (down)
                Log.Write("key", string.Create(CultureInfo.InvariantCulture, $"key {index + 1} as held (the tablet's key {own + 1}) pressed: every key is switched off"));
            return;
        }
        var action = ActiveProfile.Key(index);
        bool ours = Assignments.NeedsXperience(action);
        if (down)
            Log.Write("key", string.Create(CultureInfo.InvariantCulture,
                $"key {index + 1} as held (the tablet's key {own + 1}) pressed: {action.Describe()}, carried out by {(ours ? "Xperience" : action.Kind == ActionKind.Disabled ? "nobody" : "the desktop")}"));
        if (down && config.Hints && action.Kind != ActionKind.Disabled && !IsDialSwitch(action))
            await HintAsync(action.Label).ConfigureAwait(false);
        if (ours)
            await ExecuteAsync("key" + index.ToString(CultureInfo.InvariantCulture), action, down).ConfigureAwait(false);
    });

    /// <summary>A pen button went down or up.</summary>
    public Task PenButtonAsync(bool upper, bool down) => Post(async () =>
    {
        var profile = ActiveProfile;
        var action = upper ? profile.PenUpper : profile.PenLower;
        bool ours = Assignments.NeedsXperience(action);
        if (down)
            Log.Write("key", $"{(upper ? "upper" : "lower")} pen button pressed: {action.Describe()}, carried out by {(ours ? "Xperience" : "the desktop")}");
        if (ours)
            await ExecuteAsync(upper ? "pen-upper" : "pen-lower", action, down).ConfigureAwait(false);
    });

    /// <summary>Carries an action out once, whoever would usually do it. The floating menu's buttons use this.</summary>
    public Task RunAsync(PadAction action) => Post(async () =>
    {
        Log.Write("key", $"floating menu button: {action.Describe()}");
        await ExecuteAsync("menu", action, down: true).ConfigureAwait(false);
        await ExecuteAsync("menu", action, down: false).ConfigureAwait(false);
    });

    /// <summary>Gets the virtual keyboard ready ahead of its first use, which otherwise comes a moment late.</summary>
    public Task PrepareTypingAsync() => Post(() =>
    {
        if (!_injector.Prepare())
            Failed?.Invoke("Xperience may not create its virtual keyboard on this account, so it cannot type shortcuts itself. Installing the driver from Settings grants that.");
        return Task.CompletedTask;
    });

    /// <summary>Forgets precision mode without putting the old area back, because the area was just set by hand.</summary>
    public Task ForgetPrecisionAsync() => Post(() =>
    {
        SetState(s => s with { AreaBeforePrecision = null });
        return Task.CompletedTask;
    });

    /// <summary>Changes what is remembered between runs, such as where the floating menu sits.</summary>
    public Task RememberAsync(Func<RunState, RunState> change) => Post(() =>
    {
        SetState(change);
        return Task.CompletedTask;
    });

    // ---- the worker ----

    Task Post(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _work.Writer.TryWrite(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Whatever went wrong with one request, the next one still has to be served.
                Log.Write("error", $"{e.GetType().Name}: {e.Message}");
                Failed?.Invoke(e.Message);
            }
            finally
            {
                done.TrySetResult();
            }
        });
        if (!queued)
            done.TrySetResult();
        return done.Task;
    }

    async Task WorkAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
            await work().ConfigureAwait(false);
    }

    void OnActiveApp(string app) => Post(async () =>
    {
        // Xperience's own window says nothing about what the tablet is being used with.
        if (app.Length == 0 || app.Equals("xperience", StringComparison.OrdinalIgnoreCase))
            return;
        Profile before, after;
        lock (_gate)
        {
            before = _config.ProfileFor(_activeApp);
            _activeApp = app;
            after = _config.ProfileFor(app);
        }
        if (!ReferenceEquals(before, after))
        {
            await ApplyBindingsAsync().ConfigureAwait(false);
            Changed?.Invoke();
        }
    });

    /// <summary>The desktop reports the active application only while some application has a profile of its own.</summary>
    async Task WatchAsync()
    {
        if (_compositor == null)
            return;
        bool wanted = Config.Profiles.Count > 1;
        if (wanted == _watching)
            return;
        _watching = wanted && await _compositor.WatchActiveAppAsync(true).ConfigureAwait(false);
        if (!wanted)
        {
            await _compositor.WatchActiveAppAsync(false).ConfigureAwait(false);
            lock (_gate)
                _activeApp = "";
        }
    }

    async Task ApplyBindingsAsync()
    {
        if (_compositor == null)
            return;
        var snapshot = await _compositor.ReadAsync(_model).ConfigureAwait(false);
        var profile = ActiveProfile;
        var own = State.OwnBindings;
        Turn = snapshot.Pen?.Turn ?? TabletTurn.None;
        // "No binding" is only enforced where Xperience put one: a pen button the user bound in the desktop's
        // own settings and never touched here stays as they bound it.
        var desired = Assignments.Desired(Config, profile, SlotsOf(profile), snapshot.Devices, Turn).Where(d => d.Value.Length > 0 || own.Contains(d.Id)).ToList();
        var changes = Assignments.Changes(await _compositor.ReadRebindsAsync().ConfigureAwait(false), desired);
        if (changes.Count > 0)
        {
            foreach (var change in changes)
                Log.Write("binding", $"{change.Group} {change.Key} on {change.Device}: {(change.Value.Length > 0 ? change.Value : "removed")}  (profile {(profile.IsDefault ? "for all applications" : profile.Name)})");
            await _compositor.WriteRebindsAsync(changes).ConfigureAwait(false);
        }
        var owned = desired.Where(d => d.Value.Length > 0).Select(d => d.Id).ToHashSet();
        if (!owned.SetEquals(own))
            SetState(s => s with { OwnBindings = owned });
        if (NeedsInjector(Config))
            _injector.Prepare();
    }

    /// <summary>True when some assignment has Xperience itself type or click.</summary>
    static bool NeedsInjector(XperienceConfig config) =>
        config.Profiles.SelectMany(p => p.Keys.Append(p.PenLower).Append(p.PenUpper))
            .Any(a => Assignments.NeedsXperience(a) && (a.Kind is ActionKind.Shortcut or ActionKind.Mouse or ActionKind.Alternate || a is { Kind: ActionKind.Special, Special: SpecialAct.BrushEraser }));

    static bool IsDialSwitch(PadAction action) => action is { Kind: ActionKind.Special, Special: SpecialAct.SwitchDial1 or SpecialAct.SwitchDial2 };

    Task HintAsync(string text) => _compositor?.ShowHintAsync(text) ?? Task.CompletedTask;

    void SetState(Func<RunState, RunState> change)
    {
        RunState state;
        lock (_gate)
            state = _state = change(_state);
        _store.SaveState(state);
    }

    // ---- carrying actions out ----

    async Task ExecuteAsync(string source, PadAction action, bool down)
    {
        switch (action.Kind)
        {
            case ActionKind.Shortcut:
                var combos = action.Combos;
                if (combos.Count == 1)
                    Hold(source, combos[0].Codes(), down);
                else if (down)
                {
                    foreach (var combo in combos)
                    {
                        Tap(combo.Codes());
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                }
                break;
            case ActionKind.Alternate:
                if (action.Combos is [var first, var second])
                {
                    // Held like a single shortcut; letting go hands the turn to the other one.
                    Hold(source, (_onSecond.Contains(source) ? second : first).Codes(), down);
                    if (!down && !_onSecond.Remove(source))
                        _onSecond.Add(source);
                }
                break;
            case ActionKind.Mouse:
                await MouseAsync(source, action, down).ConfigureAwait(false);
                break;
            case ActionKind.RunApp when down:
                if (action.App.Trim().Length > 0 && !_launch(action.App))
                    Failed?.Invoke($"{action.App} could not be started.");
                break;
            case ActionKind.Special when down:
                await SpecialAsync(action.Special).ConfigureAwait(false);
                break;
        }
    }

    async Task MouseAsync(string source, PadAction action, bool down)
    {
        var mods = PadAction.ModCodes(action.MouseMods);
        switch (action.Mouse)
        {
            case MouseAct.LeftClick:
                Hold(source, [.. mods, KernelInput.BtnLeft], down);
                break;
            case MouseAct.MiddleClick:
                Hold(source, [.. mods, KernelInput.BtnMiddle], down);
                break;
            case MouseAct.RightClick:
                Hold(source, [.. mods, KernelInput.BtnRight], down);
                break;
            case MouseAct.DoubleClick when down:
                if (!_injector.Prepare())
                    break;
                Press(mods, true);
                for (int i = 0; i < 2; i++)
                {
                    _injector.Key(KernelInput.BtnLeft, true);
                    _injector.Key(KernelInput.BtnLeft, false);
                    await Task.Delay(40).ConfigureAwait(false);
                }
                Press(mods, false);
                break;
            case MouseAct.ScrollUp or MouseAct.ScrollDown when down:
                if (!_injector.Prepare())
                    break;
                Press(mods, true);
                _injector.Wheel(action.Mouse == MouseAct.ScrollUp ? 1 : -1);
                Press(mods, false);
                break;
        }
    }

    /// <summary>Holds keys while a control is held: down in order, up in reverse, and never up without a down.</summary>
    void Hold(string source, IReadOnlyList<int> codes, bool down)
    {
        if (down)
        {
            if (!_injector.Prepare() || !_held.Add(source))
                return;
            Press(codes, true);
        }
        else if (_held.Remove(source))
            Press(codes, false);
    }

    void Tap(IReadOnlyList<int> codes)
    {
        if (!_injector.Prepare())
            return;
        Press(codes, true);
        Press(codes, false);
    }

    void Press(IReadOnlyList<int> codes, bool down)
    {
        if (down)
        {
            foreach (int code in codes)
                _injector.Key(code, true);
        }
        else
        {
            for (int i = codes.Count - 1; i >= 0; i--)
                _injector.Key(codes[i], false);
        }
    }

    async Task SpecialAsync(SpecialAct act)
    {
        switch (act)
        {
            case SpecialAct.ShowXperience:
                Ui?.ToggleWindow();
                break;
            case SpecialAct.FloatingMenu:
                Ui?.ToggleFloatingMenu();
                break;
            case SpecialAct.BrushEraser:
                Tap(new KeyCombo(KeyMods.None, _eraserNext ? "E" : "B").Codes());
                _eraserNext = !_eraserNext;
                break;
            case SpecialAct.SwitchDial1:
                await SwitchDialAsync(0).ConfigureAwait(false);
                break;
            case SpecialAct.SwitchDial2:
                await SwitchDialAsync(1).ConfigureAwait(false);
                break;
            case SpecialAct.SwitchMonitor:
                await SwitchMonitorAsync().ConfigureAwait(false);
                break;
            case SpecialAct.PrecisionMode:
                await TogglePrecisionAsync().ConfigureAwait(false);
                break;
            case SpecialAct.ShowDesktop:
                if (_compositor != null)
                    await _compositor.ToggleShowDesktopAsync().ConfigureAwait(false);
                break;
            case SpecialAct.ScreenKeyboard:
                if (_compositor != null)
                    await _compositor.ToggleScreenKeyboardAsync().ConfigureAwait(false);
                break;
        }
    }

    async Task SwitchDialAsync(int dial)
    {
        var profile = ActiveProfile;
        int[] slots = [.. SlotsOf(profile)];
        slots[dial] = Assignments.NextSlot(profile, dial, slots[dial]);
        SetState(s => s with { DialSlots = new Dictionary<string, int[]>(s.DialSlots) { [profile.App] = slots } });
        await ApplyBindingsAsync().ConfigureAwait(false);
        Log.Write("key", string.Create(CultureInfo.InvariantCulture, $"dial {dial + 1} as held is now on: {profile.Dial(dial, slots[dial]).Label}"));
        if (Config.Hints)
            await HintAsync(string.Create(CultureInfo.InvariantCulture, $"Dial {dial + 1}: {profile.Dial(dial, slots[dial]).Label}")).ConfigureAwait(false);
        Changed?.Invoke();
    }

    async Task SwitchMonitorAsync()
    {
        if (_compositor == null)
            return;
        var snapshot = await _compositor.ReadAsync(_model).ConfigureAwait(false);
        if (snapshot.Pen is not { } pen || snapshot.Screens.Count == 0)
            return;
        // Each monitor in turn, then all of them together when there is more than one, then round again.
        var screens = snapshot.Screens.OrderBy(s => s.Number).ToList();
        int current = pen.TargetKind == ScreenTargetKind.OneScreen ? screens.FindIndex(s => s.Name == pen.TargetScreen) : -1;
        bool all = screens.Count > 1 && pen.TargetKind == ScreenTargetKind.OneScreen && current == screens.Count - 1;
        var next = screens[all ? 0 : (current + 1) % screens.Count];
        if (all)
            await _compositor.SetTargetAsync(pen.Id, ScreenTargetKind.AllScreens, "").ConfigureAwait(false);
        else
            await _compositor.SetTargetAsync(pen.Id, ScreenTargetKind.OneScreen, next.Name).ConfigureAwait(false);
        await _compositor.SetScreenAreaAsync(pen.Id, RectD.Full).ConfigureAwait(false);
        SetState(s => s with { AreaBeforePrecision = null });
        Log.Write("key", all ? "switch monitor: the pen is on all monitors" : string.Create(CultureInfo.InvariantCulture, $"switch monitor: the pen is on monitor {next.Number} ({next.Name})"));
        if (Config.Hints)
            await HintAsync(all ? "Pen on all monitors" : string.Create(CultureInfo.InvariantCulture, $"Pen on monitor {next.Number}")).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Precision mode maps the pen's whole surface onto a third of the screen area, placed so the pointer
    /// stays where it is. Leaving it puts the earlier area back.
    /// </summary>
    async Task TogglePrecisionAsync()
    {
        if (_compositor == null)
            return;
        var snapshot = await _compositor.ReadAsync(_model).ConfigureAwait(false);
        if (snapshot.Pen is not { CanSetScreenArea: true } pen)
            return;
        if (State.AreaBeforePrecision is { } earlier)
        {
            await _compositor.SetScreenAreaAsync(pen.Id, earlier).ConfigureAwait(false);
            SetState(s => s with { AreaBeforePrecision = null });
        }
        else
        {
            var (x, y) = PenPosition?.Invoke() ?? (0.5, 0.5);
            await _compositor.SetScreenAreaAsync(pen.Id, AreaMath.Precise(pen.ScreenArea, OnSurface(pen, x, y), PrecisionFactor)).ConfigureAwait(false);
            SetState(s => s with { AreaBeforePrecision = pen.ScreenArea });
        }
        if (Config.Hints)
            await HintAsync(Precise ? "Precision mode on" : "Precision mode off").ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Where a raw pen position ends up after the desktop's flip and calibration, as shares of the mapped area.</summary>
    static (double X, double Y) OnSurface(PenState pen, double x, double y)
    {
        if (pen.Flipped)
            (x, y) = (1 - x, 1 - y);
        var (u, v) = pen.Matrix.Apply(x, y);
        return (Math.Clamp(u, 0, 1), Math.Clamp(v, 0, 1));
    }

    /// <summary>Starts a program from a command line, split on spaces with quotes respected, and lets it go.</summary>
    static bool Launch(string commandLine)
    {
        var parts = SplitCommand(commandLine);
        if (parts.Count == 0)
            return false;
        try
        {
            var start = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
            foreach (string argument in parts.Skip(1))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return process != null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> SplitCommand(string commandLine)
    {
        var parts = new List<string>();
        var part = new System.Text.StringBuilder();
        char quote = '\0';
        bool any = false;
        foreach (char c in commandLine)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    part.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                any = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (part.Length > 0 || any)
                    parts.Add(part.ToString());
                part.Clear();
                any = false;
            }
            else
                part.Append(c);
        }
        if (part.Length > 0 || any)
            parts.Add(part.ToString());
        return parts;
    }

    public void Dispose()
    {
        if (_compositor != null)
            _compositor.ActiveAppChanged -= OnActiveApp;
        _work.Writer.TryComplete();
        // Keys still held go up, so nothing stays pressed after Xperience is gone.
        _injector.Dispose();
    }
}
