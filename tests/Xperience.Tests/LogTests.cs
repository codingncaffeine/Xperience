using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Diagnostics;
using Xperience.Core.Input;
using Xperience.Core.Pen;
using Xperience.Core.Service;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>A record kept in memory, for reading back what was written.</summary>
public sealed class ListLog : ILog
{
    readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
                return [.. _lines];
        }
    }

    public void Write(string area, string text)
    {
        lock (_lines)
            _lines.Add($"{area}: {text}");
    }
}

/// <summary>The record of a session: the file, what the pen's reports become in it, and what the keys leave in it.</summary>
public sealed class LogTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Lines_are_stamped_kept_across_runs_and_one_event_stays_one_line()
    {
        var at = new DateTimeOffset(2026, 11, 17, 6, 50, 12, 345, TimeSpan.FromHours(-7));
        using (var first = SessionLog.Open(_folder, () => at)!)
        {
            first.Write("pen", "stroke: pressure 0..16383");
            first.Write("problem", "two\nlines");
            first.Settle();
            Assert.Equal(["2026-11-17 06:50:12.345  pen      stroke: pressure 0..16383", "2026-11-17 06:50:12.345  problem  two | lines"], File.ReadAllLines(first.Path));
        }
        using var second = SessionLog.Open(_folder, () => at.AddMinutes(1))!;
        second.Write("start", "again");
        second.Settle();
        string[] all = File.ReadAllLines(second.Path);
        Assert.Equal(3, all.Length);
        Assert.Equal("2026-11-17 06:51:12.345  start    again", all[2]);
    }

    [Fact]
    public void A_record_grown_large_is_set_aside_and_one_that_cannot_be_written_is_no_record()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "xperience.log");
        File.WriteAllText(path, new string('x', (int)SessionLog.RotateAt + 1));
        using (var log = SessionLog.Open(_folder)!)
        {
            log.Write("start", "fresh");
            log.Settle();
            Assert.Single(File.ReadAllLines(path));
            Assert.Equal(SessionLog.RotateAt + 1, new FileInfo(Path.Combine(_folder, "xperience.previous.log")).Length);
        }

        string blocked = Path.Combine(_folder, "a-file");
        File.WriteAllText(blocked, "");
        Assert.Null(SessionLog.Open(Path.Combine(blocked, "below")));
    }

    static PenSample Pen(bool touching, int pressure, int x = 1000, int y = 2000, bool eraser = false, bool lower = false, bool upper = false, int tiltX = 0, int tiltY = 0) =>
        new(true, touching, eraser, lower, upper, x, y, pressure, tiltX, tiltY);

    [Fact]
    public void A_stroke_becomes_one_line_with_what_it_reached()
    {
        var log = new ListLog();
        long now = 0;
        var pen = new PenLog(log, () => now, 1000);

        pen.Take(Pen(false, 0));
        now = 100; pen.Take(Pen(true, 300, x: 1000, y: 2000, tiltX: -5, tiltY: 10));
        now = 200; pen.Take(Pen(true, 16383, x: 3000, y: 2500, tiltX: 20, tiltY: 12));
        now = 600; pen.Take(Pen(true, 9000, x: 68199, y: 38359, tiltX: 7, tiltY: -30));
        now = 700; pen.Take(Pen(false, 0, x: 68199, y: 38359));
        now = 900; pen.Take(default);

        Assert.Equal(
        [
            "pen: came near the surface",
            "pen: stroke: 0.50 s, 3 reports (4/s), pressure 300..16383 mean 8561, x 1000..68199, y 2000..38359, tilt x -5..20 y -30..12",
            "pen: left the surface: 0.70 s, 5 reports (6/s), x 1000..68199, y 2000..38359, tilt x -5..20 y -30..12",
        ], log.Lines);
    }

    [Fact]
    public void Buttons_and_the_eraser_are_written_the_moment_they_change()
    {
        var log = new ListLog();
        long now = 0;
        var pen = new PenLog(log, () => now, 1000);

        pen.Take(Pen(false, 0));
        pen.Take(Pen(false, 0, lower: true));
        pen.Take(Pen(false, 0, lower: true));
        pen.Take(Pen(false, 0));
        pen.Take(Pen(false, 0, eraser: true, upper: true));
        now = 10; pen.Take(Pen(true, 500, eraser: true, upper: true));
        now = 20; pen.Take(Pen(false, 0, eraser: true, upper: true));
        pen.Take(Pen(false, 0));

        Assert.Equal(
        [
            "pen: came near the surface",
            "pen: lower button down",
            "pen: lower button up",
            "pen: became its eraser",
            "pen: upper button down",
            "pen: stroke with the eraser: 0.00 s, 1 reports, pressure 500..500 mean 500, x 1000..1000, y 2000..2000, tilt x 0..0 y 0..0, with the upper button held",
            "pen: became the pen again",
            "pen: upper button up",
        ], log.Lines);
    }

    [Fact]
    public void Keys_and_dial_turns_are_named_as_the_kernel_has_them()
    {
        Assert.Equal("key 1", PadReader.Name(KernelInput.EvKey, KernelInput.BtnMisc));
        Assert.Equal("key 8", PadReader.Name(KernelInput.EvKey, KernelInput.BtnMisc + 7));
        Assert.Equal("REL_WHEEL", PadReader.Name(KernelInput.EvRel, 8));
        Assert.Equal("REL_HWHEEL", PadReader.Name(KernelInput.EvRel, 6));
        Assert.Equal("REL_WHEEL_HI_RES", PadReader.Name(KernelInput.EvRel, 11));
        Assert.Equal("relative code 9", PadReader.Name(KernelInput.EvRel, 9));

        byte[] record = new byte[24];
        record[16] = 2; record[18] = 8;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), -1);
        Assert.Equal(((ushort)2, (ushort)8, -1), PadReader.Raw(record));
    }
}

