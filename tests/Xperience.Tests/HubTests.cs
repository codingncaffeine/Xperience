using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Service;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The hub against a desktop held in memory: what it asks the desktop to bind, and what it does itself.</summary>
public class HubTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));
    readonly DemoCompositor _desktop = DemoCompositor.Sample();
    readonly RecordingInjector _injector = new();
    readonly List<string> _launched = [], _shown = [], _failures = [];
    ControlHub? _hub;

    ConfigStore Store => new(Path.Combine(_folder, "config"), Path.Combine(_folder, "state"));

    async Task<ControlHub> StartAsync(XperienceConfig? config = null, bool driven = true)
    {
        if (config != null)
            Store.Save(config);
        if (driven)
            _desktop.SetPad(Bench.PadName);
        _hub = new ControlHub(_desktop, Store, _injector, TabletModel.Artist16Third, app =>
        {
            _launched.Add(app);
            return !app.Contains("missing");
        }) { Ui = new Ui(_shown) };
        _hub.Failed += _failures.Add;
        await _hub.StartAsync();
        return _hub;
    }

    sealed class Ui(List<string> shown) : IHubUi
    {
        public void ShowWindow() => shown.Add("window");
        public void ToggleFloatingMenu() => shown.Add("menu");
    }

    async Task<PenState> PenAsync() => (await _desktop.ReadAsync(TabletModel.Artist16Third, TestContext.Current.CancellationToken)).Pen!;

    static XperienceConfig With(int key, PadAction action) => XperienceConfig.Default.Completed().Change("", p => p.WithKey(key, action));

    public void Dispose()
    {
        _hub?.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task With_real_keys_the_default_bindings_are_written_once()
    {
        var hub = await StartAsync();

        Assert.Equal(10, _desktop.Writes.Count);
        Assert.All(_desktop.Writes, w => Assert.StartsWith("bind ", w));
        Assert.Equal("Key,Ctrl+Z", _desktop.Binding(Rebind.Keys, "7"));
        Assert.Equal("Disabled", _desktop.Binding(Rebind.Keys, "0"));
        Assert.Equal("AxisKey,Ctrl+=,Ctrl+-,1", _desktop.Binding(Rebind.Dials, "0"));
        Assert.Equal("", _desktop.Binding(Rebind.PenButtons, "331"));

        await hub.SyncAsync();
        await hub.StartAsync();
        Assert.Equal(10, _desktop.Writes.Count);
        // Nothing here needs Xperience to type, so no virtual keyboard was asked for.
        Assert.Equal(0, _injector.Prepared);
    }

    [Fact]
    public async Task A_pen_button_bound_in_the_desktops_own_settings_is_left_alone_until_it_is_assigned_here()
    {
        // The user bound the lower button to a right click in the desktop's tablet settings, before Xperience.
        await _desktop.WriteRebindsAsync([new Rebind(Rebind.PenButtons, "XPPen Artist 16 3rd", "331", "MouseButton,273")], TestContext.Current.CancellationToken);
        _desktop.Writes.Clear();

        var hub = await StartAsync(driven: false);
        Assert.Empty(_desktop.Writes);
        Assert.Equal("MouseButton,273", _desktop.Binding(Rebind.PenButtons, "331"));

        // Assigned here, it is Xperience's to set and, back on "pen button", to take away again.
        await hub.UseAsync(hub.Config.Change("", p => p with { PenLower = PadAction.Shortcut("E") }));
        Assert.Equal("Key,E", _desktop.Binding(Rebind.PenButtons, "331"));
        await hub.UseAsync(hub.Config.Change("", p => p with { PenLower = PadAction.PenButton }));
        Assert.Equal("", _desktop.Binding(Rebind.PenButtons, "331"));

        // That is remembered across a restart, and nothing is written twice.
        hub.Dispose();
        int writes = _desktop.Writes.Count;
        await StartAsync(driven: false);
        Assert.Equal(writes, _desktop.Writes.Count);
    }

    [Fact]
    public async Task In_built_in_mode_nothing_is_written()
    {
        await StartAsync(driven: false);
        Assert.Empty(_desktop.Writes);
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task A_key_the_desktop_handles_gets_a_hint_and_nothing_typed()
    {
        var hub = await StartAsync();

        await hub.KeyAsync(7, true);
        await hub.KeyAsync(7, false);

        Assert.Equal(["Ctrl+Z"], _desktop.Hints);
        Assert.Empty(_injector.Typed);
    }

    [Fact]
    public async Task Hints_can_be_switched_off()
    {
        var hub = await StartAsync(XperienceConfig.Default with { Hints = false });
        await hub.KeyAsync(7, true);
        await hub.KeyAsync(3, true);
        Assert.Empty(_desktop.Hints);
    }

    [Fact]
    public async Task The_show_key_asks_for_the_window()
    {
        var hub = await StartAsync();
        await hub.KeyAsync(0, true);
        await hub.KeyAsync(0, false);
        Assert.Equal(["window"], _shown);
        Assert.DoesNotContain(_desktop.Writes, w => !w.StartsWith("bind ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_switch_key_steps_the_dial_rebinds_it_says_so_and_remembers()
    {
        var hub = await StartAsync();

        await hub.KeyAsync(3, true);
        await hub.KeyAsync(3, false);

        Assert.Equal("Scroll", _desktop.Binding(Rebind.Dials, "0"));
        Assert.Equal("AxisKey,],[,1", _desktop.Binding(Rebind.Dials, "1"));
        Assert.Equal(["Dial 1: Scroll"], _desktop.Hints);
        Assert.Equal([1, 0], hub.SlotsOf(hub.ActiveProfile));

        await hub.KeyAsync(4, true);
        Assert.Equal("AxisKey,Ctrl+=,Ctrl+-,1", _desktop.Binding(Rebind.Dials, "1"));
        Assert.Equal("Dial 2: Zoom", _desktop.Hints[^1]);

        // A new start finds the dials where they were left.
        hub.Dispose();
        var again = await StartAsync();
        Assert.Equal([1, 1], again.SlotsOf(again.ActiveProfile));
        Assert.Equal("Scroll", _desktop.Binding(Rebind.Dials, "0"));
    }

    [Fact]
    public async Task Several_shortcuts_are_typed_in_order_on_the_press_and_the_desktop_keeps_out()
    {
        var hub = await StartAsync(With(1, PadAction.Shortcut("Ctrl+C Ctrl+V")));

        Assert.Equal("Disabled", _desktop.Binding(Rebind.Keys, "1"));
        Assert.True(_injector.Prepared > 0);
        await hub.KeyAsync(1, true);
        Assert.Equal(["+29", "+46", "-46", "-29", "+29", "+47", "-47", "-29"], _injector.Typed);
        await hub.KeyAsync(1, false);
        Assert.Equal(8, _injector.Typed.Count);
    }

    [Fact]
    public async Task One_shortcut_xperience_has_to_type_is_held_while_the_key_is_held()
    {
        var hub = await StartAsync(With(2, PadAction.Shortcut("Ctrl+Alt")));

        await hub.KeyAsync(2, false);
        Assert.Empty(_injector.Typed);
        await hub.KeyAsync(2, true);
        Assert.Equal(["+29", "+56"], _injector.Typed);
        await hub.KeyAsync(2, true);
        Assert.Equal(2, _injector.Typed.Count);
        await hub.KeyAsync(2, false);
        Assert.Equal(["+29", "+56", "-56", "-29"], _injector.Typed);
    }

    [Fact]
    public async Task Mouse_actions_the_desktop_cannot_do_are_done_here()
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p
            .WithKey(0, PadAction.Click(MouseAct.DoubleClick, MouseMods.Shift))
            .WithKey(1, PadAction.Click(MouseAct.ScrollUp))
            .WithKey(2, PadAction.Click(MouseAct.LeftClick, MouseMods.Space))
            .WithKey(5, PadAction.Click(MouseAct.RightClick)));
        var hub = await StartAsync(config);

        await hub.KeyAsync(0, true);
        await hub.KeyAsync(0, false);
        Assert.Equal(["+42", "+272", "-272", "+272", "-272", "-42"], _injector.Typed);

        _injector.Typed.Clear();
        await hub.KeyAsync(1, true);
        await hub.KeyAsync(1, false);
        Assert.Equal(["wheel 1"], _injector.Typed);

        _injector.Typed.Clear();
        await hub.KeyAsync(2, true);
        Assert.Equal(["+57", "+272"], _injector.Typed);
        await hub.KeyAsync(2, false);
        Assert.Equal(["+57", "+272", "-272", "-57"], _injector.Typed);

        // A plain right click is the desktop's own.
        _injector.Typed.Clear();
        Assert.Equal("MouseButton,273", _desktop.Binding(Rebind.Keys, "5"));
        await hub.KeyAsync(5, true);
        await hub.KeyAsync(5, false);
        Assert.Empty(_injector.Typed);
    }

    [Fact]
    public async Task A_program_is_started_and_one_that_cannot_be_is_reported()
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p.WithKey(5, PadAction.Run("krita --nosplash")).WithKey(6, PadAction.Run("/opt/missing")));
        var hub = await StartAsync(config);

        await hub.KeyAsync(5, true);
        await hub.KeyAsync(5, false);
        Assert.Equal(["krita --nosplash"], _launched);
        Assert.Empty(_failures);

        await hub.KeyAsync(6, true);
        Assert.Contains("/opt/missing could not be started", Assert.Single(_failures));
    }

    [Fact]
    public async Task Brush_and_eraser_take_turns()
    {
        var hub = await StartAsync(With(1, PadAction.Do(SpecialAct.BrushEraser)));
        for (int i = 0; i < 3; i++)
        {
            await hub.KeyAsync(1, true);
            await hub.KeyAsync(1, false);
        }
        Assert.Equal(["+48", "-48", "+18", "-18", "+48", "-48"], _injector.Typed);
    }

    [Fact]
    public async Task Switched_off_keys_do_nothing_at_all()
    {
        var hub = await StartAsync(XperienceConfig.Default with { KeysDisabled = true });

        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal("Disabled", _desktop.Binding(Rebind.Keys, i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal("Disabled", _desktop.Binding(Rebind.Dials, "0"));
        await hub.KeyAsync(0, true);
        await hub.KeyAsync(3, true);
        Assert.Empty(_shown);
        Assert.Empty(_desktop.Hints);
        Assert.Equal([0, 0], hub.SlotsOf(hub.ActiveProfile));
    }

    [Fact]
    public async Task Precision_mode_on_a_pen_button_narrows_the_area_around_the_pen_and_puts_it_back()
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p with { PenLower = PadAction.Do(SpecialAct.PrecisionMode) });
        var hub = await StartAsync(config, driven: false);
        // The pen is a quarter of the way across its surface; the desktop flips it, so on screen it is three quarters across.
        hub.PenPosition = () => (0.25, 0.5);

        Assert.Equal("Disabled", _desktop.Binding(Rebind.PenButtons, "331"));
        Assert.Equal("", _desktop.Binding(Rebind.PenButtons, "332"));

        await hub.PenButtonAsync(upper: false, down: true);
        await hub.PenButtonAsync(upper: false, down: false);
        var area = (await PenAsync()).ScreenArea;
        Assert.True(area.IsCloseTo(new RectD(0.5, 1 / 3.0, 1 / 3.0, 1 / 3.0)), area.ToString());
        Assert.True(hub.Precise);
        Assert.Equal("Precision mode on", _desktop.Hints[^1]);
        // The point under the pen did not move: three quarters across, half way down.
        Assert.Equal(0.75, area.X + 0.75 * area.Width, 6);
        Assert.Equal(0.5, area.Y + 0.5 * area.Height, 6);

        await hub.PenButtonAsync(upper: false, down: true);
        Assert.Equal(RectD.Full, (await PenAsync()).ScreenArea);
        Assert.False(hub.Precise);

        // The upper button is still a plain pen button: nothing happens here for it.
        int writes = _desktop.Writes.Count;
        await hub.PenButtonAsync(upper: true, down: true);
        Assert.Equal(writes, _desktop.Writes.Count);
    }

    [Fact]
    public async Task An_application_with_a_profile_gets_its_bindings_while_it_is_active()
    {
        var config = XperienceConfig.Default.Completed();
        config = config with { Profiles = [.. config.Profiles, config.DefaultProfile with { App = "krita", Name = "Krita" }] };
        config = config.Change("krita", p => p.WithKey(7, PadAction.Shortcut("Ctrl+Shift+Z")));
        var hub = await StartAsync(config);
        Assert.True(_desktop.WatchingActiveApp);
        Assert.Equal("Key,Ctrl+Z", _desktop.Binding(Rebind.Keys, "7"));

        _desktop.Activate("krita");
        await hub.SyncAsync();
        Assert.Equal("Key,Ctrl+Shift+Z", _desktop.Binding(Rebind.Keys, "7"));
        Assert.Equal("Krita", hub.ActiveProfile.Name);

        // Xperience's own window says nothing about what is being drawn in.
        _desktop.Activate("xperience");
        await hub.SyncAsync();
        Assert.Equal("Krita", hub.ActiveProfile.Name);

        _desktop.Activate("firefox");
        await hub.SyncAsync();
        Assert.Equal("Key,Ctrl+Z", _desktop.Binding(Rebind.Keys, "7"));
        Assert.True(hub.ActiveProfile.IsDefault);

        // With the last application profile gone the desktop is no longer asked.
        await hub.UseAsync(hub.Config with { Profiles = [hub.Config.DefaultProfile] });
        Assert.False(_desktop.WatchingActiveApp);
    }

    [Fact]
    public async Task Switch_monitor_moves_the_pen_to_the_next_one_whole()
    {
        var hub = await StartAsync(With(1, PadAction.Do(SpecialAct.SwitchMonitor)));
        await hub.KeyAsync(1, true);
        var pen = (await PenAsync());
        Assert.Equal((ScreenTargetKind.OneScreen, "DP-3", RectD.Full), (pen.TargetKind, pen.TargetScreen, pen.ScreenArea));
        Assert.Equal("Pen on monitor 2", _desktop.Hints[^1]);
    }

    [Fact]
    public async Task The_floating_menu_has_xperience_type_even_what_the_desktop_could()
    {
        var hub = await StartAsync();
        await hub.RunAsync(PadAction.Shortcut("Ctrl+S"));
        Assert.Equal(["+29", "+31", "-31", "-29"], _injector.Typed);
        await hub.RunAsync(PadAction.Do(SpecialAct.FloatingMenu));
        Assert.Equal(["menu"], _shown);
    }

    [Fact]
    public async Task Without_leave_to_make_a_keyboard_the_hub_says_so_and_types_nothing()
    {
        _injector.Allowed = false;
        var hub = await StartAsync();
        await hub.PrepareTypingAsync();
        Assert.Contains("virtual keyboard", Assert.Single(_failures));
        await hub.RunAsync(PadAction.Shortcut("Ctrl+S"));
        Assert.Empty(_injector.Typed);
    }

    [Fact]
    public async Task New_settings_are_saved_applied_and_announced()
    {
        var hub = await StartAsync();
        int announced = 0;
        hub.Changed += () => announced++;

        await hub.UseAsync(hub.Config.Change("", p => p.WithKey(1, PadAction.Shortcut("E"))));

        Assert.Equal("Key,E", _desktop.Binding(Rebind.Keys, "1"));
        Assert.Equal(1, announced);
        Assert.Equal("E", Store.Load().DefaultProfile.Key(1).Label);
        Assert.Empty(_failures);
    }

    [Theory]
    [InlineData("krita", new[] { "krita" })]
    [InlineData("  /usr/bin/krita   --nosplash ", new[] { "/usr/bin/krita", "--nosplash" })]
    [InlineData("\"/opt/My App/run\" --file 'a b.kra' \"\"", new[] { "/opt/My App/run", "--file", "a b.kra", "" })]
    [InlineData("", new string[0])]
    public void A_command_line_splits_on_spaces_outside_quotes(string line, string[] parts)
    {
        Assert.Equal(parts, ControlHub.SplitCommand(line));
    }
}

