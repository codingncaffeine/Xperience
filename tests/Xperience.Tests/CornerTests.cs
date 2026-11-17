using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Xperience.App.ViewModels;
using Xperience.App.Views.Dialogs;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>Picking an area by its two corners: the arithmetic, the dialog's side of it, and the veil itself.</summary>
public class CornerTests
{
    static Task OnUi(Func<Task> body) => HeadlessApp.OnUi(body);

    [Theory]
    [InlineData(0.2, 0.1, 0.7, 0.9)]
    [InlineData(0.7, 0.9, 0.2, 0.1)]
    [InlineData(0.7, 0.1, 0.2, 0.9)]
    public void Two_corners_make_the_area_between_them_whichever_way_round(double x1, double y1, double x2, double y2)
    {
        var area = AreaMath.FromCorners((x1, y1), (x2, y2));
        Assert.NotNull(area);
        Assert.True(area.Value.IsCloseTo(new RectD(0.2, 0.1, 0.5, 0.8), 1e-12), area.ToString());
    }

    [Fact]
    public void Corners_off_the_surface_are_pulled_onto_it_and_a_sliver_is_no_area()
    {
        Assert.True(AreaMath.FromCorners((-0.3, 0.5), (0.4, 1.7))!.Value.IsCloseTo(new RectD(0, 0.5, 0.4, 0.5), 1e-12));
        Assert.Null(AreaMath.FromCorners((0.5, 0.5), (0.52, 0.9)));
        Assert.Null(AreaMath.FromCorners((0.1, 0.5), (0.9, 0.53)));
        Assert.NotNull(AreaMath.FromCorners((0.1, 0.5), (0.9, 0.5 + AreaMath.SmallestSide + 1e-9)));
    }

    [Fact]
    public void A_tap_is_placed_in_the_frame_the_active_area_is_given_in()
    {
        // Unturned: where the tablet says. Turned around: mirrored both ways.
        Assert.Equal((0.2, 0.3), AreaMath.TapOnSurface((0.2, 0.3), false, TabletTurn.None));
        var half = AreaMath.TapOnSurface((0.2, 0.3), true, TabletTurn.None);
        Assert.Equal((0.8, 0.7), (Math.Round(half.X, 12), Math.Round(half.Y, 12)));

        // A quarter turn puts the keys at the top: the surface's top left as built is then the top right.
        var quarter = AreaMath.TapOnSurface((0, 0), false, TabletTurn.Quarter);
        Assert.Equal((1.0, 0.0), quarter);
        // The same frame the pen's transform uses, so an area picked by taps is the area the transform cuts out.
        var area = AreaMath.FromCorners(AreaMath.TapOnSurface((0.1, 0.6), false, TabletTurn.Quarter), AreaMath.TapOnSurface((0.5, 0.2), false, TabletTurn.Quarter))!.Value;
        var matrix = PenMatrix.Compose(TabletTurn.Quarter, area, PenMatrix.Identity);
        var first = matrix.Apply(0.1, 0.6);
        var second = matrix.Apply(0.5, 0.2);
        Assert.Equal((0.0, 0.0, 1.0, 1.0), (Math.Round(Math.Min(first.X, second.X), 9), Math.Round(Math.Min(first.Y, second.Y), 9),
            Math.Round(Math.Max(first.X, second.X), 9), Math.Round(Math.Max(first.Y, second.Y), 9)));
    }

    static ScreenInfo Monitor() => new("DP-2", 1, new RectD(0, 0, 3440, 1440), 1, ScreenRotation.None);

    [Fact]
    public Task The_dialog_takes_the_area_picked_by_corners_and_shows_its_numbers() => OnUi(async () =>
    {
        CornerRequest? asked = null;
        var request = new AreaRequest("Customize screen area", "", RectD.Full, 3440, 1440, "pixels", 0) { Corners = new CornerRequest(Monitor(), WithPen: false) };
        var dialog = new AreaDialogViewModel(request, corners =>
        {
            asked = corners;
            return Task.FromResult<RectD?>(new RectD(0.25, 0.5, 0.5, 0.25));
        });
        Assert.True(dialog.CanPickCorners);
        Assert.Equal("Click to set screen area", dialog.CornersLabel);

        await dialog.PickCornersCommand.ExecuteAsync(null);

        Assert.Same(request.Corners, asked);
        Assert.Equal(new RectD(0.25, 0.5, 0.5, 0.25), dialog.Area);
        Assert.Equal((860m, 720m, 1720m, 360m), (dialog.X, dialog.Y, dialog.Width, dialog.Height));
    });

