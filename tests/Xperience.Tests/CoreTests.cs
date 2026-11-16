using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

public class KScreenLayoutTests
{
    static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kscreen-four-screens.json"));

    [Fact]
    public void Screens_are_numbered_as_the_desktop_numbers_them_and_laid_out_scaled()
    {
        var screens = KScreenLayout.Parse(Fixture);

        Assert.Equal(["DP-2", "DP-3", "HDMI-A-2", "DP-5"], screens.Select(s => s.Name));
        Assert.Equal([1, 2, 3, 4], screens.Select(s => s.Number));
        Assert.Equal(new RectD(0, 967, 3440, 1440), screens[0].Bounds);
        // A 1920 x 1080 panel at 135% takes 1423 x 800 of the desktop, turned or not.
        Assert.Equal(new RectD(8800, 0, 1423, 800), screens[3].Bounds);
        Assert.Equal(ScreenRotation.Half, screens[3].Rotation);
        Assert.Equal(1.35, screens[3].Scale);
    }

    [Fact]
    public void A_screen_that_is_off_is_left_out()
    {
        Assert.DoesNotContain(KScreenLayout.Parse(Fixture), s => s.Name == "DP-1");
    }

    [Fact]
    public void A_screen_on_its_side_swaps_width_and_height()
    {
        string json = """{"outputs":[{"name":"A","enabled":true,"priority":1,"scale":1,"rotation":8,"pos":{"x":0,"y":0},"size":{"width":1920,"height":1080}}]}""";
        var screen = Assert.Single(KScreenLayout.Parse(json));
        Assert.Equal(new RectD(0, 0, 1080, 1920), screen.Bounds);
        Assert.Equal(ScreenRotation.Quarter, screen.Rotation);
    }

    [Fact]
    public void Screens_without_numbers_are_numbered_left_to_right()
    {
        string json = """
            {"outputs":[
            {"name":"R","enabled":true,"scale":1,"rotation":1,"pos":{"x":1920,"y":0},"size":{"width":1920,"height":1080}},
            {"name":"L","enabled":true,"scale":1,"rotation":1,"pos":{"x":0,"y":0},"size":{"width":1920,"height":1080}}]}
            """;
        Assert.Equal([("L", 1), ("R", 2)], KScreenLayout.Parse(json).Select(s => (s.Name, s.Number)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"outputs\": 3}")]
    [InlineData("[]")]
    [InlineData("3")]
    [InlineData("{\"outputs\": [3, \"x\", null]}")]
    [InlineData("{\"outputs\": [{\"name\": 5, \"enabled\": true, \"pos\": {}, \"size\": {}}]}")]
    [InlineData("{\"outputs\": [{\"name\": \"A\", \"enabled\": true, \"pos\": 1, \"size\": [1, 2]}]}")]
    public void Text_that_is_not_a_layout_gives_no_screens(string text)
    {
        Assert.Empty(KScreenLayout.Parse(text));
    }
}

public class PressureCurveTests
{
    [Fact]
    public void The_desktops_text_form_reads_and_writes_back()
    {
        Assert.Equal(PressureCurve.Linear, PressureCurve.Parse("0,0;1,1;"));
        var curve = PressureCurve.Parse("0.25,0.6;0.4,0.9;");
        Assert.Equal(new PressureCurve(0.25, 0.6, 0.4, 0.9), curve);
        Assert.Equal("0.25,0.6;0.4,0.9;", curve.ToDesktopString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0,0;1;")]
    [InlineData("a,b;c,d;")]
    [InlineData("0,0;1,NaN;")]
    public void Anything_else_reads_as_linear(string? text)
    {
        Assert.Equal(PressureCurve.Linear, PressureCurve.Parse(text));
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.3, 0.7)]
    [InlineData(0.7, 0.3)]
    [InlineData(0.2, 0.8)]
    public void A_curve_made_through_a_point_passes_through_it(double x, double y)
    {
        var (mx, my) = PressureCurve.Through(x, y).Middle;
        Assert.Equal(x, mx, 6);
        Assert.Equal(y, my, 6);
        var (ax, ay) = PressureCurve.Through(x, y).At(0.5);
        Assert.Equal(x, ax, 6);
        Assert.Equal(y, ay, 6);
    }

    [Fact]
    public void The_curve_starts_at_nothing_and_ends_at_full()
    {
        foreach (var curve in PressureCurve.Presets)
        {
            Assert.Equal((0, 0), curve.At(0));
            Assert.Equal((1, 1), curve.At(1));
        }
    }

    [Fact]
    public void Presets_run_from_soft_to_hard_with_linear_in_the_middle()
    {
        Assert.Equal(7, PressureCurve.Presets.Count);
        Assert.True(PressureCurve.Presets[3].IsCloseTo(PressureCurve.Linear));
        // Soft gives more output than input at the middle, hard gives less, and each step is harder than the last.
        var middles = PressureCurve.Presets.Select(p => p.Middle.Y - p.Middle.X).ToList();
        Assert.True(middles[0] > 0 && middles[6] < 0);
        Assert.Equal(middles.OrderByDescending(m => m), middles);
    }
}

public class AreaMathTests
{
    const double Surface = 341.0 / 191.8, Ultrawide = 3440.0 / 1440.0;

    [Fact]
    public void A_wide_screen_gives_up_its_sides_to_match_the_pen_area()
    {
        var area = AreaMath.SameShape(Surface, Ultrawide);
        Assert.Equal(1, area.Height);
        Assert.Equal(Surface / Ultrawide, area.Width, 6);
        Assert.Equal((1 - area.Width) / 2, area.X, 6);
        Assert.Equal(1, AreaMath.Stretch(Surface, Ultrawide, area), 6);
    }

