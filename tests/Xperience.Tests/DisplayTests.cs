using Xperience.App.ViewModels;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The tablet's own screen: finding it among the monitors and setting how bright it is.</summary>
public class DisplayTests
{
    const string Layout = """
        {"outputs":[
          {"name":"DP-5","enabled":true,"connected":true,"priority":4,"pos":{"x":8800,"y":0},"size":{"width":1920,"height":1080},"scale":1.35,"rotation":4,
           "sizeMM":{"width":340,"height":192},"brightness":0.8},
          {"name":"HDMI-A-2","enabled":true,"connected":true,"priority":3,"pos":{"x":6880,"y":0},"size":{"width":1920,"height":1080},"scale":1,"rotation":1,
           "sizeMM":{"width":0,"height":0},"brightness":1},
          {"name":"DP-2","enabled":true,"connected":true,"priority":1,"pos":{"x":0,"y":0},"size":{"width":3440,"height":1440},"scale":1,"rotation":1,
           "sizeMM":{"width":810,"height":350}}
        ]}
        """;

    [Fact]
    public void The_layout_carries_each_panels_size_and_brightness()
    {
        var screens = KScreenLayout.Parse(Layout);
        var tablet = screens.Single(s => s.Name == "DP-5");
        Assert.Equal((340.0, 192.0, 0.8), (tablet.WidthMm, tablet.HeightMm, tablet.Brightness));
        Assert.Null(screens.Single(s => s.Name == "DP-2").Brightness);
        Assert.Equal((0.0, 0.0), (screens.Single(s => s.Name == "HDMI-A-2").WidthMm, screens.Single(s => s.Name == "HDMI-A-2").HeightMm));
    }

    [Fact]
    public void The_tablets_own_screen_is_the_one_monitor_of_its_size()
    {
        var model = TabletModel.Artist16Third;
        var screens = KScreenLayout.Parse(Layout);
        Assert.Equal("DP-5", new DesktopSnapshot(screens, null).OwnScreen(model)?.Name);

        // No monitor of that size, or two of them, is no answer: a guess would dim the wrong screen.
        Assert.Null(new DesktopSnapshot([.. screens.Where(s => s.Name != "DP-5")], null).OwnScreen(model));
        var twin = screens.Single(s => s.Name == "DP-5") with { Name = "DP-6" };
        Assert.Null(new DesktopSnapshot([.. screens, twin], null).OwnScreen(model));
        var larger = screens.Single(s => s.Name == "DP-5") with { WidthMm = 346, HeightMm = 195 };
        Assert.Null(new DesktopSnapshot([larger], null).OwnScreen(model));
    }

    static Task OnUi(Func<Task> body) => HeadlessApp.OnUi(body);

    [Fact]
    public Task The_display_page_shows_the_tablet_screens_brightness_and_writes_it_when_the_slider_is_let_go() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var display = bench.Main.Display;
        Assert.True(display.HasScreen);
        Assert.Contains("DP-5", display.ScreenLabel);
        Assert.Equal((true, 80.0, "80%"), (display.CanSetBrightness, display.Brightness, display.BrightnessText));

        // Moving the slider writes nothing by itself.
        display.Brightness = 55;
        Assert.Empty(bench.Desktop.Writes);
        Assert.Equal("55%", display.BrightnessText);

        await display.ApplyBrightnessAsync();
        await bench.Main.Pending;
        Assert.Equal(["brightness DP-5 0.55"], bench.Desktop.Writes);
        Assert.Equal(55.0, display.Brightness);
        Assert.Equal(0.55, (await bench.Desktop.ReadAsync(bench.Main.Model)).OwnScreen(bench.Main.Model)!.Brightness);
    });

    [Fact]
    public Task Without_a_monitor_of_the_tablets_size_the_page_says_so_and_writes_nothing() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var snapshot = await bench.Desktop.ReadAsync(bench.Main.Model);
        bench.Desktop.SetScreens([.. snapshot.Screens.Select(s => s with { WidthMm = 0, HeightMm = 0 })]);
        await bench.Main.RefreshAsync();
        var display = bench.Main.Display;

        Assert.False(display.HasScreen);
        Assert.False(display.CanSetBrightness);
        Assert.Contains("cannot tell which one is the tablet", display.Note);
        display.Brightness = 10;
        await display.ApplyBrightnessAsync();
        Assert.Empty(bench.Desktop.Writes);

        await display.DesktopSettingsCommand.ExecuteAsync(null);
        Assert.Equal(["display settings"], bench.Desktop.Writes);
    });

    [Theory]
    [InlineData(0.8, "output.DP-5.brightness.80")]
    [InlineData(0.004, "output.DP-5.brightness.0")]
    [InlineData(1.7, "output.DP-5.brightness.100")]
    public void Brightness_is_written_in_whole_percent_the_way_the_desktop_takes_it(double share, string expected) =>
        Assert.Equal(expected, KWinCompositor.BrightnessArgument("DP-5", share));
}
