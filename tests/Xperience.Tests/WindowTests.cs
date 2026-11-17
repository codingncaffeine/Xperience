using Avalonia.Headless;
using Avalonia.VisualTree;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The window's state against a desktop held in memory: what it shows, and exactly what it writes.</summary>
public class WindowTests
{
    static Task OnUi(Func<Task> body) => HeadlessApp.OnUi(body);

    [Fact]
    public Task Opening_the_window_shows_the_desktops_settings_and_writes_nothing() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var (main, desktop) = (bench.Main, bench.Desktop);

        Assert.Empty(desktop.Writes);
        Assert.Equal("Monitor 1  ·  DP-2  ·  3440 × 1440", main.WorkArea.SelectedTarget?.Label);
        Assert.Equal(TabletTurn.Half, main.WorkArea.Turn);
        Assert.True(main.WorkArea.Turned);
        Assert.True(main.WorkArea.IsWholeScreen);
        Assert.False(main.WorkArea.IsSameShape);
        Assert.True(main.WorkArea.IsFullArea);
        Assert.Equal("", main.WorkArea.GeometryNote);
        Assert.True(main.Home.Turned);
        Assert.False(main.Pen.MouseMode);
        Assert.True(main.Pen.PressureOn);
        Assert.Equal(3, main.Pen.Preset);
        Assert.True(main.StatusGood);
        Assert.Null(main.Notice);
        Assert.Null(main.Problem);