    [Fact]
    public Task Leaving_the_veil_keeps_the_dialogs_area_and_no_offer_means_no_picking() => OnUi(async () =>
    {
        var start = new RectD(0.1, 0.1, 0.5, 0.5);
        var left = new AreaDialogViewModel(new AreaRequest("Customize active area", "", start, 341, 191.8, "mm", 1, DarkSurface: true)
            { Corners = new CornerRequest(Monitor(), WithPen: true) }, _ => Task.FromResult<RectD?>(null));
        Assert.Equal("Click to set the active area", left.CornersLabel);
        await left.PickCornersCommand.ExecuteAsync(null);
        Assert.Equal(start, left.Area);

        int asks = 0;
        var none = new AreaDialogViewModel(new AreaRequest("Customize active area", "", start, 341, 191.8, "mm", 1, DarkSurface: true)
            { CornersUnavailable = "Needs one monitor." }, _ => { asks++; return Task.FromResult<RectD?>(RectD.Full); });
        Assert.False(none.CanPickCorners);
        Assert.Equal("Needs one monitor.", none.CornersNote);
        Assert.Equal("Click to set the active area", none.CornersLabel);
        await none.PickCornersCommand.ExecuteAsync(null);
        Assert.Equal((0, start), (asks, none.Area));
    });

    [Fact]
    public Task The_work_area_offers_corner_picking_on_the_mapped_monitor_and_says_why_when_it_cannot() => OnUi(async () =>
    {
        using var bench = await Bench.OpenAsync();
        var main = bench.Main;

        await main.WorkArea.CustomScreenArea();
        var screen = bench.Dialogs.LastArea!;
        Assert.Equal(("DP-2", false), (screen.Corners!.Screen.Name, screen.Corners.WithPen));

        // The pen's own node cannot be read here, so corner taps cannot be either.
        await main.WorkArea.CustomActiveArea();
        Assert.Null(bench.Dialogs.LastArea!.Corners);
        Assert.Contains("no access to the pen's input device", bench.Dialogs.LastArea.CornersUnavailable);

        using var node = new FakeNode(bench.Folder, "pen");
        bench.Presence = Bench.BuiltIn with { PenNode = node.Path };
        await main.RefreshAsync();
        await main.WorkArea.CustomActiveArea();
        var turnedAround = bench.Dialogs.LastArea!.Corners!;
        Assert.Equal(("DP-2", true, true, TabletTurn.None), (turnedAround.Screen.Name, turnedAround.WithPen, turnedAround.Flipped, turnedAround.QuarterPart));

        main.WorkArea.SelectedTurn = main.WorkArea.Turns.Single(t => t.Turn == TabletTurn.ThreeQuarter);
        await main.Pending;
        await main.WorkArea.CustomActiveArea();
        var sideways = bench.Dialogs.LastArea!.Corners!;
        Assert.Equal((false, TabletTurn.ThreeQuarter), (sideways.Flipped, sideways.QuarterPart));

        main.WorkArea.SelectedTarget = main.WorkArea.Targets.Single(t => t.Kind == ScreenTargetKind.AllScreens);
        await main.Pending;
        await main.WorkArea.CustomScreenArea();
        Assert.Null(bench.Dialogs.LastArea!.Corners);
        Assert.Contains("one monitor", bench.Dialogs.LastArea.CornersUnavailable);
        await main.WorkArea.CustomActiveArea();
        Assert.Null(bench.Dialogs.LastArea!.Corners);
        Assert.Contains("one monitor", bench.Dialogs.LastArea.CornersUnavailable);
    });

