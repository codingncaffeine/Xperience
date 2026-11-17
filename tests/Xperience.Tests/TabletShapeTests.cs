using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The app following the plugged-in tablet: the model list, what a tablet answered, and the picture.</summary>
public sealed class TabletShapeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "xperience-shapes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A HID device of the tablet in a made-up sysfs, and udev's record of it, as the probe left it.</summary>
    string Plug(string name, string hidName, params string[] record)
    {
        string hid = Path.Combine(_root, "hid");
        string device = Path.Combine(hid, name);
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "uevent"), $"DRIVER=hid-generic\nHID_NAME={hidName}\n");
        string udev = Path.Combine(_root, "udev");
        Directory.CreateDirectory(udev);
        if (record.Length > 0)
            File.WriteAllLines(Path.Combine(udev, "+hid:" + name), ["I:1", .. record.Select(r => "E:" + r)]);
        return hid;
    }

    TabletModel? Detect(string hid) => TabletDetector.Detect(hid, Path.Combine(_root, "udev"));

    [Fact]
    public void The_model_list_is_the_one_the_driver_is_built_with()
    {
        var tsv = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "driver", "models.tsv"));
        Assert.Equal(TabletDetector.Parse(tsv), TabletDetector.Models);
        Assert.Contains(TabletDetector.Models, m => m.ProductId == 0x097e && m.Keys == 8 && m.Dials == 2 && m.Screen);
        // Models the kernel or a udev-hid-bpf driver drives are not on it.
        Assert.DoesNotContain(TabletDetector.Models, m => m.ProductId is 0x0905 or 0x0935 or 0x0929 or 0x097d);
    }

    [Fact]
    public void The_Artist_16_3rd_answering_as_it_does_is_its_measured_model()
    {
        string hid = Plug("0003:28BD:097E.0019", "XPPen Artist 16 3rd", "XPERIENCE_TABLET_PROBE=ok", "XPERIENCE_TABLET_X_MAX=68199",
            "XPERIENCE_TABLET_Y_MAX=38359", "XPERIENCE_TABLET_PRESSURE_MAX=16383", "XPERIENCE_TABLET_RESOLUTION=5080",
            "XPERIENCE_TABLET_KEYS=8", "XPERIENCE_TABLET_DIALS=2");
        var model = Detect(hid);
        Assert.NotNull(model);
        Assert.Equal(TabletModel.Artist16Third.WidthMm, model.WidthMm);
        Assert.Equal(TabletModel.Artist16Third.ActiveAreaMm, model.ActiveAreaMm);
        Assert.Equal(8, model.Keys.Count);
        Assert.Equal(2, model.Dials.Count);
    }

    [Fact]
    public void Another_tablet_is_drawn_from_its_answer_and_the_list()
    {
        // Artist 16 (2nd Gen): ten keys, no dial; the list says it is a pen display.
        string hid = Plug("0003:28BD:094C.0003", "XPPen Artist 16 (2nd Gen)", "XPERIENCE_TABLET_PROBE=ok", "XPERIENCE_TABLET_X_MAX=68198",
            "XPERIENCE_TABLET_Y_MAX=38362", "XPERIENCE_TABLET_PRESSURE_MAX=8191", "XPERIENCE_TABLET_RESOLUTION=5080",
            "XPERIENCE_TABLET_KEYS=10", "XPERIENCE_TABLET_DIALS=0");
        var model = Detect(hid);
        Assert.NotNull(model);
        Assert.Equal("Artist 16 (2nd Gen)", model.Name);
        Assert.Equal(0x094c, model.ProductId);
        Assert.Equal(10, model.Keys.Count);
        Assert.Empty(model.Dials);
        Assert.Equal(8191, model.PressureMax);
        Assert.True(model.HasScreen);
        // 68198 units at 5080 per inch is 341.0 mm.
        Assert.Equal(341.0, model.ActiveAreaMm.Width, 1);
        Assert.Equal(191.8, model.ActiveAreaMm.Height, 1);
        // Everything fits on the body, and the keys run down it in order without overlapping.
        Assert.True(model.ActiveAreaMm.X + model.ActiveAreaMm.Width <= model.WidthMm);
        Assert.True(model.ActiveAreaMm.Y + model.ActiveAreaMm.Height <= model.HeightMm);
        for (int i = 1; i < model.Keys.Count; i++)
            Assert.True(model.Keys[i].CenterY - model.Keys[i - 1].CenterY >= model.Keys[i].Length);
        Assert.All(model.Keys, k => Assert.InRange(k.CenterY, 0.0, 1.0));
    }

    [Fact]
    public void A_tablet_without_a_screen_or_controls_has_neither()
    {
        string hid = Plug("0003:28BD:0914.0001", "XPPen Star G640", "XPERIENCE_TABLET_PROBE=ok", "XPERIENCE_TABLET_X_MAX=32000",
            "XPERIENCE_TABLET_Y_MAX=20000", "XPERIENCE_TABLET_PRESSURE_MAX=8191", "XPERIENCE_TABLET_RESOLUTION=5080",
            "XPERIENCE_TABLET_KEYS=0", "XPERIENCE_TABLET_DIALS=0");
        var model = Detect(hid);
        Assert.NotNull(model);
        Assert.False(model.HasScreen);
        Assert.Empty(model.Keys);
        Assert.Empty(model.Dials);
    }

    [Fact]
    public void A_tablet_not_yet_asked_is_drawn_from_the_list()
    {
        // No udev record: the driver is not installed yet, so the probe has not run.
        var model = Detect(Plug("0003:28BD:091F.0002", "XPPen Artist 12 Pro"));
        Assert.NotNull(model);
        Assert.Equal(8, model.Keys.Count);
        Assert.Single(model.Dials);
    }

    [Fact]
    public void Tablets_not_on_the_list_are_not_taken()
    {
        Assert.Null(Detect(Plug("0003:28BD:0929.0004", "XPPen Deco mini4", "XPERIENCE_TABLET_PROBE=ok")));
        Assert.Null(Detect(Plug("0003:046D:C52B.0001", "Logitech receiver")));
    }

    [Theory]
    [InlineData(0x100, 0)]  // BTN_0: key 1
    [InlineData(0x109, 9)]  // BTN_9: key 10
    [InlineData(0x130, 10)] // BTN_A: key 11, as the driver hands on the keys past ten
    [InlineData(0x135, 15)] // key 16
    [InlineData(0x10a, -1)] // between BTN_9 and BTN_MOUSE: no key of a tablet
    [InlineData(0x136, -1)] // past the sixteenth
    [InlineData(0x110, -1)] // BTN_LEFT
    public void Keys_past_ten_are_read_as_the_driver_sends_them(int code, int key)
    {
        var record = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1); // EV_KEY
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), (ushort)code);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), 1);
        var decoded = Xperience.Core.Input.PadReader.Decode(record);
        Assert.Equal(key < 0 ? null : (key, true), decoded);
    }

    [Theory]
    [InlineData(TabletTurn.None, 0, 0)]
    [InlineData(TabletTurn.Half, 0, 9)]
    [InlineData(TabletTurn.Half, 9, 0)]
    [InlineData(TabletTurn.Quarter, 3, 6)]
    public void Turned_tablets_count_their_own_keys(TabletTurn turn, int held, int own)
    {
        // A ten-key tablet turned a half: its own last key is the first as held.
        Assert.Equal(own, HeldOrder.Key(held, turn, keys: 10));
        Assert.Equal(held, HeldOrder.Key(own, turn, keys: 10));
    }

    [Fact]
    public void The_check_lists_each_key_and_each_dial_both_ways()
    {
        Assert.Equal(["Key 1", "Key 2", "Key 3", "Key 4", "Key 5", "Key 6", "Key 7", "Key 8",
            "Dial 1 one way", "Dial 1 other way", "Dial 2 one way", "Dial 2 other way"], TabletReport.Controls(TabletModel.Artist16Third));
        var penOnly = TabletModel.FromAnswer("Star G640", 0x0914, 32000, 20000, 8191, 5080, 0, 0, false);
        Assert.Empty(TabletReport.Controls(penOnly));
    }

    [Fact]
    public void A_report_says_which_tablet_what_it_answered_and_what_was_seen()
    {
        var model = TabletModel.FromAnswer("Artist 16 (2nd Gen)", 0x094c, 68198, 38362, 8191, 5080, 10, 0, true);
        string text = TabletReport.Build(new ReportFacts("0.0.3", "7.3.4-arch1-2", model,
            new Dictionary<string, string> { ["XPERIENCE_TABLET_X_MAX"] = "68198", ["XPERIENCE_TABLET_PROBE"] = "ok" },
            "UG903_X 2026", ["driver   installed"], [new CheckedControl("Key 1", true), new CheckedControl("Key 2", false)],
            ["12:00 pen came near the surface"]));
        Assert.Contains("Xperience 0.0.3, kernel 7.3.4-arch1-2", text);
        Assert.Contains("Artist 16 (2nd Gen), USB 28bd:094c, 10 keys, 0 dials, pen display", text);
        Assert.Contains("firmware UG903_X 2026", text);
        Assert.Contains("answer   probe ok, x_max 68198", text);
        Assert.Contains("seen     Key 1", text);
        Assert.Contains("not seen Key 2", text);
        Assert.Contains("  driver   installed", text);
        Assert.Contains("  12:00 pen came near the surface", text);
        // The sections come in this order.
        Assert.True(text.IndexOf("TABLET", StringComparison.Ordinal) < text.IndexOf("KEYS AND DIALS SEEN", StringComparison.Ordinal));
        Assert.True(text.IndexOf("KEYS AND DIALS SEEN", StringComparison.Ordinal) < text.IndexOf("STATE", StringComparison.Ordinal));
        Assert.True(text.IndexOf("STATE", StringComparison.Ordinal) < text.IndexOf("RECORD", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_of_a_tablet_not_yet_asked_says_so()
    {
        string text = TabletReport.Build(new ReportFacts("0.0.3", "k", TabletModel.Artist16Third, new Dictionary<string, string>(), null, [], [], []));
        Assert.Contains("answer   none (the driver's probe has not asked it)", text);
        Assert.Contains("firmware not read", text);
    }

    [Fact]
    public void The_answer_is_read_from_udevs_record_of_the_tablet()
    {
        string hid = Plug("0003:28BD:094C.0003", "XPPen Artist 16 (2nd Gen)", "XPERIENCE_TABLET_PROBE=ok", "XPERIENCE_TABLET_KEYS=10",
            "XPERIENCE_PEN_TILT=on");
        var answer = TabletDetector.AnswerOf(0x094c, hid, Path.Combine(_root, "udev"));
        Assert.Equal(new Dictionary<string, string> { ["XPERIENCE_TABLET_PROBE"] = "ok", ["XPERIENCE_TABLET_KEYS"] = "10" }, answer);
        Assert.Empty(TabletDetector.AnswerOf(0x0914, hid, Path.Combine(_root, "udev")));
    }
}
