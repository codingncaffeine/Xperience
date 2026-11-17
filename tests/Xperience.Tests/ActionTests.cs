using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Settings;

namespace Xperience.Tests;

public class KeyComboTests
{
    [Theory]
    [InlineData("Ctrl+Z", "Ctrl+Z")]
    [InlineData("ctrl+shift+z", "Ctrl+Shift+Z")]
    [InlineData("Shift+Alt+Ctrl+Super+f5", "Super+Ctrl+Alt+Shift+F5")]
    [InlineData("Alt", "Alt")]
    [InlineData("[", "[")]
    [InlineData("Ctrl+=", "Ctrl+=")]
    [InlineData("Ctrl+-", "Ctrl+-")]
    [InlineData(" Space ", "Space")]
    public void A_shortcut_reads_back_as_it_is_written(string text, string written)
    {
        Assert.Equal(written, KeyCombo.Parse(text)?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("Ctrl+Nope")]
    [InlineData("Z+X")]
    [InlineData("Hyper+Z")]
    public void Something_that_is_not_a_shortcut_is_refused(string text)
    {
        Assert.Null(KeyCombo.Parse(text));
    }

    [Theory]
    [InlineData("Ctrl+Z", "Ctrl+Z")]
    [InlineData("Super+E", "Meta+E")]
    [InlineData("Alt", "Alt")]
    [InlineData("Super", "Meta")]
    [InlineData("Enter", "Return")]
    [InlineData("Ctrl+Delete", "Ctrl+Del")]
    [InlineData("Shift+PageUp", "Shift+PgUp")]
    [InlineData("Ctrl+Alt+Shift+F12", "Ctrl+Alt+Shift+F12")]
    public void The_desktop_gets_shortcuts_in_its_own_spelling(string text, string desktop)
    {
        Assert.Equal(desktop, KeyCombo.Parse(text)!.Value.ToDesktop());
    }

    [Fact]
    public void Two_modifiers_alone_are_more_than_the_desktop_can_hold()
    {
        Assert.Null(KeyCombo.Parse("Ctrl+Alt")!.Value.ToDesktop());
    }

    [Fact]
    public void Keys_are_pressed_modifiers_first()
    {
        Assert.Equal([29, 42, 44], KeyCombo.Parse("Ctrl+Shift+Z")!.Value.Codes());
        Assert.Equal([57], KeyCombo.Parse("Space")!.Value.Codes());
        Assert.Equal([56], KeyCombo.Parse("Alt")!.Value.Codes());
        Assert.Equal([125, 29, 56, 42, 30], KeyCombo.Parse("Shift+Alt+Ctrl+Super+A")!.Value.Codes());
    }

    [Fact]
    public void Every_key_has_one_name_one_code_and_a_name_that_splits_cleanly()
    {
        Assert.Equal(KeyTable.Keys.Count, KeyTable.Keys.Select(k => k.Name.ToUpperInvariant()).Distinct().Count());
        Assert.Equal(KeyTable.Keys.Count, KeyTable.Keys.Select(k => k.Code).Distinct().Count());
        Assert.All(KeyTable.Keys, k =>
        {
            Assert.True(k.Code > 0);
            Assert.DoesNotContain(' ', k.Name);
            Assert.DoesNotContain('+', k.Name);
            Assert.Equal(k.Name, KeyCombo.Parse(k.Name)?.Key);
        });
    }

    [Fact]
    public void Several_shortcuts_are_written_with_spaces_and_unknown_ones_dropped()
    {
        var combos = KeyCombo.ParseAll("Ctrl+C  Bogus Ctrl+V");
        Assert.Equal(["Ctrl+C", "Ctrl+V"], combos.Select(c => c.ToString()));
        Assert.Equal("Ctrl+C Ctrl+V", KeyCombo.Write(combos));
        Assert.Empty(KeyCombo.ParseAll(null));
    }
}

public class PadActionTests
{
    [Fact]
    public void An_action_describes_itself_unless_it_was_given_a_name()
    {
        Assert.Equal("Ctrl+C, Ctrl+V", PadAction.Shortcut("Ctrl+C Ctrl+V").Label);
        Assert.Equal("Paste twice", (PadAction.Shortcut("Ctrl+V Ctrl+V") with { Name = "Paste twice" }).Label);
        Assert.Equal("Shift+Left double-click", PadAction.Click(MouseAct.DoubleClick, MouseMods.Shift).Label);
        Assert.Equal("Ctrl+Left Alt+Space+Scroll down", PadAction.Click(MouseAct.ScrollDown, MouseMods.Space | MouseMods.Ctrl | MouseMods.LeftAlt).Label);
        Assert.Equal("Run krita", PadAction.Run("/usr/bin/krita --nosplash").Label);
        Assert.Equal("Switch dial 1 function", PadAction.Do(SpecialAct.SwitchDial1).Label);
        Assert.Equal("Disabled", PadAction.Off.Label);
        Assert.Equal("Pen button", PadAction.PenButton.Label);
    }