    [Fact]
    public void A_tall_screen_gives_up_its_top_and_bottom()
    {
        var area = AreaMath.SameShape(Surface, 1.0);
        Assert.Equal(1, area.Width);
        Assert.Equal(1 / Surface, area.Height, 6);
        Assert.Equal(1, AreaMath.Stretch(Surface, 1.0, area), 6);
    }

    [Fact]
    public void The_whole_of_a_wider_screen_stretches_strokes_sideways()
    {
        Assert.Equal(Ultrawide / Surface, AreaMath.Stretch(Surface, Ultrawide, RectD.Full), 6);
        Assert.True(AreaMath.Stretch(Surface, Ultrawide, RectD.Full) > 1.3);
    }

    [Fact]
    public void An_area_is_kept_inside_the_screen()
    {
        Assert.Equal(new RectD(0.5, 0.5, 0.5, 0.5), AreaMath.Clamp(new RectD(0.9, 0.8, 0.5, 0.5)));
        Assert.Equal(new RectD(0, 0, 0.05, 1), AreaMath.Clamp(new RectD(-1, -1, 0, 4)));
    }
}

public class TabletScannerTests : IDisposable
{
    // The capability lists the kernel prints for this tablet's own input nodes.
    const string PenKeys = "c03 0 0 0 0 0";
    const string KeyboardKeys = "1000000000007 ff9f207ac14057ff febeffdfffefffff fffffffffffffffe";
    const string MouseKeys = "400 1f0000 0 0 0 0";
    const string PadKeys = "800 0 0 0 ff 0 0 0 0";

    readonly string _root = Directory.CreateTempSubdirectory("xperience-scan").FullName;

    string Sys => Path.Combine(_root, "sys");
    string Proc => Path.Combine(_root, "proc");

    public TabletScannerTests()
    {
        Directory.CreateDirectory(Sys);
        Directory.CreateDirectory(Proc);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void Node(string name, string vendor, string product, string keys)
    {
        string device = Path.Combine(Sys, name, "device");
        Directory.CreateDirectory(Path.Combine(device, "id"));
        Directory.CreateDirectory(Path.Combine(device, "capabilities"));
        File.WriteAllText(Path.Combine(device, "id", "vendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(device, "id", "product"), product + "\n");
        File.WriteAllText(Path.Combine(device, "capabilities", "key"), keys + "\n");
    }

    void Process(string pid, string name)
    {
        Directory.CreateDirectory(Path.Combine(Proc, pid));
        File.WriteAllText(Path.Combine(Proc, pid, "comm"), name + "\n");
    }

    TabletPresence Scan() => TabletScanner.Scan(TabletModel.Artist16Third, Sys, Proc);

    [Fact]
    public void The_kernels_capability_list_is_read_bit_by_bit()
    {
        Assert.True(TabletScanner.HasBit(PenKeys, 0x140));      // a pen tool
        Assert.False(TabletScanner.HasBit(PenKeys, 0x100));     // no pad button
        Assert.True(TabletScanner.HasBit(KeyboardKeys, 30));    // the A key
        Assert.False(TabletScanner.HasBit(MouseKeys, 0x140));
        Assert.True(TabletScanner.HasBit(PadKeys, 0x100));
        Assert.False(TabletScanner.HasBit("", 0));
        Assert.False(TabletScanner.HasBit("zz", 0));
    }

    [Fact]
    public void Out_of_the_box_the_tablet_has_a_pen_and_a_keyboard_and_no_pad()
    {
        Node("event27", "28bd", "097e", MouseKeys);
        Node("event28", "28bd", "097e", KeyboardKeys);
        Node("event29", "28bd", "097e", PenKeys);
        Node("event3", "046d", "c52b", KeyboardKeys);

        var found = Scan();

        Assert.Equal(DriveMode.BuiltIn, found.Mode);
        Assert.Equal("/dev/input/event29", found.PenNode);
        Assert.Equal("/dev/input/event28", found.KeyboardNode);
        Assert.Null(found.PadNode);
        Assert.False(found.VendorAppRunning);
    }

    [Fact]
    public void A_pad_node_means_the_driver_is_loaded()
    {
        Node("event40", "28bd", "097e", PadKeys);
        Node("event41", "28bd", "097e", PenKeys);

        var found = Scan();

        Assert.Equal(DriveMode.Driver, found.Mode);
        Assert.Equal("/dev/input/event40", found.PadNode);
        Assert.Equal("/dev/input/event41", found.PenNode);
    }

    [Fact]
    public void Another_makers_devices_are_not_the_tablet()
    {
        Node("event3", "046d", "c52b", KeyboardKeys);
        Node("event4", "28bd", "0905", PenKeys);

        Assert.Equal(TabletPresence.None, Scan());
    }

    [Fact]
    public void The_makers_own_driver_program_is_noticed()
    {
        Process("1", "systemd");
        Process("4242", "PenTablet");
        Directory.CreateDirectory(Path.Combine(Proc, "self"));

        Assert.True(Scan().VendorAppRunning);
    }

    [Fact]
    public void A_missing_device_list_reports_no_tablet()
    {
        Assert.Equal(DriveMode.Absent, TabletScanner.Scan(TabletModel.Artist16Third, Path.Combine(_root, "nowhere"), Proc).Mode);
    }
}
