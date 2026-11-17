using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

public class SettingsFileTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));

    ConfigStore Store => new(Path.Combine(_folder, "config"), Path.Combine(_folder, "state"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    static XperienceConfig Changed()
    {
        var config = XperienceConfig.Default.Completed();
        config = config with
        {
            Profiles = [.. config.Profiles, config.DefaultProfile with { App = "krita", Name = "Krita" }],
            Hints = false,
            KeysDisabled = true,
            Geometry = new PenGeometry { ActiveArea = new RectD(0.1, 0.2, 0.5, 0.6), Correction = [1.01, 0, 0.01, 0, 0.99, -0.01] },
            FloatingMenu = new FloatingMenuConfig { Vertical = true }.Completed(),
        };
        return config.Change("krita", p => p
            .WithKey(0, PadAction.Click(MouseAct.MiddleClick, MouseMods.Shift) with { Name = "Pan" })
            .WithKey(1, PadAction.Run("\"/opt/My App/run\" --x"))
            .WithDial(1, [new DialFunction { Kind = DialKind.Custom, Counterclockwise = "Ctrl+Z", Clockwise = "Ctrl+Shift+Z", Name = "History" },
                DialFunction.Of(DialKind.Scroll), DialFunction.Of(DialKind.Disabled), DialFunction.Of(DialKind.Rotate)])
            with { PenLower = PadAction.Do(SpecialAct.PrecisionMode), PenUpper = PadAction.Shortcut("E") });
    }

    [Fact]
    public void Settings_come_back_from_the_file_as_they_went_in()
    {
        var config = Changed();
        Assert.True(Store.Save(config));

        var read = Store.Load();

        Assert.True(read.SameAs(config));
        Assert.Equal("Pan", read.Find("krita")!.Key(0).Label);
        Assert.Equal(MouseMods.Shift, read.Find("krita")!.Key(0).MouseMods);
        Assert.Equal("History", read.Find("krita")!.Dial(1, 0).Label);
        Assert.Equal(new RectD(0.1, 0.2, 0.5, 0.6), read.Geometry.ActiveArea);
        Assert.Equal([1.01, 0, 0.01, 0, 0.99, -0.01], read.Geometry.Correction);
        Assert.Equal("Show Xperience", read.DefaultProfile.Key(0).Label);
    }

    [Fact]
    public void No_file_means_the_defaults_and_a_broken_one_is_set_aside_not_overwritten()
    {
        Assert.True(Store.Load().SameAs(XperienceConfig.Default.Completed()));

        Directory.CreateDirectory(Path.Combine(_folder, "config"));
        File.WriteAllText(Store.ConfigPath, "{ this is not json");
        Assert.True(Store.Load().SameAs(XperienceConfig.Default.Completed()));
        Assert.Equal("{ this is not json", File.ReadAllText(Store.ConfigPath));
        Assert.Equal("{ this is not json", File.ReadAllText(Store.ConfigPath + ".unreadable"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"profiles": []}""")]
    [InlineData("""{"profiles": [null, {"app": "krita", "keys": [null, {"kind": "Shortcut", "keys": "E"}], "dials": [[{"kind": "Scroll"}]]}]}""")]
    [InlineData("""{"profiles": [{"app": "left over", "name": "Odd"}], "floatingMenu": {"buttons": []}, "geometry": {"correction": [1, 2]}}""")]
    public void A_file_with_parts_missing_is_filled_out_to_full_length(string json)
    {
        var config = XperienceConfig.FromJson(json)!;

        Assert.True(config.DefaultProfile.IsDefault);
        Assert.Equal("All applications", config.DefaultProfile.Name);
        Assert.All(config.Profiles, p =>
        {
            Assert.Equal(Profile.KeyCount, p.Keys.Count);
            Assert.Equal(Profile.DialCount, p.Dials.Count);
            Assert.All(p.Dials, d => Assert.Equal(Profile.DialSlots, d.Count));
            Assert.All(p.Keys, Assert.NotNull);
        });
        Assert.Equal(FloatingMenuConfig.ButtonCount, config.FloatingMenu.Buttons.Count);
        Assert.Equal(6, config.Geometry.Correction.Count);
    }

    [Fact]
    public void What_a_partly_written_profile_holds_is_kept()
    {
        var config = XperienceConfig.FromJson("""{"profiles": [{}, {"app": "krita", "keys": [null, {"kind": "Shortcut", "keys": "E"}], "dials": [[{"kind": "Scroll"}]]}, {"app": "KRITA"}]}""")!;
        var krita = Assert.Single(config.Profiles.Skip(1));
        Assert.Equal("E", krita.Key(1).Label);
        Assert.Equal("Show Xperience", krita.Key(0).Label);
        Assert.Equal("Scroll", krita.Dial(0, 0).Label);
        Assert.Equal("Brush size", krita.Dial(1, 0).Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"profiles": 5}""")]
    public void Text_that_is_not_settings_is_refused(string json)
    {
        Assert.Null(XperienceConfig.FromJson(json));
    }

    [Fact]
    public void One_profile_is_changed_by_its_application_and_the_others_are_left()
    {
        var config = Changed();
        var changed = config.Change("KRITA", p => p.WithKey(7, PadAction.Off));
        Assert.Equal("Disabled", changed.Find("krita")!.Key(7).Label);
        Assert.Equal("Ctrl+Z", changed.DefaultProfile.Key(7).Label);
        Assert.Same(config.DefaultProfile, changed.DefaultProfile);
        Assert.True(config.Change("gimp", p => p.WithKey(7, PadAction.Off)).SameAs(config));
        Assert.Same(config.DefaultProfile, config.ProfileFor("firefox"));
        Assert.Equal("Krita", config.ProfileFor("Krita").Name);
    }

    [Fact]
    public void State_is_kept_between_runs()
    {
        var state = new RunState { DialSlots = { ["krita"] = [2, 1] }, AreaBeforePrecision = new RectD(0, 0, 0.5, 1), FloatingMenuAt = [120, 340], FloatingMenuPinned = true };
        Assert.True(Store.SaveState(state));

        var read = Store.LoadState();

        Assert.Equal([2, 1], read.SlotsFor("krita"));
        Assert.Equal([0, 0], read.SlotsFor(""));
        Assert.Equal(new RectD(0, 0, 0.5, 1), read.AreaBeforePrecision);
        Assert.Equal([120, 340], read.FloatingMenuAt!);
        Assert.True(read.FloatingMenuPinned);
        Assert.Null(new RunState().AreaBeforePrecision);
    }

    static Task<DesktopSnapshot> Read(DemoCompositor desktop) => desktop.ReadAsync(TabletModel.Artist16Third, TestContext.Current.CancellationToken);

    static readonly PenState Pen = new("event9", "Pen", ScreenTargetKind.OneScreen, "DP-2", new RectD(0.1, 0, 0.8, 1), Flipped: true, MouseMode: true,
        new PressureSettings(PressureCurve.Through(0.6, 0.3), 0.05, 0.9), CanFlip: true, CanSetScreenArea: true, CanSetPressureRange: true)
    {
        Matrix = PenMatrix.QuarterTurn,
        CanSetMatrix = true,
        MouseSpeed = 0.4,
        CanSetMouseSpeed = true,
    };

    [Fact]
    public async Task An_exported_file_brings_back_the_settings_and_the_pen()
    {
        var backup = Backup.FromJson(new Backup { Config = Changed(), Pen = PenBackup.Of(Pen) }.ToJson())!;
        Assert.True(backup.Config.SameAs(Changed()));

        var desktop = DemoCompositor.Sample();
        var before = (await Read(desktop)).Pen!;
        await backup.Pen!.ApplyAsync(desktop, before, (await Read(desktop)).Screens);

        var after = (await Read(desktop)).Pen!;
        Assert.Equal((Pen.TargetKind, Pen.TargetScreen, Pen.ScreenArea, Pen.Flipped, Pen.MouseMode, Pen.MouseSpeed),
            (after.TargetKind, after.TargetScreen, after.ScreenArea, after.Flipped, after.MouseMode, after.MouseSpeed));
        Assert.True(after.Matrix.IsCloseTo(PenMatrix.QuarterTurn, 1e-6));
        Assert.True(after.Pressure.Curve.IsCloseTo(Pen.Pressure.Curve));
        Assert.Equal((0.05, 0.9), (after.Pressure.RangeMin, after.Pressure.RangeMax));
    }

    [Fact]
    public async Task A_file_from_another_machine_cannot_send_the_pen_to_a_monitor_that_is_not_there()
    {
        var desktop = DemoCompositor.Sample();
        var snapshot = await Read(desktop);
        await (PenBackup.Of(Pen) with { TargetScreen = "HDMI-9" }).ApplyAsync(desktop, snapshot.Pen!, snapshot.Screens);

        Assert.DoesNotContain(desktop.Writes, w => w.StartsWith("target", StringComparison.Ordinal));
        Assert.Equal("DP-2", (await Read(desktop)).Pen!.TargetScreen);
        // The rest of the file still applies.
        Assert.Contains("mouse True", desktop.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"profiles": []}""")]
    [InlineData("""{"xperience": "one"}""")]
    public void A_file_that_was_not_exported_by_xperience_is_refused(string json)
    {
        Assert.Null(Backup.FromJson(json));
    }

    [Fact]
    public void An_export_without_a_pen_still_carries_the_settings()
    {
        var backup = Backup.FromJson("""{"xperience": 1, "config": {"hints": false}}""")!;
        Assert.False(backup.Config.Hints);
        Assert.Null(backup.Pen);
        Assert.Equal(Profile.KeyCount, backup.Config.DefaultProfile.Keys.Count);
    }
}
