using Avalonia;
using Avalonia.Headless;
using Xperience.App.ViewModels;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The real application, without a display.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    public static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
}

/// <summary>The window's state against a desktop held in memory: what it shows, and exactly what it writes.</summary>
public class WindowTests
{
    static readonly TabletPresence BuiltIn = new(DriveMode.BuiltIn, null, null, "/dev/null", false);

    static Task OnUi(Func<Task> body) => HeadlessApp.Session.Dispatch(body, TestContext.Current.CancellationToken);

    static async Task<(MainViewModel Main, DemoCompositor Desktop)> OpenAsync(TabletPresence? presence = null)
    {
        var desktop = DemoCompositor.Sample();
        var found = presence ?? BuiltIn;
        var main = new MainViewModel(desktop, () => found, "0.0.0", "");
        await main.StartAsync();
        return (main, desktop);
    }

    [Fact]
    public Task Opening_the_window_shows_the_desktops_settings_and_writes_nothing() => OnUi(async () =>
    {
        var (main, desktop) = await OpenAsync();
        using var _ = main;

        Assert.Empty(desktop.Writes);
        Assert.Equal("Monitor 1  ·  DP-2  ·  3440 × 1440", main.WorkArea.SelectedTarget?.Label);
        Assert.Equal(1, main.WorkArea.RotationIndex);
        Assert.True(main.WorkArea.Turned);
        Assert.True(main.WorkArea.IsWholeScreen);
        Assert.False(main.WorkArea.IsSameShape);
        Assert.True(main.Home.Turned);
        Assert.False(main.Pen.MouseMode);
        Assert.Equal(3, main.Pen.Preset);
        Assert.True(main.StatusGood);
        Assert.Null(main.Notice);

        // Visiting every page changes nothing either.
        foreach (string page in (string[])["workarea", "pen", "settings", "home"])
            main.Open(page);
        main.Pen.Tick();
        main.Settings.Tick();
        await main.RefreshAsync();
        Assert.Empty(desktop.Writes);
    });

    [Fact]
    public Task Picking_a_screen_writes_it_and_shows_what_the_desktop_then_has() => OnUi(async () =>
    {
        var (main, desktop) = await OpenAsync();
        using var _ = main;

        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.ScreenName == "DP-5");
        await main.Pending;

        Assert.Equal(["target OneScreen DP-5", "area 0 0 1 1"], desktop.Writes);
        Assert.Equal(ScreenTargetKind.OneScreen, main.WorkArea.TargetKind);
        Assert.Equal("DP-5", main.WorkArea.TargetScreen);
        Assert.Equal("DP-5", (await desktop.ReadAsync(main.Model)).Pen!.TargetScreen);

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
        var (main, desktop) = await OpenAsync();
        using var _ = main;

        await main.WorkArea.SameShapeCommand.ExecuteAsync(null);

        var area = (await desktop.ReadAsync(main.Model)).Pen!.ScreenArea;
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
    public Task Turning_the_tablet_writes_the_flip_and_turns_the_pictures() => OnUi(async () =>
    {
        var (main, desktop) = await OpenAsync();
        using var _ = main;

        main.WorkArea.RotationIndex = 0;
        await main.Pending;

        Assert.Equal(["flipped False"], desktop.Writes);
        Assert.False(main.WorkArea.Turned);
        Assert.False(main.Home.Turned);
    });

    [Fact]
    public Task The_pressure_curve_is_written_when_a_preset_is_picked_or_a_drag_ends() => OnUi(async () =>
    {
        var (main, desktop) = await OpenAsync();
        using var _ = main;

        await main.Pen.UsePresetCommand.ExecuteAsync("0");
        Assert.Equal(0, main.Pen.Preset);
        Assert.False(main.Pen.IsDefault);
        Assert.True((await desktop.ReadAsync(main.Model)).Pen!.Pressure.Curve.IsCloseTo(PressureCurve.Presets[0]));

        // A drag in the editor changes the values first and writes once, at its end.
        int writes = desktop.Writes.Count;
        main.Pen.Curve = PressureCurve.Through(0.6, 0.3);
        main.Pen.RangeMin = 0.1;
        Assert.Equal(writes, desktop.Writes.Count);
        await main.Pen.ApplyAsync();
        var pressure = (await desktop.ReadAsync(main.Model)).Pen!.Pressure;
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
        var (main, desktop) = await OpenAsync();
        using var _ = main;

        main.Pen.MouseMode = true;
        await main.Pending;

        Assert.Equal(["mouse True"], desktop.Writes);
        Assert.True((await desktop.ReadAsync(main.Model)).Pen!.MouseMode);
    });

    [Fact]
    public Task An_unplugged_tablet_disables_the_pages_and_says_so() => OnUi(async () =>
    {
        var (main, desktop) = await OpenAsync(TabletPresence.None);
        using var _ = main;
        desktop.SetPen(null);
        await main.RefreshAsync();

        Assert.Equal("Not connected", main.StatusText);
        Assert.False(main.StatusGood);
        Assert.False(main.WorkArea.HasPen);
        Assert.False(main.Pen.HasPen);
        Assert.Null(main.WorkArea.SelectedTarget);

        // With no pen there is nothing to write to, whatever is clicked.
        await main.WorkArea.WholeScreenCommand.ExecuteAsync(null);
        Assert.Empty(desktop.Writes);
    });

    [Fact]
    public Task The_makers_driver_running_is_called_out() => OnUi(async () =>
    {
        var (main, _) = await OpenAsync(BuiltIn with { VendorAppRunning = true });
        using var __ = main;

        Assert.False(main.StatusGood);
        Assert.True(main.StatusWarn);
        Assert.Contains("XPPen app is running", main.Notice);
    });

    [Fact]
    public Task Every_page_renders_without_a_binding_error() => OnUi(async () =>
    {
        var (main, _) = await OpenAsync();
        using var __ = main;
        var window = new App.Views.MainWindow { DataContext = main };
        window.Show();
        foreach (string page in (string[])["home", "workarea", "pen", "settings"])
        {
            main.Open(page);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame());
        }
        window.Close();
    });
}
