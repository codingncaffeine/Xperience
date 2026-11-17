using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Xperience.App.Controls;
using Xperience.App.ViewModels;
using Xperience.App.Views;
using Xperience.App.Views.Dialogs;
using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The drawn controls and the dialogs, used the way a person uses them: keys pressed, the mouse clicked and dragged.</summary>
public class ControlsTests
{
    static Task OnUi(Func<Task> body) => HeadlessApp.OnUi(body);

    static Window Host(Control content, double width = 600, double height = 400)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task The_shortcut_box_takes_what_is_pressed() => OnUi(() =>
    {
        var box = new ShortcutBox();
        var window = Host(box);
        box.Focus();

        Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("Ctrl+Z", box.Keys);

        // With room for one, the next press replaces it.
        Press(window, Key.OemOpenBrackets, PhysicalKey.BracketLeft);
        Assert.Equal("[", box.Keys);

        Press(window, Key.S, PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift | RawInputModifiers.Alt);
        Assert.Equal("Ctrl+Alt+Shift+S", box.Keys);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_modifier_pressed_and_let_go_alone_is_a_shortcut_of_its_own() => OnUi(() =>
    {
        var box = new ShortcutBox();
        var window = Host(box);
        box.Focus();

        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Alt", box.Keys);

        // A modifier held for a key is part of that key's shortcut and nothing more.
        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
        window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
        window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ctrl+S", box.Keys);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_box_with_room_for_many_collects_them_in_order_up_to_its_limit() => OnUi(() =>
    {
        var box = new ShortcutBox { Max = 3, AddSuper = true };
        var window = Host(box);
        box.Focus();

        Press(window, Key.C, PhysicalKey.C, RawInputModifiers.Control);
        box.AddSuper = false;
        Press(window, Key.V, PhysicalKey.V, RawInputModifiers.Control);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Press(window, Key.X, PhysicalKey.X);
        Assert.Equal("Super+Ctrl+C Ctrl+V Tab", box.Keys);

        box.Undo();
        Assert.Equal("Super+Ctrl+C Ctrl+V", box.Keys);
        box.Clear();
        Assert.Equal("", box.Keys);
        // A key that has no name here is not taken.
        Press(window, Key.VolumeUp, PhysicalKey.AudioVolumeUp);
        Assert.Equal("", box.Keys);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Clicking_a_key_or_a_dial_on_the_tablets_picture_asks_to_change_it() => OnUi(() =>
    {
        var keys = new List<int>();
        var dials = new List<int>();
        var art = new TabletArt
        {
            Model = TabletModel.Artist16Third,
            KeyLabels = ["One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight"],
            DialLabels = ["Zoom", "Brush size"],
            KeyCommand = new RelayCommand<int>(keys.Add),
            DialCommand = new RelayCommand<int>(dials.Add),
        };
        var window = Host(art, 900, 560);
        Assert.Equal((8, 2), (art.KeyHits.Count, art.DialHits.Count));

        Point Of(Rect hit) => art.TranslatePoint(hit.Center, window)!.Value;
        window.MouseDown(Of(art.KeyHits[3]), MouseButton.Left);
        window.MouseUp(Of(art.KeyHits[3]), MouseButton.Left);
        window.MouseDown(Of(art.DialHits[1]), MouseButton.Left);
        window.MouseUp(Of(art.DialHits[1]), MouseButton.Left);
        // The middle of the screen is neither.
        window.MouseDown(art.TranslatePoint(new Point(art.Bounds.Width / 2, art.Bounds.Height / 2), window)!.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([3], keys);
        Assert.Equal([1], dials);

        // Turned around, the same key is on the other side and still found.
        art.Turned = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(art.KeyHits[0].Center.X > art.Bounds.Width / 2);
        window.MouseDown(Of(art.KeyHits[0]), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([3, 0], keys);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dragging_the_frame_moves_it_and_dragging_a_corner_resizes_it() => OnUi(() =>
    {
        var picker = new AreaPicker { Area = new RectD(0.25, 0.25, 0.5, 0.5), SurfaceAspect = 2 };
        int edits = 0;
        picker.Edited += (_, _) => edits++;
        var window = Host(picker, 816, 416);
        // The surface is 800 by 400, eight in from each edge.
        Point At(double x, double y) => picker.TranslatePoint(new Point(8 + x * 800, 8 + y * 400), window)!.Value;

        window.MouseDown(At(0.5, 0.5), MouseButton.Left);
        window.MouseMove(At(0.6, 0.55));
        window.MouseUp(At(0.6, 0.55), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(picker.Area.IsCloseTo(new RectD(0.35, 0.3, 0.5, 0.5), 0.002), picker.Area.ToString());
        Assert.Equal(1, edits);

        // The bottom right corner, pulled far past the surface's edge, stops at it.
        window.MouseDown(At(0.85, 0.8), MouseButton.Left);
        window.MouseMove(At(1.5, 1.5));
        window.MouseUp(At(1.5, 1.5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(picker.Area.IsCloseTo(new RectD(0.35, 0.3, 0.65, 0.7), 0.002), picker.Area.ToString());

        // The left edge, pushed past the right one, leaves the smallest frame there is.
        window.MouseDown(At(0.35, 0.6), MouseButton.Left);
        window.MouseMove(At(2, 0.6));
        window.MouseUp(At(2, 0.6), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.05, picker.Area.Width, 3);
        Assert.Equal(1.0, picker.Area.Right, 3);
        Assert.Equal(3, edits);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void The_assignment_dialog_builds_each_kind_of_action_and_says_who_carries_it_out()
    {
        var model = new ActionDialogViewModel(new ActionRequest("Key 8", PadAction.Shortcut("Ctrl+Z"), PadAction.Shortcut("Ctrl+Z")));
        Assert.True(model.IsShortcut);
        Assert.Equal("Ctrl+Z", model.Keys);
        Assert.Contains("desktop carries this out", model.Note);
        Assert.True(model.ShowMouse);
        Assert.DoesNotContain(model.Others, o => o.Kind == ActionKind.Default);

        model.Keys = "Ctrl+C Ctrl+V";
        model.Name = "  Copy and paste ";
        Assert.Equal(PadAction.Shortcut("Ctrl+C Ctrl+V") with { Name = "Copy and paste" }, model.Build());
        Assert.Contains("Xperience carries this out", model.Note);

        model.IsShortcut = false;
        model.IsMouse = true;
        model.MouseIndex = (int)MouseAct.MiddleClick;
        model.Shift = true;
        model.Name = "";
        Assert.Equal(PadAction.Click(MouseAct.MiddleClick, MouseMods.Shift), model.Build());
        Assert.Contains("desktop carries this out", model.Note);
        model.Space = true;
        Assert.Contains("Xperience carries this out", model.Note);

        model.IsMouse = false;
        model.IsRun = true;
        model.App = " /usr/bin/krita ";
        Assert.Equal(PadAction.Run("/usr/bin/krita"), model.Build());

        model.IsRun = false;
        model.IsOther = true;
        model.Other = model.Others.Single(o => o.Special == SpecialAct.PrecisionMode && o.Kind == ActionKind.Special);
        Assert.Equal(PadAction.Do(SpecialAct.PrecisionMode), model.Build());
        model.Other = model.Others.Single(o => o.Kind == ActionKind.Disabled);
        Assert.Equal(PadAction.Off, model.Build());
        Assert.Equal("The control does nothing.", model.Note);

        model.UseDefaultCommand.Execute(null);
        Assert.Equal(PadAction.Shortcut("Ctrl+Z"), model.Build());
        Assert.True(model.IsShortcut);
    }

    [Fact]
    public void Nothing_entered_is_a_control_switched_off_not_a_broken_assignment()
    {
        var model = new ActionDialogViewModel(new ActionRequest("Key 1", PadAction.Off, PadAction.Off));
        Assert.True(model.IsOther);
        model.IsOther = false;
        model.IsShortcut = true;
        Assert.Equal(PadAction.Off, model.Build());
        model.IsShortcut = false;
        model.IsRun = true;
        model.App = "   ";
        Assert.Equal(PadAction.Off, model.Build());
    }

    [Fact]
    public void A_pen_button_can_stay_a_pen_button_and_a_menu_button_has_no_mouse_actions()
    {
        var pen = new ActionDialogViewModel(new ActionRequest("Lower pen button", PadAction.PenButton, PadAction.PenButton, ForPenButton: true));
        Assert.True(pen.IsOther);
        Assert.Equal(ActionKind.Default, pen.Other!.Kind);
        Assert.Equal(PadAction.PenButton, pen.Build());
        Assert.Contains("stays a plain pen button", pen.Note);

        var menu = new ActionDialogViewModel(new ActionRequest("Floating menu button 1", PadAction.Click(MouseAct.RightClick), PadAction.Off, ForMenu: true));
        Assert.False(menu.ShowMouse);
        Assert.False(menu.IsMouse);
        Assert.Equal("", menu.Note);
    }

    [Fact]
    public void The_dial_dialog_builds_four_functions_and_which_one_is_in_use()
    {
        var model = new DialDialogViewModel("Dial 1", Xperience.Core.Settings.Profile.DefaultDials[0], 2, Xperience.Core.Settings.Profile.DefaultDials[0]);
        Assert.Equal([false, false, true, false], model.Slots.Select(s => s.InUse));
        Assert.Equal(["Function 1", "Function 2", "Function 3", "Function 4"], model.Slots.Select(s => s.Heading));

        model.Slots[1].KindIndex = (int)DialKind.Custom;
        Assert.True(model.Slots[1].IsCustom);
        model.Slots[1].Counterclockwise = "Ctrl+Z";
        model.Slots[1].Clockwise = "Ctrl+Shift+Z";
        model.Slots[3].KindIndex = (int)DialKind.Disabled;
        model.Slots[2].InUse = false;
        model.Slots[1].InUse = true;

        var (slots, current) = model.Build();
        Assert.Equal(1, current);
        Assert.Equal(["Zoom", "Ctrl+Z / Ctrl+Shift+Z", "Brush size", "Disabled"], slots.Select(s => s.Label));

        model.UseDefaultCommand.Execute(null);
        (slots, current) = model.Build();
        Assert.Equal(0, current);
        Assert.Equal(["Zoom", "Scroll", "Brush size", "Rotate"], slots.Select(s => s.Label));
    }

    [Fact]
    public void The_area_dialogs_numbers_and_its_picture_are_the_same_rectangle()
    {
        var model = new AreaDialogViewModel(new AreaRequest("Customize screen area", "", new RectD(0.25, 0.5, 0.5, 0.25), 3440, 1440, "pixels", 0));
        Assert.Equal((860m, 720m, 1720m, 360m), (model.X, model.Y, model.Width, model.Height));

        // A number typed in moves the picture.
        model.X = 0;
        model.Width = 3440;
        Assert.Equal(new RectD(0, 0.5, 1, 0.25), model.Area);

        // The picture dragged changes the numbers.
        model.Area = new RectD(0.5, 0, 0.5, 1);
        Assert.Equal((1720m, 0m, 1720m, 1440m), (model.X, model.Y, model.Width, model.Height));

        // Numbers that do not fit are brought back onto the surface.
        model.X = 3000;
        Assert.True(model.Area.Right <= 1.0000001, model.Area.ToString());

        model.UseWholeCommand.Execute(null);
        Assert.Equal((RectD.Full, 0m, 3440m), (model.Area, model.X, model.Width));

        var millimeters = new AreaDialogViewModel(new AreaRequest("Customize active area", "", new RectD(0.5, 0.5, 0.5, 0.5), 341.0, 191.8, "mm", 1));
        Assert.Equal((170.5m, 95.9m, "F1"), (millimeters.X, millimeters.Y, millimeters.Format));
    }

    [Fact]
    public Task Every_dialog_renders() => OnUi(() =>
    {
        Window[] dialogs =
        [
            new ActionDialog { DataContext = new ActionDialogViewModel(new ActionRequest("Key 8", PadAction.Shortcut("Ctrl+Z"), PadAction.Shortcut("Ctrl+Z"))) },
            new ActionDialog { DataContext = new ActionDialogViewModel(new ActionRequest("Key 1", PadAction.Click(MouseAct.MiddleClick, MouseMods.Shift), PadAction.Off)) },
            new ActionDialog { DataContext = new ActionDialogViewModel(new ActionRequest("Key 2", PadAction.Run("/usr/bin/krita"), PadAction.Off)) },
            new ActionDialog { DataContext = new ActionDialogViewModel(new ActionRequest("Lower pen button", PadAction.PenButton, PadAction.PenButton, ForPenButton: true)) },
            new DialDialog { DataContext = new DialDialogViewModel("Dial 1", Xperience.Core.Settings.Profile.DefaultDials[0], 0, Xperience.Core.Settings.Profile.DefaultDials[0]) },
            new AreaDialog { DataContext = new AreaDialogViewModel(new AreaRequest("Customize screen area", "Drag the frame.", new RectD(0.2, 0.2, 0.5, 0.5), 3440, 1440, "pixels", 0)) },
            new AskDialog("Remove profile", "Remove the profile for Krita?", "Remove"),
        ];
        foreach (var dialog in dialogs)
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            // A layout pass and a render pass with every binding of the dialog live.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.IsVisible);
            Assert.True(dialog.Bounds.Width > 100 && dialog.Bounds.Height > 100, dialog.Bounds.ToString());
            dialog.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_floating_menu_has_a_button_per_action_and_a_click_carries_it_out() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var menu = new FloatingMenuWindow(bench.Main);
        menu.ShowQuietly();
        Dispatcher.UIThread.RunJobs();

        var buttons = menu.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();
        Assert.Equal(["Ctrl+Shift+Z", "Ctrl+Z", "Ctrl+Shift+N", "Ctrl+E", "]", "[", "Ctrl+S", "Show Xperience"],
            buttons.Select(b => ((TextBlock)b.Content!).Text));

        var center = buttons[6].TranslatePoint(new Point(buttons[6].Bounds.Width / 2, buttons[6].Bounds.Height / 2), menu)!.Value;
        menu.MouseDown(center, MouseButton.Left);
        menu.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await bench.Hub.SyncAsync();
        Assert.Equal(["+29", "+31", "-31", "-29"], bench.Injector.Typed);

        // A change to the settings shows on the menu the next time it is filled.
        await bench.Main.ChangeConfigAsync(c => c with { FloatingMenu = c.FloatingMenu with { Vertical = true, Buttons = [.. c.FloatingMenu.Buttons.Select((b, i) => i == 0 ? PadAction.Shortcut("E") : b)] } });
        menu.Fill();
        Dispatcher.UIThread.RunJobs();
        buttons = [.. menu.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile"))];
        Assert.Equal("E", ((TextBlock)buttons[0].Content!).Text);
        menu.Close();
    });
}