/// <summary>Assignments follow the tablet as it is held: turned, another of its keys is the first.</summary>
public sealed class HeldOrderTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));
    ControlHub? _hub;

    public void Dispose()
    {
        _hub?.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(TabletTurn.None, 0, 0)]
    [InlineData(TabletTurn.ThreeQuarter, 0, 0)]
    [InlineData(TabletTurn.Half, 0, 7)]
    [InlineData(TabletTurn.Quarter, 0, 7)]
    [InlineData(TabletTurn.Half, 5, 2)]
    public void The_first_key_as_held_is_the_tablets_last_when_turned_a_quarter_or_a_half(TabletTurn turn, int held, int own)
    {
        Assert.Equal(own, HeldOrder.Key(held, turn));
        Assert.Equal(held, HeldOrder.Key(own, turn));
        Assert.Equal(HeldOrder.Reversed(turn) ? 1 : 0, HeldOrder.Dial(0, turn));
    }

    static readonly TabletDevices Both = new("Pen", "Pad");

    [Fact]
    public void The_desktops_bindings_follow_the_turn()
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p.WithKey(0, PadAction.Shortcut("Ctrl+Z")).WithKey(7, PadAction.Shortcut("F5")));
        string Key(IReadOnlyList<Rebind> all, string own) => all.Single(r => r.Group == Rebind.Keys && r.Key == own).Value;
        string Dial(IReadOnlyList<Rebind> all, string own) => all.Single(r => r.Group == Rebind.Dials && r.Key == own).Value;

        var upright = Assignments.Desired(config, config.DefaultProfile, [0, 0], Both, TabletTurn.None);
        Assert.Equal(("Key,Ctrl+Z", "Key,F5"), (Key(upright, "0"), Key(upright, "7")));
        var turned = Assignments.Desired(config, config.DefaultProfile, [0, 0], Both, TabletTurn.Half);
        Assert.Equal(("Key,F5", "Key,Ctrl+Z"), (Key(turned, "0"), Key(turned, "7")));
        // The dials trade places too, each keeping the function its place as held is on.
        Assert.Equal((Dial(upright, "0"), Dial(upright, "1")), (Dial(turned, "1"), Dial(turned, "0")));
        Assert.NotEqual(Dial(upright, "0"), Dial(upright, "1"));
        // Three quarters round, the keys lie along the bottom in the tablet's own order.
        Assert.Equal(upright, Assignments.Desired(config, config.DefaultProfile, [0, 0], Both, TabletTurn.ThreeQuarter));
    }

    [Fact]
    public async Task A_key_pressed_on_a_turned_tablet_does_what_its_place_as_held_is_set_to_and_says_so_in_the_record()
    {
        var store = new ConfigStore(Path.Combine(_folder, "config"), Path.Combine(_folder, "state"));
        // Two shortcuts in a row are typed by Xperience itself, so the hub has to pick the right key.
        store.Save(XperienceConfig.Default.Completed().Change("", p => p.WithKey(0, PadAction.Shortcut("Ctrl+Z Ctrl+Y"))));
        var desktop = DemoCompositor.Sample();
        desktop.SetPad(Bench.PadName);
        var injector = new RecordingInjector();
        var log = new ListLog();
        _hub = new ControlHub(desktop, store, injector, TabletModel.Artist16Third) { Log = log };
        await _hub.StartAsync();
        Assert.Equal(TabletTurn.Half, _hub.Turn);

        // The sample desktop has the tablet turned around: the top key as held is the tablet's own eighth.
        await _hub.KeyAsync(0, true);
        await _hub.KeyAsync(0, false);
        Assert.Empty(injector.Typed);
        await _hub.KeyAsync(7, true);
        await _hub.KeyAsync(7, false);
        Assert.NotEmpty(injector.Typed);

        Assert.Contains(log.Lines, l => l == "key: key 1 as held (the tablet's key 8) pressed: Ctrl+Z, Ctrl+Y, carried out by Xperience");
        Assert.Contains(log.Lines, l => l.StartsWith("key: key 8 as held (the tablet's key 1) pressed: ", StringComparison.Ordinal) && l.EndsWith("carried out by the desktop", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("binding: Tablet 7 on ", StringComparison.Ordinal) && l.Contains("Disabled"));
    }
}