        // Visiting every page changes nothing either.
        foreach (string page in (string[])["workarea", "pen", "settings", "floatingmenu", "home"])
            main.Open(page);
        main.Pen.Tick();
        main.Settings.Tick();
        await main.RefreshAsync();
        Assert.Empty(desktop.Writes);
        Assert.Empty(bench.Injector.Typed);
        Assert.Equal(0, bench.Injector.Prepared);
        Assert.False(File.Exists(bench.Store.ConfigPath));
    });

    [Fact]
    public Task Picking_a_screen_writes_it_and_shows_what_the_desktop_then_has() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var (main, desktop) = (bench.Main, bench.Desktop);

        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.ScreenName == "DP-5");
        await main.Pending;

        Assert.Equal(["target OneScreen DP-5", "area 0 0 1 1"], desktop.Writes);
        Assert.Equal(ScreenTargetKind.OneScreen, main.WorkArea.TargetKind);
        Assert.Equal("DP-5", main.WorkArea.TargetScreen);
        Assert.Equal("DP-5", (await bench.PenAsync()).TargetScreen);

        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.Kind == ScreenTargetKind.AllScreens);
        await main.Pending;
        Assert.Equal("target AllScreens", desktop.Writes[2]);
        Assert.Equal(ScreenTargetKind.AllScreens, main.WorkArea.TargetKind);

        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.Kind == ScreenTargetKind.ActiveScreen);
        await main.Pending;
        Assert.Equal("target ActiveScreen", desktop.Writes[4]);
        Assert.False(main.WorkArea.CanSetArea);
    });

    [Fact]
    public Task Match_pen_shape_maps_the_pen_to_the_middle_of_a_wide_screen() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;

        await main.WorkArea.SameShapeCommand.ExecuteAsync(null);

        var area = (await bench.PenAsync()).ScreenArea;
        double width = (341.0 / 191.8) / (3440.0 / 1440.0);
        Assert.True(area.IsCloseTo(new RectD((1 - width) / 2, 0, width, 1)), area.ToString());
        Assert.True(main.WorkArea.IsSameShape);
        Assert.False(main.WorkArea.IsWholeScreen);
        Assert.Contains("stays a circle", main.WorkArea.ShapeNote);

        await main.WorkArea.WholeScreenCommand.ExecuteAsync(null);
        Assert.True(main.WorkArea.IsWholeScreen);
        Assert.Contains("1.34", main.WorkArea.ShapeNote);
    });

    [Fact]
    public Task Turning_the_tablet_back_writes_the_flip_alone_and_turns_the_pictures() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var (main, desktop) = (bench.Main, bench.Desktop);

        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.None);
        await main.Pending;

        Assert.Equal(["flipped False"], desktop.Writes);
        Assert.False(main.WorkArea.Turned);
        Assert.False(main.Home.Turned);
        Assert.Equal(TabletTurn.None, main.WorkArea.Turn);
    });

    [Fact]
    public Task The_pressure_curve_is_written_when_a_preset_is_picked_or_a_drag_ends() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var (main, desktop) = (bench.Main, bench.Desktop);

        await main.Pen.UsePresetCommand.ExecuteAsync("0");
        Assert.Equal(0, main.Pen.Preset);
        Assert.False(main.Pen.IsDefault);
        Assert.True((await bench.PenAsync()).Pressure.Curve.IsCloseTo(PressureCurve.Presets[0]));

        // A drag in the editor changes the values first and writes once, at its end.
        int writes = desktop.Writes.Count;
        main.Pen.Curve = PressureCurve.Through(0.6, 0.3);
        main.Pen.RangeMin = 0.1;
        Assert.Equal(writes, desktop.Writes.Count);
        await main.Pen.ApplyAsync();
        var pressure = (await bench.PenAsync()).Pressure;
        Assert.Equal(0.1, pressure.RangeMin);
        Assert.True(pressure.Curve.IsCloseTo(PressureCurve.Through(0.6, 0.3)));
        Assert.Equal(-1, main.Pen.Preset);

        await main.Pen.ResetPressureCommand.ExecuteAsync(null);
        Assert.True(main.Pen.IsDefault);
        Assert.Equal(3, main.Pen.Preset);
    });

    [Fact]
    public Task Mouse_mode_is_written_when_ticked() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var (main, desktop) = (bench.Main, bench.Desktop);

        main.Pen.MouseMode = true;
        await main.Pending;

        Assert.Equal(["mouse True"], desktop.Writes);
        Assert.True((await bench.PenAsync()).MouseMode);
    });

    [Fact]
    public Task An_unplugged_tablet_disables_the_pages_and_says_so() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(TabletPresence.None);
        var (main, desktop) = (bench.Main, bench.Desktop);
        desktop.SetPen(null);
        await main.RefreshAsync();

        Assert.Equal("Not connected", main.StatusText);
        Assert.False(main.StatusGood);
        Assert.False(main.WorkArea.HasPen);
        Assert.False(main.Pen.HasPen);
        Assert.Null(main.WorkArea.SelectedTarget);

        // With no pen there is nothing to write to, whatever is clicked.
        await main.WorkArea.WholeScreenCommand.ExecuteAsync(null);
        await main.WorkArea.FullAreaCommand.ExecuteAsync(null);
        Assert.Empty(desktop.Writes);
    });

    [Fact]
    public Task The_makers_driver_running_is_called_out() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.BuiltIn with { VendorAppRunning = true });
        var main = bench.Main;

        Assert.False(main.StatusGood);
        Assert.True(main.StatusWarn);
        Assert.Contains("XPPen app is running", main.Notice);
    });

    [Fact]
    public Task Every_page_renders_without_a_binding_error() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        var window = new App.Views.MainWindow { DataContext = main };
        window.Show();
        (string Page, Type View)[] pages =
        [
            ("home", typeof(App.Views.HomeView)), ("workarea", typeof(App.Views.WorkAreaView)), ("pen", typeof(App.Views.PenView)),
            ("settings", typeof(App.Views.SettingsView)), ("floatingmenu", typeof(App.Views.FloatingMenuView)),
        ];
        foreach (var (page, view) in pages)
        {
            main.Open(page);
            // A layout pass and a render pass with every binding of the page live.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var shown = Assert.Single(window.GetVisualDescendants(), v => v.GetType() == view);
            Assert.True(shown.Bounds.Width > 300 && shown.Bounds.Height > 300, $"{page}: {shown.Bounds}");
        }
        window.Close();
    });
}
