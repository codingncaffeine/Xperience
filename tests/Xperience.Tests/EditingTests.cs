using System.Buffers.Binary;
using System.Diagnostics;
using Xperience.App.ViewModels;
using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>
/// A named pipe that stands in for one of the tablet's input nodes: what is written into it is read by the
/// program exactly as it reads the kernel's records.
/// </summary>
public sealed class FakeNode : IDisposable
{
    readonly FileStream _writer;

    public FakeNode(string folder, string name)
    {
        Path = System.IO.Path.Combine(folder, name);
        using (var make = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { Path }, UseShellExecute = false })!)
            make.WaitForExit();
        // Held open for reading too, so neither end ever waits for the other to show up.
        _writer = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.None);
    }

    public string Path { get; }

    public void Send(ushort type, ushort code, int value)
    {
        Span<byte> record = stackalloc byte[24];
        record.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(record[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(record[18..], code);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], value);
        _writer.Write(record);
        _writer.Flush();
    }

    public void Sync() => Send(0, 0, 0);

    public void Dispose() => _writer.Dispose();
}

/// <summary>Changing what the controls do, through the pages, against a desktop held in memory.</summary>
public class EditingTests
{
    static Task OnUi(Func<Task> body) => HeadlessApp.OnUi(body);

    /// <summary>Waits for something another thread is about to make true, and fails with what was waited for.</summary>
    static async Task UntilAsync(Func<bool> done, string what)
    {
        for (int i = 0; i < 200 && !done(); i++)
            await Task.Delay(10);
        Assert.True(done(), $"never happened: {what}");
    }