    [Fact]
    public void The_makers_assignments_are_the_defaults()
    {
        Assert.Equal(["Show Xperience", "B", "Alt", "Switch dial 1 function", "Switch dial 2 function", "Space", "Ctrl+S", "Ctrl+Z"],
            new Profile().Keys.Select(k => k.Label));
        Assert.Equal(["Zoom", "Brush size"], new Profile().Dials.Select(d => d[0].Label));
        Assert.Equal(["Ctrl+Shift+Z", "Ctrl+Z", "Ctrl+Shift+N", "Ctrl+E", "]", "[", "Ctrl+S", "Show Xperience"],
            new FloatingMenuConfig().Buttons.Select(b => b.Label));
    }

    [Fact]
    public void A_dial_function_has_a_shortcut_for_each_direction()
    {
        Assert.Equal(("Ctrl+-", "Ctrl+="), Names(DialFunction.Of(DialKind.Zoom)));
        Assert.Equal(("[", "]"), Names(DialFunction.Of(DialKind.BrushSize)));
        Assert.Equal(("Ctrl+Z", "Ctrl+Shift+Z"), Names(new DialFunction { Kind = DialKind.Custom, Counterclockwise = "Ctrl+Z", Clockwise = "Ctrl+Shift+Z" }));
        Assert.Null(DialFunction.Of(DialKind.Scroll).Turns());
        Assert.Null(DialFunction.Of(DialKind.Custom).Turns());
        Assert.Equal("Ctrl+Z / Ctrl+Shift+Z", new DialFunction { Kind = DialKind.Custom, Counterclockwise = "Ctrl+Z", Clockwise = "Ctrl+Shift+Z" }.Label);

        static (string, string)? Names(DialFunction f) => f.Turns() is (var back, var forward) ? (back.ToString(), forward.ToString()) : null;
    }
}

/// <summary>What the desktop is asked to do by itself, and what is left to Xperience.</summary>
public class AssignmentTests
{
    [Theory]
    [InlineData("Ctrl+Z", "Key,Ctrl+Z")]
    [InlineData("Alt", "Key,Alt")]
    [InlineData("Space", "Key,Space")]
    [InlineData("Ctrl+,", @"Key,Ctrl+\,")]
    [InlineData(@"\", @"Key,\\")]
    public void One_shortcut_is_the_desktops_to_carry_out(string keys, string binding)
    {
        Assert.Equal(binding, Assignments.Native(PadAction.Shortcut(keys)));
        Assert.False(Assignments.NeedsXperience(PadAction.Shortcut(keys)));
    }

    [Fact]
    public void Plain_clicks_are_the_desktops_with_the_modifiers_it_knows()
    {
        Assert.Equal("MouseButton,272", Assignments.Native(PadAction.Click(MouseAct.LeftClick)));
        Assert.Equal("MouseButton,273", Assignments.Native(PadAction.Click(MouseAct.RightClick)));
        Assert.Equal("MouseButton,274,100663296", Assignments.Native(PadAction.Click(MouseAct.MiddleClick, MouseMods.Shift | MouseMods.Ctrl)));
        Assert.Equal("MouseButton,272,134217728", Assignments.Native(PadAction.Click(MouseAct.LeftClick, MouseMods.LeftAlt)));
        Assert.Equal("Disabled", Assignments.Native(PadAction.Off));
    }

    [Fact]
    public void Everything_else_is_left_to_xperience()
    {
        PadAction[] ours =
        [
            PadAction.Shortcut("Ctrl+C Ctrl+V"), PadAction.Shortcut("Ctrl+Alt"), PadAction.Click(MouseAct.DoubleClick), PadAction.Click(MouseAct.ScrollUp),
            PadAction.Click(MouseAct.LeftClick, MouseMods.Space), PadAction.Click(MouseAct.RightClick, MouseMods.RightAlt), PadAction.Run("krita"),
            PadAction.Do(SpecialAct.ShowXperience), PadAction.Do(SpecialAct.PrecisionMode),
        ];
        Assert.All(ours, action =>
        {
            Assert.Null(Assignments.Native(action));
            Assert.True(Assignments.NeedsXperience(action));
        });
        // A pen button left alone is nobody's job.
        Assert.False(Assignments.NeedsXperience(PadAction.PenButton));
    }

    [Fact]
    public void A_dial_turns_into_a_shortcut_each_way_or_the_wheel()
    {
        Assert.Equal("AxisKey,Ctrl+=,Ctrl+-,1", Assignments.Native(DialFunction.Of(DialKind.Zoom)));
        Assert.Equal("AxisKey,],[,1", Assignments.Native(DialFunction.Of(DialKind.BrushSize)));
        Assert.Equal("Scroll", Assignments.Native(DialFunction.Of(DialKind.Scroll)));
        Assert.Equal("Disabled", Assignments.Native(DialFunction.Of(DialKind.Disabled)));
        Assert.Equal("Disabled", Assignments.Native(DialFunction.Of(DialKind.Custom)));
        Assert.Equal("AxisKey,Ctrl+Shift+Z,Ctrl+Z,1", Assignments.Native(new DialFunction { Kind = DialKind.Custom, Counterclockwise = "Ctrl+Z", Clockwise = "Ctrl+Shift+Z" }));
    }

    static readonly TabletDevices Both = new("Pen", "Pad");

    [Fact]
    public void The_default_profile_gives_every_control_its_binding()
    {
        var config = XperienceConfig.Default.Completed();
        var wanted = Assignments.Desired(config, config.DefaultProfile, [0, 0], Both);

        Assert.Equal(["Disabled", "Key,B", "Key,Alt", "Disabled", "Disabled", "Key,Space", "Key,Ctrl+S", "Key,Ctrl+Z"],
            wanted.Where(r => r.Group == Rebind.Keys).OrderBy(r => r.Key).Select(r => r.Value));
        Assert.Equal(["AxisKey,Ctrl+=,Ctrl+-,1", "AxisKey,],[,1"], wanted.Where(r => r.Group == Rebind.Dials).OrderBy(r => r.Key).Select(r => r.Value));
        Assert.All(wanted.Where(r => r.Group != Rebind.PenButtons), r => Assert.Equal("Pad", r.Device));
        // The pen's buttons are left alone: entries with no value, so that leftovers are removed.
        Assert.Equal([("331", ""), ("332", "")], wanted.Where(r => r.Group == Rebind.PenButtons).Select(r => (r.Key, r.Value)));
        Assert.All(wanted.Where(r => r.Group == Rebind.PenButtons), r => Assert.Equal("Pen", r.Device));
    }

    [Fact]
    public void A_dial_on_its_second_function_is_bound_to_that_one()
    {
        var config = XperienceConfig.Default.Completed();
        var wanted = Assignments.Desired(config, config.DefaultProfile, [1, 3], Both);
        Assert.Equal(["Scroll", "AxisKey,Ctrl+],Ctrl+[,1"], wanted.Where(r => r.Group == Rebind.Dials).OrderBy(r => r.Key).Select(r => r.Value));
    }

    [Fact]
    public void Without_real_keys_only_the_pen_is_bound_and_switched_off_keys_are_all_off()
    {
        var config = XperienceConfig.Default.Completed();
        Assert.All(Assignments.Desired(config, config.DefaultProfile, [0, 0], new TabletDevices("Pen", null)), r => Assert.Equal(Rebind.PenButtons, r.Group));
        Assert.Empty(Assignments.Desired(config, config.DefaultProfile, [0, 0], new TabletDevices(null, null)));

        var off = Assignments.Desired(config with { KeysDisabled = true }, config.DefaultProfile, [0, 0], Both);
        Assert.All(off.Where(r => r.Group != Rebind.PenButtons), r => Assert.Equal("Disabled", r.Value));
        Assert.Equal(10, off.Count(r => r.Group != Rebind.PenButtons));
    }

    [Fact]
    public void Only_what_differs_is_changed_and_leftovers_are_removed()
    {
        Rebind[] current = [new("Tablet", "Pad", "0", "Key,B"), new("TabletTool", "Pen", "331", "MouseButton,273"), new("Tablet", "Other", "0", "Key,Q")];
        Rebind[] desired = [new("Tablet", "Pad", "0", "Key,B"), new("Tablet", "Pad", "1", "Key,E"), new("TabletTool", "Pen", "331", ""), new("TabletTool", "Pen", "332", "")];

        var changes = Assignments.Changes(current, desired);

        Assert.Equal([new Rebind("Tablet", "Pad", "1", "Key,E"), new Rebind("TabletTool", "Pen", "331", "")], changes);
    }

    [Fact]
    public void A_dial_steps_past_functions_that_are_disabled()
    {
        var profile = new Profile().WithDial(0, [DialFunction.Of(DialKind.Zoom), DialFunction.Of(DialKind.Disabled), DialFunction.Of(DialKind.Rotate), DialFunction.Of(DialKind.Disabled)]);
        Assert.Equal(2, Assignments.NextSlot(profile, 0, 0));
        Assert.Equal(0, Assignments.NextSlot(profile, 0, 2));
        var dead = new Profile().WithDial(1, [.. Enumerable.Repeat(DialFunction.Of(DialKind.Disabled), 4)]);
        Assert.Equal(1, Assignments.NextSlot(dead, 1, 1));
    }
}

public class RebindFileTests
{
    const string Sample = """
        [Libinput][10429][2430][XPPen Artist 16 3rd]
        LeftHanded=true
        OutputUuid=abc

        [ButtonRebinds][Tablet][XPPen Artist 16 3rd Pad]
        0=Disabled
        7=Key,Ctrl+Z
        3=Key,Ctrl+\\,
        note[$i]=x

        [ButtonRebinds][TabletDial][XPPen Artist 16 3rd Pad]
        0=AxisKey,Ctrl+\\=,Ctrl+-,1

        [ButtonRebinds][TabletTool][XPPen Artist 16 3rd]
        331=MouseButton,273

        [ButtonRebinds][Mouse]
        ExtraButton1=Key,Ctrl+C

        [Mouse]
        X11LibInputXAccelProfileFlat=true
        """;

    [Fact]
    public void Bindings_are_read_from_the_desktops_file_and_everything_else_is_passed_over()
    {
        var found = RebindFile.Parse(Sample);

        Assert.Equal(
        [
            new Rebind("Tablet", "XPPen Artist 16 3rd Pad", "0", "Disabled"),
            new Rebind("Tablet", "XPPen Artist 16 3rd Pad", "7", "Key,Ctrl+Z"),
            new Rebind("Tablet", "XPPen Artist 16 3rd Pad", "3", @"Key,Ctrl+\,"),
            new Rebind("TabletDial", "XPPen Artist 16 3rd Pad", "0", @"AxisKey,Ctrl+\=,Ctrl+-,1"),
            new Rebind("TabletTool", "XPPen Artist 16 3rd", "331", "MouseButton,273"),
        ], found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[ButtonRebinds][Tablet]\n0=Key,B\n")]
    [InlineData("[ButtonRebinds][Tablet][Pad\n0=Key,B\n")]
    [InlineData("no sections at all\n=\n")]
    public void A_file_without_bindings_gives_none(string text)
    {
        Assert.Empty(RebindFile.Parse(text));
    }

    /// <summary>
    /// What Xperience writes through the desktop's own settings tool comes back the same from the file. The
    /// tool runs against a settings folder of the test's own and tells no running desktop.
    /// </summary>
    [Fact]
    public async Task The_desktops_settings_tool_writes_what_is_read_back()
    {
        string? tool = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, "kwriteconfig6")).FirstOrDefault(File.Exists);
        if (tool == null)
            Assert.Skip("kwriteconfig6 is not installed on this machine");
        string home = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            Rebind[] wanted =
            [
                new("Tablet", "XPPen Artist 16 3rd Pad", "7", "Key,Ctrl+Z"),
                new("Tablet", "XPPen Artist 16 3rd Pad", "3", Assignments.Native(PadAction.Shortcut("Ctrl+,"))!),
                new("Tablet", "XPPen Artist 16 3rd Pad", "2", Assignments.Native(PadAction.Shortcut(@"\"))!),
                new("TabletDial", "XPPen Artist 16 3rd Pad", "0", Assignments.Native(DialFunction.Of(DialKind.Zoom))),
                new("TabletTool", "XPPen Artist 16 3rd", "331", "MouseButton,273"),
                new("TabletTool", "XPPen Artist 16 3rd", "332", "Disabled"),
            ];
            await KWinCompositor.WriteRebindsWithToolAsync(wanted, home, notify: false, TestContext.Current.CancellationToken);
            var read = await KWinCompositor.ReadRebindsAsync(Path.Combine(home, "kcminputrc"), TestContext.Current.CancellationToken);
            Assert.Equal(wanted.OrderBy(r => r.Id), read.OrderBy(r => r.Id));

            // An empty value takes the binding out again, and the rest stays.
            await KWinCompositor.WriteRebindsWithToolAsync([new Rebind("TabletTool", "XPPen Artist 16 3rd", "331", "")], home, notify: false, TestContext.Current.CancellationToken);
            read = await KWinCompositor.ReadRebindsAsync(Path.Combine(home, "kcminputrc"), TestContext.Current.CancellationToken);
            Assert.Equal(wanted.Where(r => r.Key != "331").OrderBy(r => r.Id), read.OrderBy(r => r.Id));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