/// <summary>One running copy per account, and always the build that was started last.</summary>
public class InstanceLinkTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));

    string Socket => Path.Combine(_folder, "run", "instance.sock");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void With_no_copy_running_nobody_answers()
    {
        Assert.Equal(App.Handover.Nobody, App.InstanceLink.Send(Socket, "show", "build-a"));
        Assert.True(App.InstanceLink.WaitUntilFree(Socket, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_second_start_of_the_same_build_hands_its_word_over()
    {
        var words = new System.Collections.Concurrent.BlockingCollection<string>();
        int replaced = 0;
        using var link = App.InstanceLink.Listen(Socket, "build-a", words.Add, () => replaced++);
        Assert.NotNull(link);

        Assert.Equal(App.Handover.Taken, await Task.Run(() => App.InstanceLink.Send(Socket, "show", "build-a"), TestContext.Current.CancellationToken));
        Assert.Equal(App.Handover.Taken, await Task.Run(() => App.InstanceLink.Send(Socket, "hello", "build-a"), TestContext.Current.CancellationToken));

        Assert.True(words.TryTake(out string? first, 2000, TestContext.Current.CancellationToken));
        Assert.True(words.TryTake(out string? second, 2000, TestContext.Current.CancellationToken));
        Assert.Equal(["show", "hello"], [first, second]);
        Assert.Equal(0, replaced);
    }

    [Fact]
    public async Task A_start_of_another_build_takes_the_running_copys_place()
    {
        var words = new System.Collections.Concurrent.BlockingCollection<string>();
        var replaced = new TaskCompletionSource();
        var link = App.InstanceLink.Listen(Socket, "build-a", words.Add, () => replaced.TrySetResult());

        var answer = await Task.Run(() => App.InstanceLink.Send(Socket, "show", "build-b"), TestContext.Current.CancellationToken);

        Assert.Equal(App.Handover.Replacing, answer);
        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Empty(words);
        // While the old copy is still there the socket is not free; once it has ended, it is.
        Assert.False(App.InstanceLink.WaitUntilFree(Socket, TimeSpan.FromMilliseconds(300)));
        link!.Dispose();
        Assert.True(App.InstanceLink.WaitUntilFree(Socket, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_socket_file_left_behind_by_a_copy_that_died_is_taken_over()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Socket)!);
        File.WriteAllText(Socket, "stale");
        Assert.Equal(App.Handover.Nobody, App.InstanceLink.Send(Socket, "show", "build-a"));

        var words = new System.Collections.Concurrent.BlockingCollection<string>();
        using var link = App.InstanceLink.Listen(Socket, "build-a", words.Add, () => { });
        Assert.NotNull(link);
        Assert.Equal(App.Handover.Taken, await Task.Run(() => App.InstanceLink.Send(Socket, "show", "build-a"), TestContext.Current.CancellationToken));
    }
}