    [Fact]
    public Task With_the_driver_opening_the_window_files_the_bindings_and_touches_nothing_of_the_pen() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);

        Assert.Equal(10, bench.Desktop.Writes.Count);
        Assert.All(bench.Desktop.Writes, w => Assert.StartsWith("bind ", w));
        Assert.Equal("Connected  ·  Xperience driver", bench.Main.StatusText);
        Assert.False(bench.Main.Home.ShowMode);

        await bench.Main.RefreshAsync();
        Assert.Equal(10, bench.Desktop.Writes.Count);
    });

    [Fact]
    public Task Assigning_a_key_saves_it_binds_it_and_shows_it() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        var main = bench.Main;
        ActionRequest? asked = null;
        bench.Dialogs.Action = request =>
        {
            asked = request;
            return PadAction.Shortcut("Ctrl+Shift+Z");
        };

        await main.Home.EditKey(7);

        Assert.Equal(("Key 8", "Ctrl+Z", "Ctrl+Z"), (asked!.Title, asked.Current.Label, asked.Default.Label));
        Assert.Equal("Ctrl+Shift+Z", main.Home.KeyLabels[7]);
        Assert.Equal("Key,Ctrl+Shift+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
        Assert.Equal("Ctrl+Shift+Z", bench.Store.Load().DefaultProfile.Key(7).Label);
        Assert.Null(main.Problem);

        // Leaving the dialog without OK changes nothing.
        int writes = bench.Desktop.Writes.Count;
        bench.Dialogs.Action = _ => null;
        await main.Home.EditKey(7);
        Assert.Equal(writes, bench.Desktop.Writes.Count);
        Assert.Equal("Ctrl+Shift+Z", main.Home.KeyLabels[7]);
    });

    [Fact]
    public Task Keys_can_be_assigned_before_the_driver_is_there_and_take_effect_when_it_comes() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(upright: true);
        var main = bench.Main;
        bench.Dialogs.Action = _ => PadAction.Shortcut("E");

        await main.Home.EditKey(1);

        Assert.Equal("E", main.Home.KeyLabels[1]);
        Assert.Empty(bench.Desktop.Writes);
        Assert.True(main.Home.ShowMode);

        // The driver is loaded: the keys become real controls and the desktop is told what they do.
        bench.Presence = Bench.Driven;
        bench.Desktop.SetPad(Bench.PadName);
        await main.RefreshAsync();
        Assert.Equal("Key,E", bench.Desktop.Binding(Rebind.Keys, "1"));
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
    });

    [Fact]
    public Task A_dials_functions_are_set_together_and_the_switch_key_steps_through_them() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        var main = bench.Main;
        bench.Dialogs.Dial = (slots, current) =>
        {
            Assert.Equal(["Zoom", "Scroll", "Brush size", "Rotate"], slots.Select(s => s.Label));
            Assert.Equal(0, current);
            return ([new DialFunction { Kind = DialKind.Custom, Counterclockwise = "Ctrl+Z", Clockwise = "Ctrl+Shift+Z" }, DialFunction.Of(DialKind.Scroll),
                DialFunction.Of(DialKind.Disabled), DialFunction.Of(DialKind.Disabled)], 1);
        };

        await main.Home.EditDial(0);

        Assert.Equal(["Scroll", "Brush size"], main.Home.DialLabels);
        Assert.Equal("Scroll", bench.Desktop.Binding(Rebind.Dials, "0"));

        // The key that switches dial 1 goes on to the next function that does something, around the end.
        await bench.Hub.KeyAsync(3, true);
        await main.RefreshAsync();
        Assert.Equal("AxisKey,Ctrl+Shift+Z,Ctrl+Z,1", bench.Desktop.Binding(Rebind.Dials, "0"));
        Assert.Equal("Ctrl+Z / Ctrl+Shift+Z", main.Home.DialLabels[0]);
        Assert.Equal("Dial 1: Ctrl+Z / Ctrl+Shift+Z", bench.Desktop.Hints[^1]);
    });

    [Fact]
    public Task The_lower_pen_button_is_bound_in_the_desktop_and_unbound_again() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        ActionRequest? asked = null;
        bench.Dialogs.Action = request =>
        {
            asked = request;
            return PadAction.Click(MouseAct.RightClick);
        };

        await main.Pen.EditLower();

        Assert.True(asked!.ForPenButton);
        Assert.Equal("Right click", main.Pen.LowerLabel);
        Assert.Equal("MouseButton,273", bench.Desktop.Binding(Rebind.PenButtons, "331"));
        Assert.Equal(["bind TabletTool 331 = MouseButton,273"], bench.Desktop.Writes);

        bench.Dialogs.Action = _ => PadAction.PenButton;
        await main.Pen.EditLower();
        Assert.Equal("Pen button", main.Pen.LowerLabel);
        Assert.Equal("", bench.Desktop.Binding(Rebind.PenButtons, "331"));
    });

    [Fact]
    public Task The_upper_pen_button_stays_the_eraser_until_the_driver_is_told_otherwise() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;

        await main.Pen.EditUpper();

        Assert.Equal(["confirm Upper pen button"], bench.Dialogs.Asked);
        Assert.Equal("Eraser while held", main.Pen.UpperLabel);
        Assert.Equal((PenButtonRole.Eraser, PenButtonRole.Button), (main.Pen.UpperRole.Role, main.Pen.LowerRole.Role));
        Assert.False(main.Pen.CanSetDriverOptions);
    });

    [Fact]
    public Task With_the_driver_set_to_a_plain_upper_button_it_can_be_assigned() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev/rules.d"));
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf"));
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"),
            "SUBSYSTEM==\"hid\", ENV{XPERIENCE_PEN_UPPER_BUTTON}=\"button\", ENV{XPERIENCE_PEN_TILT}=\"off\"\n");
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
        var main = bench.Main;
        await main.RefreshAsync();

        Assert.Equal((PenButtonRole.Button, PenButtonRole.Button), (main.Pen.UpperRole.Role, main.Pen.LowerRole.Role));
        Assert.False(main.Pen.TiltOn);
        Assert.True(main.Pen.CanSetDriverOptions);
        Assert.Equal("Pen button", main.Pen.UpperLabel);

        bench.Dialogs.Action = _ => PadAction.Shortcut("E");
        await main.Pen.EditUpper();
        Assert.Equal("E", main.Pen.UpperLabel);
        Assert.Equal("Key,E", bench.Desktop.Binding(Rebind.PenButtons, "332"));
    });

    [Fact]
    public Task An_application_gets_a_profile_of_its_own_and_loses_it_again() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        var main = bench.Main;
        bench.Dialogs.App = running => running.Single(a => a.Id == "krita");

        await main.Home.AddProfile();

        Assert.Equal("app krita,org.kde.dolphin,firefox", bench.Dialogs.Asked[^1]);
        Assert.Equal(["All applications", "Krita"], main.Home.Profiles.Select(p => p.Name));
        Assert.Equal("krita", main.EditingApp);
        Assert.True(main.Home.Profiles[1].Selected);
        Assert.True(main.Home.CanRemoveProfile);
        Assert.True(bench.Desktop.WatchingActiveApp);

        // The new profile is edited without changing the one every other application gets.
        bench.Dialogs.Action = _ => PadAction.Shortcut("Ctrl+Shift+Z");
        await main.Home.EditKey(7);
        Assert.Equal("Ctrl+Shift+Z", main.Home.KeyLabels[7]);
        Assert.Equal("Ctrl+Z", main.Config.DefaultProfile.Key(7).Label);
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "7"));

        // It takes over while its application is the active one.
        bench.Desktop.Activate("krita");
        await bench.Hub.SyncAsync();
        await main.RefreshAsync();
        Assert.Equal("Key,Ctrl+Shift+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
        Assert.True(main.Home.Profiles[1].Active);
        Assert.False(main.Home.Profiles[0].Active);

        // Adding the same application again only goes to its profile. It is no longer on the list of
        // applications to pick from, so it is typed in.
        main.EditingApp = "";
        bench.Dialogs.App = running =>
        {
            Assert.DoesNotContain(running, a => a.Id == "krita");
            return new AppInfo("Krita", "");
        };
        await main.Home.AddProfile();
        Assert.Equal("krita", main.EditingApp);
        Assert.Equal(2, main.Config.Profiles.Count);

        await main.Home.RemoveProfile();
        Assert.Equal(["All applications"], main.Home.Profiles.Select(p => p.Name));
        Assert.Equal("", main.EditingApp);
        Assert.False(main.Home.CanRemoveProfile);
        Assert.False(bench.Desktop.WatchingActiveApp);
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
    });

    [Fact]
    public Task Default_keys_puts_one_profile_back_and_asks_first() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        var main = bench.Main;
        bench.Dialogs.Action = _ => PadAction.Off;
        await main.Home.EditKey(7);
        await bench.Hub.KeyAsync(3, true);
        Assert.Equal("Scroll", bench.Desktop.Binding(Rebind.Dials, "0"));

        bench.Dialogs.Confirm = false;
        await main.Home.ResetKeys();
        Assert.Equal("Disabled", main.Home.KeyLabels[7]);

        bench.Dialogs.Confirm = true;
        await main.Home.ResetKeys();
        Assert.Equal("Ctrl+Z", main.Home.KeyLabels[7]);
        Assert.Equal(["Zoom", "Brush size"], main.Home.DialLabels);
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
        Assert.Equal("AxisKey,Ctrl+=,Ctrl+-,1", bench.Desktop.Binding(Rebind.Dials, "0"));
    });

    [Fact]
    public Task The_settings_switches_are_saved_and_take_effect() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        var main = bench.Main;
        Assert.True(main.Settings.Hints);
        Assert.False(main.Settings.KeysDisabled);
        Assert.True(main.Settings.KeepRunning);
        Assert.False(main.Settings.StartWithDesktop);

        main.Settings.Hints = false;
        await main.Pending;
        main.Settings.KeysDisabled = true;
        await main.Pending;
        main.Settings.KeepRunning = false;
        await main.Pending;

        var saved = bench.Store.Load();
        Assert.Equal((false, true, false), (saved.Hints, saved.KeysDisabled, saved.KeepRunning));
        Assert.Equal("Disabled", bench.Desktop.Binding(Rebind.Keys, "7"));
        Assert.Equal("Disabled", bench.Desktop.Binding(Rebind.Dials, "1"));
        Assert.True(main.Home.KeysDisabled);
        Assert.Equal("The keys and dials are switched off", main.Home.ModeTitle);

        main.Settings.KeysDisabled = false;
        await main.Pending;
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
    });

    [Fact]
    public Task Starting_with_the_desktop_is_an_entry_in_the_accounts_autostart_folder() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        string entry = Path.Combine(bench.Folder, "autostart", "xperience.desktop");

        main.Settings.StartWithDesktop = true;

        string text = File.ReadAllText(entry);
        Assert.Contains("Exec=\"/opt/xperience/xperience\" --background", text);
        Assert.Contains("Type=Application", text);
        Assert.True(main.Settings.StartWithDesktop);

        main.Settings.StartWithDesktop = false;
        Assert.False(File.Exists(entry));
        Assert.Empty(bench.Desktop.Writes);
    });

    [Fact]
    public Task Settings_exported_to_a_file_come_back_from_it() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        var main = bench.Main;
        string file = Path.Combine(bench.Folder, "export.json");
        bench.Dialogs.FileToSave = bench.Dialogs.FileToOpen = file;
        bench.Dialogs.Action = _ => PadAction.Shortcut("Ctrl+Shift+Z");
        await main.Home.EditKey(7);
        await main.Pen.UsePresetCommand.ExecuteAsync("1");
        await main.WorkArea.SameShapeCommand.ExecuteAsync(null);
        var exportedPen = await bench.PenAsync();

        await main.Settings.Export();
        Assert.Contains("exported", main.Settings.Message);

        // Everything is changed away from what was exported.
        bench.Dialogs.Action = _ => PadAction.Off;
        await main.Home.EditKey(7);
        await main.Pen.ResetPressureCommand.ExecuteAsync(null);
        await main.WorkArea.WholeScreenCommand.ExecuteAsync(null);
        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.None);
        await main.Pending;

        await main.Settings.Import();

        Assert.Equal("Ctrl+Shift+Z", main.Home.KeyLabels[7]);
        Assert.Equal("Key,Ctrl+Shift+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
        var pen = await bench.PenAsync();
        Assert.Equal((exportedPen.ScreenArea, exportedPen.Flipped, exportedPen.TargetScreen), (pen.ScreenArea, pen.Flipped, pen.TargetScreen));
        Assert.True(pen.Pressure.Curve.IsCloseTo(PressureCurve.Presets[1]));
        Assert.Null(main.Problem);
    });

    [Fact]
    public Task A_file_that_is_not_an_export_is_refused_and_changes_nothing() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        string file = Path.Combine(bench.Folder, "other.json");
        File.WriteAllText(file, """{"profiles": []}""");
        bench.Dialogs.FileToOpen = file;

        await main.Settings.Import();

        Assert.Contains("is not an Xperience settings file", main.Problem);
        Assert.Empty(bench.Desktop.Writes);
        Assert.False(File.Exists(bench.Store.ConfigPath));
    });

    [Fact]
    public Task Default_settings_resets_everything_but_the_monitor_and_the_rotation() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        var main = bench.Main;
        bench.Dialogs.Action = _ => PadAction.Off;
        bench.Dialogs.App = running => running[0];
        // The tablet is turned around here: its own first key is the eighth as held.
        await main.Home.EditKey(0);
        await main.Home.AddProfile();
        await main.Pen.UsePresetCommand.ExecuteAsync("6");
        main.Pen.MouseMode = true;
        await main.Pending;
        await main.WorkArea.SameShapeCommand.ExecuteAsync(null);
        await main.WorkArea.ProportionCommand.ExecuteAsync(null);
        main.Settings.Hints = false;
        await main.Pending;

        bench.Dialogs.Confirm = false;
        await main.Settings.RestoreDefaults();
        Assert.Equal("Disabled", main.Config.DefaultProfile.Key(7).Label);

        bench.Dialogs.Confirm = true;
        await main.Settings.RestoreDefaults();

        Assert.True(main.Config.SameAs(XperienceConfig.Default.Completed()));
        Assert.Equal("", main.EditingApp);
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "0"));
        var pen = await bench.PenAsync();
        Assert.True(pen.Pressure.Curve.IsCloseTo(PressureCurve.Linear));
        Assert.False(pen.MouseMode);
        Assert.Equal(RectD.Full, pen.ScreenArea);
        Assert.True(pen.Matrix.IsCloseTo(PenMatrix.Identity));
        // The two settings that put the pen where it belongs on this desk are left alone.
        Assert.Equal((ScreenTargetKind.OneScreen, "DP-2", true), (pen.TargetKind, pen.TargetScreen, pen.Flipped));
    });

    [Fact]
    public Task Installing_the_driver_runs_the_helper_and_gives_the_renamed_pen_its_settings_back() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(upright: true);
        var main = bench.Main;
        Assert.Equal("Not installed", main.Settings.DriverTitle);
        Assert.True(main.Settings.CanInstallDriver);
        Assert.False(main.Settings.CanRemoveDriver);
        await main.WorkArea.SameShapeCommand.ExecuteAsync(null);
        var before = await bench.PenAsync();

        bench.Elevate = command =>
        {
            // What loading the driver does: the files are in place, and the pen comes back as a new device
            // under a new name, with nothing set.
            Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev/rules.d"));
            Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf"));
            File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"), "");
            File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
            bench.Presence = Bench.Driven;
            bench.Desktop.SetPen(new PenState("event31", "XPPen Artist 16 3rd Stylus", ScreenTargetKind.ActiveScreen, "", RectD.Full, Flipped: false, MouseMode: false,
                PressureSettings.Default, CanFlip: true, CanSetScreenArea: true, CanSetPressureRange: true) { CanSetMatrix = true, CanSetMouseSpeed = true });
            bench.Desktop.SetPad(Bench.PadName);
            return (0, "");
        };

        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;

        Assert.Equal([$"install {DriverSetup.ObjectName} eraser on button none 1"], bench.Elevated);
        var pen = await bench.PenAsync();
        Assert.Equal("XPPen Artist 16 3rd Stylus", pen.Name);
        Assert.Equal((before.TargetKind, before.TargetScreen, before.Flipped, before.ScreenArea), (pen.TargetKind, pen.TargetScreen, pen.Flipped, pen.ScreenArea));
        Assert.Equal("Installed and loaded", main.Settings.DriverTitle);
        Assert.Equal("Reinstall driver", main.Settings.InstallLabel);
        Assert.True(main.Settings.CanRemoveDriver);
        Assert.Equal("Key,Ctrl+Z", bench.Desktop.Binding(Rebind.Keys, "7"));
        Assert.Null(main.Problem);
        Assert.False(main.Settings.DriverBusy);
    });

    [Fact]
    public Task Administrator_rights_refused_leave_everything_as_it_was_and_say_so() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Elevate = _ => (126, "");

        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;

        Assert.Contains("Administrator rights were not given", main.Problem);
        Assert.Empty(bench.Desktop.Writes);
        Assert.Equal("Not installed", main.Settings.DriverTitle);
        Assert.False(main.Settings.DriverBusy);
    });

    [Fact]
    public Task A_missing_loader_is_brought_along_by_the_install_and_never_asked_for_by_command() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        File.Delete(Path.Combine(bench.SystemFolder, "usr/bin/udev-hid-bpf"));
        await main.RefreshAsync();

        // The button stays there to press, and the card says what else pressing it installs.
        Assert.Equal("Not installed", main.Settings.DriverTitle);
        Assert.Contains("also installs udev-hid-bpf", main.Settings.DriverText);
        Assert.DoesNotContain("pacman", main.Settings.DriverText);
        Assert.True(main.Settings.CanInstallDriver);

        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Equal([$"install {DriverSetup.ObjectName} eraser on button none 1"], bench.Elevated);
        Assert.Null(main.Problem);

        // When the helper cannot bring it, what it said is shown.
        bench.Elevate = _ => (3, "The package udev-hid-bpf, which loads the driver, could not be installed.");
        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Contains("could not be installed", main.Problem);
    });

    [Fact]
    public Task Installing_the_driver_sees_to_the_makers_app_first() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        string theirs = Path.Combine(bench.SystemFolder, "etc/xdg/autostart/xppentablet.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
        File.WriteAllText(theirs, "[Desktop Entry]\nName=xppentablet\nType=Application\n");
        await main.RefreshAsync();
        Assert.True(main.Settings.MakersAppStarts);

        // Saying no leaves everything as it is.
        bench.Dialogs.Confirm = false;
        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Equal(["confirm Install driver"], bench.Dialogs.Asked);
        Assert.Empty(bench.Elevated);
        Assert.True(bench.Autostart.MakersAppStarts);

        // Saying yes keeps the maker's app from starting, without touching what it installed, and installs.
        bench.Dialogs.Confirm = true;
        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.False(bench.Autostart.MakersAppStarts);
        Assert.False(main.Settings.MakersAppStarts);
        Assert.True(File.Exists(theirs));
        Assert.Single(bench.Elevated);

        // The next install has nothing left to ask.
        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Equal(2, bench.Dialogs.Asked.Count(a => a == "confirm Install driver"));
        Assert.Equal(2, bench.Elevated.Count);

        // While the maker's app runs, the driver is not installed over it.
        bench.Presence = bench.Presence with { VendorAppRunning = true };
        await main.RefreshAsync();
        int before = bench.Elevated.Count;
        await main.Settings.InstallDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Equal(before, bench.Elevated.Count);
        Assert.Contains("Close it first", main.Problem);
    });

    [Fact]
    public Task The_pens_driver_options_reload_the_driver_with_the_new_choice() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev/rules.d"));
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf"));
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"), "");
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
        var main = bench.Main;
        await main.RefreshAsync();
        Assert.True(main.Pen.TiltOn);
        Assert.True(main.Pen.CanSetDriverOptions);

        main.Pen.TiltOn = false;
        await main.Pending;
        Assert.Equal([$"install {DriverSetup.ObjectName} eraser off button none 1"], bench.Elevated);

        main.Pen.UpperRole = main.Pen.Roles.Single(r => r.Role == PenButtonRole.Button);
        await main.Pending;
        Assert.Equal($"install {DriverSetup.ObjectName} button on button none 1", bench.Elevated[^1]);
    });

    [Fact]
    public Task A_pen_button_set_to_erase_or_to_switch_says_so_and_is_not_assignable_until_it_is_a_button_again() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev/rules.d"));
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf"));
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"),
            "SUBSYSTEM==\"hid\", ENV{XPERIENCE_PEN_UPPER_BUTTON}=\"toggle\", ENV{XPERIENCE_PEN_LOWER_BUTTON}=\"eraser\", ENV{XPERIENCE_PEN_TILT}=\"on\"\n");
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
        var main = bench.Main;
        await main.RefreshAsync();

        Assert.Equal((PenButtonRole.Toggle, PenButtonRole.Eraser), (main.Pen.UpperRole.Role, main.Pen.LowerRole.Role));
        Assert.Equal(("Pen/Eraser switch", "Eraser while held"), (main.Pen.UpperLabel, main.Pen.LowerLabel));
        Assert.True(main.Pen.TiltOn);

        await main.Pen.EditUpper();
        await main.Pen.EditLower();
        Assert.Equal(["confirm Upper pen button", "confirm Lower pen button"], bench.Dialogs.Asked);

        main.Pen.LowerRole = main.Pen.Roles.Single(r => r.Role == PenButtonRole.Button);
        await main.Pending;
        Assert.Equal([$"install {DriverSetup.ObjectName} toggle on button none 1"], bench.Elevated);
    });

    [Fact]
    public Task A_key_set_to_pen_eraser_is_handed_to_the_driver_and_follows_the_tablets_turn() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev/rules.d"));
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf"));
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"), "");
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
        var main = bench.Main;
        await main.RefreshAsync();
        bench.Dialogs.Action = request =>
        {
            Assert.True(request.ForDriverKey);
            return PadAction.Do(SpecialAct.PenEraser);
        };

        // The third key from the top. The driver is told, since only it can change the pen's tool.
        await main.Home.EditKey(2);
        await main.Pending;
        Assert.Equal([$"install {DriverSetup.ObjectName} eraser on button 3 1"], bench.Elevated);
        Assert.Equal("Disabled", bench.Desktop.Binding(Rebind.Keys, "2"));
        Assert.Equal("Pen/Eraser", main.Home.KeyLabels[2]);

        // The stand-in helper writes no rules, so the driver still counts as loaded without the key: turned
        // around, the third key as held is the tablet's own sixth, and that is what the driver is told then.
        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.Half);
        await main.Pending;
        Assert.Equal($"install {DriverSetup.ObjectName} eraser on button 6 1", bench.Elevated[^1]);

        // With the rules saying so already, nothing is reloaded.
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"), "ENV{XPERIENCE_PEN_SWITCH_KEYS}=\"6\"\n");
        await main.RefreshAsync();
        int before = bench.Elevated.Count;
        await main.ChangeConfigAsync(c => c with { Hints = !c.Hints });
        Assert.Equal(before, bench.Elevated.Count);

        // Given another job, the key is taken back from the driver.
        bench.Dialogs.Action = _ => PadAction.Shortcut("Alt");
        await main.Home.EditKey(5);
        await main.Pending;
        Assert.Equal($"install {DriverSetup.ObjectName} eraser on button none 1", bench.Elevated[^1]);
    });

    [Fact]
    public Task Pen_eraser_is_offered_for_a_key_only_where_the_driver_can_carry_it_out() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(upright: true);
        var main = bench.Main;
        ActionRequest? seen = null;
        bench.Dialogs.Action = request =>
        {
            seen = request;
            return null;
        };
        await main.Home.EditKey(0);
        Assert.True(seen!.ForDriverKey);
        Assert.Contains(new ActionDialogViewModel(seen).Others, o => o.Label == "Pen/Eraser");

        // A profile for one application cannot have it: the driver does not change with the application.
        bench.Dialogs.App = running => running[0];
        await main.Home.AddProfile();
        await main.Home.EditKey(0);
        Assert.False(seen!.ForDriverKey);
        Assert.DoesNotContain(new ActionDialogViewModel(seen).Others, o => o.Label == "Pen/Eraser");

        // Neither can a pen button, which has its own setting for this, nor a button of the floating menu.
        await main.Pen.EditLower();
        Assert.False(seen!.ForDriverKey);
    });

    [Fact]
    public void The_keys_the_driver_is_told_of_are_the_tablets_own_numbers_in_order()
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p.WithKey(2, PadAction.Do(SpecialAct.PenEraser)).WithKey(0, PadAction.Do(SpecialAct.PenEraser)));
        Assert.Equal("13", Assignments.SwitchKeys(config, TabletTurn.None));
        Assert.Equal("13", Assignments.SwitchKeys(config, TabletTurn.ThreeQuarter));
        Assert.Equal("68", Assignments.SwitchKeys(config, TabletTurn.Half));
        Assert.Equal("68", Assignments.SwitchKeys(config, TabletTurn.Quarter));
        Assert.Equal("", Assignments.SwitchKeys(config with { KeysDisabled = true }, TabletTurn.None));
        Assert.Equal("", Assignments.SwitchKeys(XperienceConfig.Default.Completed(), TabletTurn.None));

        var action = PadAction.Do(SpecialAct.PenEraser);
        Assert.Equal("Disabled", Assignments.Native(action));
        Assert.False(Assignments.NeedsXperience(action));
        Assert.Equal("36", DriverOptions.Keys("6a3 3-9"));
        Assert.Equal("", DriverOptions.Keys(null));
    }

    [Fact]
    public void A_word_the_driver_does_not_know_leaves_a_pen_button_as_it_comes()
    {
        Assert.Equal(PenButtonRole.Eraser, DriverOptions.Role("Button", DriverOptions.Default.Upper));
        Assert.Equal(PenButtonRole.Button, DriverOptions.Role(null, DriverOptions.Default.Lower));
        Assert.Equal(PenButtonRole.Toggle, DriverOptions.Role("toggle", DriverOptions.Default.Lower));
        foreach (var role in Enum.GetValues<PenButtonRole>())
            Assert.Equal(role, DriverOptions.Role(DriverOptions.Word(role), role == PenButtonRole.Button ? PenButtonRole.Eraser : PenButtonRole.Button));
    }

    [Fact]
    public Task The_work_area_reset_puts_the_areas_back_and_keeps_the_monitor_and_the_rotation() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Dialogs.Area = _ => new RectD(0.25, 0.1, 0.5, 0.5);
        await main.WorkArea.CustomScreenArea();
        await main.WorkArea.ProportionCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.False(main.WorkArea.IsWholeScreen);
        Assert.False(main.WorkArea.IsFullArea);

        bench.Dialogs.Confirm = false;
        await main.WorkArea.ResetAreas();
        Assert.False(main.WorkArea.IsWholeScreen);

        bench.Dialogs.Confirm = true;
        await main.WorkArea.ResetAreas();
        await main.Pending;
        var pen = await bench.PenAsync();
        Assert.Equal(RectD.Full, pen.ScreenArea);
        Assert.True(pen.Matrix.IsCloseTo(PenMatrix.Identity));
        Assert.Equal((ScreenTargetKind.OneScreen, "DP-2", true), (pen.TargetKind, pen.TargetScreen, pen.Flipped));
        Assert.True(main.WorkArea.IsWholeScreen && main.WorkArea.IsFullArea);
        Assert.Equal(RectD.Full, bench.Store.Load().Geometry.ActiveArea);
    });

    [Fact]
    public Task The_pen_reset_puts_pressure_mouse_mode_and_this_profiles_pen_buttons_back() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Dialogs.Action = _ => PadAction.Shortcut("E");
        await main.Pen.EditLower();
        await main.Pen.UsePresetCommand.ExecuteAsync("6");
        main.Pen.MouseMode = true;
        await main.Pending;
        Assert.Equal("E", main.Config.DefaultProfile.PenLower.Label);
        Assert.True((await bench.PenAsync()).MouseMode);

        bench.Dialogs.Confirm = false;
        await main.Pen.ResetPen();
        Assert.Equal("E", main.Config.DefaultProfile.PenLower.Label);

        bench.Dialogs.Confirm = true;
        await main.Pen.ResetPen();
        await main.Pending;
        var pen = await bench.PenAsync();
        Assert.True(pen.Pressure.Curve.IsCloseTo(PressureCurve.Linear));
        Assert.False(pen.MouseMode);
        Assert.Equal(ActionKind.Default, main.Config.DefaultProfile.PenLower.Kind);
        // The work area is not the pen page's to reset.
        Assert.Equal((ScreenTargetKind.OneScreen, "DP-2", true), (pen.TargetKind, pen.TargetScreen, pen.Flipped));
    });

    [Fact]
    public Task The_eye_puts_the_labels_away_remembers_it_and_leaves_the_keys_to_click() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        Assert.True(main.Home.LabelsShown);

        await main.Home.ToggleLabels();
        Assert.False(main.Home.LabelsShown);
        Assert.True(bench.Store.LoadState().LabelsHidden);
        Assert.Equal(Profile.KeyCount, main.Home.KeyLabels.Count);

        await main.Home.ToggleLabels();
        Assert.True(main.Home.LabelsShown);
        Assert.False(bench.Store.LoadState().LabelsHidden);
    });

    [Fact]
    public Task Smoothing_reloads_the_driver_once_when_the_slider_is_let_go() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev/rules.d"));
        Directory.CreateDirectory(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf"));
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev/rules.d/70-xperience.rules"), "ENV{XPERIENCE_PEN_SMOOTHING}=\"3\"\n");
        File.WriteAllText(Path.Combine(bench.SystemFolder, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
        var main = bench.Main;
        await main.RefreshAsync();
        Assert.Equal((3.0, "3 positions"), (main.Pen.Smoothing, main.Pen.SmoothingText));

        // Dragging reloads nothing; letting go does, once, and letting go where it already is does not.
        main.Pen.Smoothing = 5;
        main.Pen.Smoothing = 6;
        Assert.Empty(bench.Elevated);
        await main.Pen.ApplySmoothingAsync();
        await main.Pending;
        Assert.Equal([$"install {DriverSetup.ObjectName} eraser on button none 6"], bench.Elevated);

        main.Pen.Smoothing = 3;
        await main.Pen.ApplySmoothingAsync();
        await main.Pending;
        Assert.Single(bench.Elevated);

        main.Pen.Smoothing = 1;
        Assert.Equal("Off", main.Pen.SmoothingText);
        await main.Pen.ApplySmoothingAsync();
        await main.Pending;
        Assert.Equal($"install {DriverSetup.ObjectName} eraser on button none 1", bench.Elevated[^1]);
    });

    [Fact]
    public Task Removing_the_driver_asks_first_and_runs_the_helper() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven);
        var main = bench.Main;

        bench.Dialogs.Confirm = false;
        await main.Settings.RemoveDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Empty(bench.Elevated);

        bench.Dialogs.Confirm = true;
        await main.Settings.RemoveDriverCommand.ExecuteAsync(null);
        await main.Pending;
        Assert.Equal(["remove"], bench.Elevated);
    });

    [Fact]
    public Task A_custom_screen_area_is_written_as_picked() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Dialogs.Area = _ => new RectD(0.25, 0.1, 0.5, 0.8);

        await main.WorkArea.CustomScreenArea();

        var request = bench.Dialogs.LastArea!;
        Assert.Equal((3440.0, 1440.0, "pixels", RectD.Full), (request.UnitsWide, request.UnitsHigh, request.Unit, request.Area));
        Assert.Equal(new RectD(0.25, 0.1, 0.5, 0.8), (await bench.PenAsync()).ScreenArea);
        Assert.True(main.WorkArea.IsCustomScreen);
        Assert.False(main.WorkArea.IsWholeScreen);
        Assert.Equal(["area 0.25 0.1 0.5 0.8"], bench.Desktop.Writes);
    });

    [Fact]
    public Task On_a_screen_turned_upside_down_the_area_is_picked_as_seen_and_kept_as_the_panel_has_it() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.ScreenName == "DP-5");
        await main.Pending;
        // The top left quarter of what is seen is the bottom right quarter of the panel.
        bench.Dialogs.Area = _ => new RectD(0, 0, 0.5, 0.5);

        await main.WorkArea.CustomScreenArea();
        Assert.True((await bench.PenAsync()).ScreenArea.IsCloseTo(new RectD(0.5, 0.5, 0.5, 0.5), 1e-9));

        // Asked again, the dialog starts from what is seen.
        await main.WorkArea.CustomScreenArea();
        Assert.True(bench.Dialogs.LastArea!.Area.IsCloseTo(new RectD(0, 0, 0.5, 0.5), 1e-9));
    });

    [Fact]
    public Task Across_all_monitors_the_area_is_picked_on_a_picture_of_the_whole_desktop() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.Kind == ScreenTargetKind.AllScreens);
        await main.Pending;
        bench.Dialogs.Area = _ => new RectD(0, 0, 0.5, 1);

        await main.WorkArea.CustomScreenArea();

        var request = bench.Dialogs.LastArea!;
        Assert.Equal(4, request.Parts!.Count);
        Assert.Equal(10223.0, request.UnitsWide);
        Assert.Equal(new RectD(0, 0, 0.5, 1), (await bench.PenAsync()).ScreenArea);
    });

    [Fact]
    public Task Proportion_uses_the_part_of_the_tablet_shaped_like_the_screen_and_circles_stay_round() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;

        await main.WorkArea.ProportionCommand.ExecuteAsync(null);

        double height = (341.0 / 191.8) / (3440.0 / 1440.0);
        var expected = new RectD(0, (1 - height) / 2, 1, height);
        Assert.True(main.Config.Geometry.ActiveArea.IsCloseTo(expected, 1e-9), main.Config.Geometry.ActiveArea.ToString());
        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(PenMatrix.ForActiveArea(expected), 1e-6));
        Assert.True(main.WorkArea.IsProportional);
        Assert.False(main.WorkArea.IsFullArea);
        Assert.Contains("stays a circle", main.WorkArea.ShapeNote);
        Assert.True(main.WorkArea.ActiveArea.IsCloseTo(expected, 1e-9));
        // The desktop's flip was already right and is not written again.
        Assert.DoesNotContain(bench.Desktop.Writes, w => w.StartsWith("flipped", StringComparison.Ordinal));

        await main.WorkArea.FullAreaCommand.ExecuteAsync(null);
        Assert.True(main.WorkArea.IsFullArea);
        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(PenMatrix.Identity));
    });

    [Fact]
    public Task A_custom_active_area_is_picked_in_millimeters_and_becomes_the_pens_transform() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Dialogs.Area = _ => new RectD(0.5, 0.5, 0.5, 0.5);

        await main.WorkArea.CustomActiveArea();

        var request = bench.Dialogs.LastArea!;
        Assert.Equal((341.0, 191.8, "mm", true), (request.UnitsWide, request.UnitsHigh, request.Unit, request.DarkSurface));
        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(new PenMatrix(2, 0, -1, 0, 2, -1), 1e-6));
        Assert.True(main.WorkArea.IsCustomArea);
        Assert.Equal(new RectD(0.5, 0.5, 0.5, 0.5), bench.Store.Load().Geometry.ActiveArea);
    });

    [Fact]
    public Task A_quarter_turn_goes_into_the_pens_transform_and_out_again() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        Assert.Equal([TabletTurn.None, TabletTurn.Quarter, TabletTurn.Half, TabletTurn.ThreeQuarter], main.WorkArea.Turns.Select(t => t.Turn));

        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.Quarter);
        await main.Pending;

        Assert.Equal(["flipped False", "matrix 0 -1 1 1 0 0"], bench.Desktop.Writes);
        Assert.Equal(TabletTurn.Quarter, main.WorkArea.Turn);
        Assert.Equal(TabletTurn.Quarter, (await bench.PenAsync()).Turn);
        Assert.False(main.WorkArea.Turned);

        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.ThreeQuarter);
        await main.Pending;
        Assert.Equal("matrix 0 1 0 -1 0 1", bench.Desktop.Writes[^1]);

        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.Half);
        await main.Pending;
        Assert.Equal(["flipped True", "matrix 1 0 0 0 1 0"], bench.Desktop.Writes.TakeLast(2));
        Assert.Equal(TabletTurn.Half, main.WorkArea.Turn);
        Assert.True(main.WorkArea.Turned);
    });

    [Fact]
    public Task A_pen_that_cannot_take_a_transform_offers_only_the_two_turns_the_desktop_does() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Desktop.SetPen((await bench.PenAsync()) with { CanSetMatrix = false });
        await main.RefreshAsync();

        Assert.Equal([TabletTurn.None, TabletTurn.Half], main.WorkArea.Turns.Select(t => t.Turn));
        Assert.False(main.WorkArea.CanSetGeometry);
        await main.WorkArea.ProportionCommand.ExecuteAsync(null);
        await main.WorkArea.CustomActiveArea();
        Assert.DoesNotContain(bench.Desktop.Writes, w => w.StartsWith("matrix", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_calibration_someone_else_set_is_called_out_and_replaced_only_when_asked() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        bench.Desktop.SetPen((await bench.PenAsync()) with { Matrix = new PenMatrix(1.5, 0, -0.1, 0, 1.2, 0) });
        await main.RefreshAsync();

        Assert.Contains("did not set", main.WorkArea.GeometryNote);
        Assert.False(main.WorkArea.IsFullArea);
        Assert.Empty(bench.Desktop.Writes);

        await main.WorkArea.FullAreaCommand.ExecuteAsync(null);
        Assert.Equal("", main.WorkArea.GeometryNote);
        Assert.True(main.WorkArea.IsFullArea);
        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(PenMatrix.Identity));
    });

    [Fact]
    public Task Precision_mode_ends_when_the_area_is_set_by_hand() => OnUi(async () =>
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p.WithKey(1, PadAction.Do(SpecialAct.PrecisionMode)));
        using var bench = await Bench.OpenAsync(Bench.Driven, config, upright: true);
        var main = bench.Main;

        await bench.Hub.KeyAsync(1, true);
        Assert.True(bench.Hub.Precise);
        await main.RefreshAsync();
        Assert.True(main.WorkArea.IsCustomScreen);

        await main.WorkArea.WholeScreenCommand.ExecuteAsync(null);
        await bench.Hub.SyncAsync();
        Assert.False(bench.Hub.Precise);
        Assert.Equal(RectD.Full, (await bench.PenAsync()).ScreenArea);
    });

    [Fact]
    public Task Pressure_off_makes_every_touch_full_and_on_brings_the_response_back() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        main.Pen.Curve = PressureCurve.Through(0.6, 0.3);
        main.Pen.RangeMin = 0.1;
        await main.Pen.ApplyAsync();

        main.Pen.PressureOn = false;
        await UntilAsync(() => bench.Desktop.Writes.Any(w => w.Contains("0-0.002")), "the squeezed range was written");
        await main.Pending;
        var off = (await bench.PenAsync()).Pressure;
        Assert.True(off.RangeMax <= 0.002);
        Assert.False(main.Pen.PressureOn);
        Assert.False(main.Pen.IsDefault);
        Assert.Equal(-1, main.Pen.Preset);

        main.Pen.PressureOn = true;
        await UntilAsync(() => bench.Desktop.Writes[^1].Contains("0.1-1"), "the kept response was written back");
        await main.Pending;
        var on = (await bench.PenAsync()).Pressure;
        Assert.Equal((0.1, 1.0), (on.RangeMin, on.RangeMax));
        Assert.True(on.Curve.IsCloseTo(PressureCurve.Through(0.6, 0.3)));
        Assert.True(main.Pen.PressureOn);
    });

    [Fact]
    public Task The_mouse_mode_speed_is_written_when_the_slider_is_let_go() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        Assert.True(main.Pen.CanSetMouseSpeed);

        main.Pen.MouseSpeed = 0.4;
        Assert.Empty(bench.Desktop.Writes);
        await main.Pen.ApplySpeedAsync();

        Assert.Equal(["speed 0.4"], bench.Desktop.Writes);
        Assert.Equal(0.4, main.Pen.MouseSpeed);
    });

    [Fact]
    public Task The_floating_menus_buttons_and_layout_are_set_on_its_page() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;
        Assert.Equal(["Ctrl+Shift+Z", "Ctrl+Z", "Ctrl+Shift+N", "Ctrl+E", "]", "[", "Ctrl+S", "Show Xperience"], main.FloatingMenu.Labels);
        ActionRequest? asked = null;
        bench.Dialogs.Action = request =>
        {
            asked = request;
            return PadAction.Run("krita") with { Name = "Krita" };
        };

        await main.FloatingMenu.EditButton(2);
        await main.FloatingMenu.SetVerticalCommand.ExecuteAsync("true");

        Assert.True(asked!.ForMenu);
        Assert.Equal("Krita", main.FloatingMenu.Labels[2]);
        Assert.True(main.FloatingMenu.Vertical);
        var saved = bench.Store.Load().FloatingMenu;
        Assert.Equal(("Krita", true), (saved.Buttons[2].Label, saved.Vertical));
        Assert.Empty(bench.Desktop.Writes);

        int asks = 0;
        main.FloatingMenu.ShowRequested += () => asks++;
        main.FloatingMenu.ShowMenuCommand.Execute(null);
        Assert.Equal(1, asks);
    });

    [Fact]
    public Task Calibrating_turns_the_taps_into_a_correction_and_can_be_undone() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        using var node = new FakeNode(bench.Folder, "pen");
        bench.Presence = Bench.BuiltIn with { PenNode = node.Path };
        var main = bench.Main;
        await main.RefreshAsync();
        Assert.True(main.CanReadPen);
        Assert.True(main.WorkArea.CanCalibrate);

        var truth = new PenMatrix(1.02, 0.01, -0.012, -0.008, 0.985, 0.02);
        bench.Dialogs.Calibration = request =>
        {
            Assert.Equal(("DP-2", true), (request.Screen.Name, request.Flipped));
            var wanted = Calibration.Crosses.Select(c => Calibration.Wanted(c, request.Screen.Rotation, request.ScreenArea)).ToList();
            return new CalibrationResult([.. wanted.Select(w => truth.Inverse()!.Value.Apply(w.X, w.Y))], wanted);
        };

        await main.WorkArea.Calibrate();
        await main.Pending;

        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(truth, 1e-5), (await bench.PenAsync()).Matrix.ToString());
        Assert.True(main.WorkArea.IsCalibrated);
        Assert.Null(main.Problem);
        Assert.True(PenMatrix.FromList(bench.Store.Load().Geometry.Correction).IsCloseTo(truth, 1e-9));

        // The active area can change without losing the calibration.
        await main.WorkArea.ProportionCommand.ExecuteAsync(null);
        Assert.True(main.WorkArea.IsCalibrated);
        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(truth.After(PenMatrix.ForActiveArea(main.WorkArea.ActiveArea)), 1e-5));

        await main.WorkArea.ResetCalibrationCommand.ExecuteAsync(null);
        Assert.False(main.WorkArea.IsCalibrated);
        Assert.True((await bench.PenAsync()).Matrix.IsCloseTo(PenMatrix.ForActiveArea(main.WorkArea.ActiveArea), 1e-5));
    });

    [Fact]
    public Task Taps_that_do_not_line_up_change_nothing_and_say_why() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        using var node = new FakeNode(bench.Folder, "pen");
        bench.Presence = Bench.BuiltIn with { PenNode = node.Path };
        var main = bench.Main;
        await main.RefreshAsync();
        bench.Dialogs.Calibration = request =>
        {
            var wanted = Calibration.Crosses.ToList();
            return new CalibrationResult([wanted[2], wanted[3], wanted[0], wanted[1], wanted[4]], wanted);
        };

        await main.WorkArea.Calibrate();
        await main.Pending;

        Assert.Contains("did not line up", main.Problem);
        Assert.Empty(bench.Desktop.Writes);
        Assert.False(main.WorkArea.IsCalibrated);
    });

    [Fact]
    public Task Calibration_is_refused_with_a_reason_when_the_pen_cannot_be_read_or_follows_the_active_monitor() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;

        await main.WorkArea.Calibrate();
        Assert.Contains("no access to the pen's input device", main.Problem);
        Assert.DoesNotContain(bench.Dialogs.Asked, a => a.StartsWith("calibrate", StringComparison.Ordinal));

        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.Kind == ScreenTargetKind.ActiveScreen);
        await main.Pending;
        await main.WorkArea.Calibrate();
        Assert.Contains("needs the pen mapped to one monitor", main.Problem);
    });

    [Fact]
    public Task A_pen_button_read_from_the_pens_own_node_reaches_its_special_action() => OnUi(async () =>
    {
        var config = XperienceConfig.Default.Completed().Change("", p => p with { PenLower = PadAction.Do(SpecialAct.PrecisionMode) });
        using var bench = await Bench.OpenAsync(config: config);
        using var node = new FakeNode(bench.Folder, "pen");
        bench.Presence = Bench.BuiltIn with { PenNode = node.Path };
        await bench.Main.RefreshAsync();

        // The pen comes near, then its lower button goes down.
        node.Send(1, 0x140, 1);
        node.Sync();
        node.Send(1, 0x14b, 1);
        node.Sync();

        await UntilAsync(() => bench.Hub.Precise, "precision mode came on from the pen's button");
        await bench.Hub.SyncAsync();
        var area = (await bench.PenAsync()).ScreenArea;
        Assert.True(area.IsCloseTo(new RectD(1 / 3.0, 1 / 3.0, 1 / 3.0, 1 / 3.0)), area.ToString());

        // Letting go does nothing; pressing again leaves precision mode.
        node.Send(1, 0x14b, 0);
        node.Sync();
        node.Send(1, 0x14b, 1);
        node.Sync();
        await UntilAsync(() => !bench.Hub.Precise, "precision mode went off on the second press");
    });

    [Fact]
    public Task A_key_read_from_the_tablets_own_node_reaches_the_hub() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync(Bench.Driven, upright: true);
        using var node = new FakeNode(bench.Folder, "pad");
        bench.Presence = Bench.Driven with { PadNode = node.Path };
        await bench.Main.RefreshAsync();

        // The first key down and up, with a key repeat and a dial step between that are not key presses.
        node.Send(1, 0x100, 1);
        node.Send(1, 0x100, 2);
        node.Send(2, 8, 1);
        node.Sync();
        node.Send(1, 0x100, 0);
        node.Sync();
        node.Send(1, 0x103, 1);
        node.Sync();

        await UntilAsync(() => bench.Shown.Count == 1 && bench.Desktop.Hints.Contains("Dial 1: Scroll"), "the show key and the dial switch key both arrived");
        Assert.Equal(["window"], bench.Shown);
        Assert.Equal("Scroll", bench.Desktop.Binding(Rebind.Dials, "0"));
    });
}