    static Point At(Window window, double x, double y) => new(window.Bounds.Width * x, window.Bounds.Height * y);

    static void Click(Window window, double x, double y)
    {
        window.MouseDown(At(window, x, y), MouseButton.Left);
        // The click that completes the area closes the veil on the way down.
        if (window.IsVisible)
            window.MouseUp(At(window, x, y), MouseButton.Left);
    }

    [Fact]
    public Task Two_clicks_on_the_veil_pick_the_screen_area_between_them() => OnUi(async () =>
    {
        var veil = new CornerWindow(new CornerRequest(Monitor(), WithPen: false), null);
        bool closed = false;
        veil.Closed += (_, _) => closed = true;
        veil.Show();
        await Task.Yield();
        Assert.True(veil.Bounds.Width > 100 && veil.Bounds.Height > 100, veil.Bounds.ToString());

        // Two clicks on nearly the same spot are no area: the veil stays and starts over.
        Click(veil, 0.5, 0.5);
        Click(veil, 0.51, 0.51);
        Assert.False(closed);
        Assert.Null(veil.Picked);

        Click(veil, 0.75, 0.8);
        Assert.False(closed);
        Click(veil, 0.25, 0.2);
        Assert.True(closed);
        Assert.True(veil.Picked!.Value.IsCloseTo(new RectD(0.25, 0.2, 0.5, 0.6), 0.002), veil.Picked.ToString());
    });

    [Fact]
    public Task One_drag_on_the_veil_picks_the_area_and_escape_or_a_right_click_leaves() => OnUi(async () =>
    {
        var veil = new CornerWindow(new CornerRequest(Monitor(), WithPen: false), null);
        veil.Show();
        await Task.Yield();
        veil.MouseDown(At(veil, 0.1, 0.1), MouseButton.Left);
        veil.MouseMove(At(veil, 0.3, 0.3));
        veil.MouseUp(At(veil, 0.6, 0.5), MouseButton.Left);
        Assert.True(veil.Picked!.Value.IsCloseTo(new RectD(0.1, 0.1, 0.5, 0.4), 0.002), veil.Picked.ToString());

        foreach (bool withKey in (bool[])[true, false])
        {
            var left = new CornerWindow(new CornerRequest(Monitor(), WithPen: false), null);
            bool closed = false;
            left.Closed += (_, _) => closed = true;
            left.Show();
            await Task.Yield();
            Click(left, 0.2, 0.2);
            if (withKey)
                left.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            else
                left.MouseDown(At(left, 0.8, 0.8), MouseButton.Right);
            Assert.True(closed);
            Assert.Null(left.Picked);
        }
    });

    static void Tap(CornerWindow veil, double x, double y)
    {
        veil.TakePenReport(false, (x, y));
        for (int i = 0; i < 8; i++)
            veil.TakePenReport(true, (x, y));
    }

    [Fact]
    public Task Two_pen_taps_pick_the_active_area_as_the_tablet_is_looked_at() => OnUi(async () =>
    {
        // Turned around, the corner nearest the person's upper left is the tablet's own lower right.
        var veil = new CornerWindow(new CornerRequest(Monitor(), WithPen: true, Flipped: true), null);
        bool closed = false;
        veil.Closed += (_, _) => closed = true;
        veil.Show();
        await Task.Yield();

        // Clicks do nothing here: the pen's taps arrive as clicks too, and only the taps read from the pen count.
        Click(veil, 0.2, 0.2);
        Click(veil, 0.8, 0.8);
        Assert.False(closed);

        Tap(veil, 0.9, 0.8);
        Assert.False(closed);
        // Resting on the first corner does not answer for the second.
        for (int i = 0; i < 20; i++)
            veil.TakePenReport(true, (0.9, 0.8));
        Assert.False(closed);
        Tap(veil, 0.4, 0.3);
        Assert.True(closed);
        Assert.True(veil.Picked!.Value.IsCloseTo(new RectD(0.1, 0.2, 0.5, 0.5), 1e-9), veil.Picked.ToString());
    });
}
