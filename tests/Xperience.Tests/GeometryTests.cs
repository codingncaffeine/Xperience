using Xperience.Core;
using Xperience.Core.Desktop;

namespace Xperience.Tests;

public class PenMatrixTests
{
    static void Close((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    [Fact]
    public void A_quarter_turn_puts_the_keys_at_the_top()
    {
        // With the keys on the left, the surface's top left is next to the keys' upper end. Turned a quarter
        // clockwise, that corner is at the top right of what the user sees.
        Close((1, 0), PenMatrix.QuarterTurn.Apply(0, 0));
        Close((1, 1), PenMatrix.QuarterTurn.Apply(1, 0));
        Close((0, 0), PenMatrix.QuarterTurn.Apply(0, 1));
        Close((0, 1), PenMatrix.ThreeQuarterTurn.Apply(0, 0));
        Close((0, 0), PenMatrix.ThreeQuarterTurn.Apply(1, 0));
        Assert.True(PenMatrix.ThreeQuarterTurn.After(PenMatrix.QuarterTurn).IsCloseTo(PenMatrix.Identity));
    }

    [Fact]
    public void An_active_area_is_stretched_over_the_whole_surface()
    {
        var matrix = PenMatrix.ForActiveArea(new RectD(0.25, 0.5, 0.5, 0.25));
        Close((0, 0), matrix.Apply(0.25, 0.5));
        Close((1, 1), matrix.Apply(0.75, 0.75));
        Close((0.5, 0.5), matrix.Apply(0.5, 0.625));
        Assert.Equal(PenMatrix.Identity, PenMatrix.ForActiveArea(RectD.Full));
        Assert.Equal(PenMatrix.Identity, PenMatrix.ForActiveArea(default));
    }

    [Fact]
    public void A_transform_and_its_inverse_cancel()
    {
        var matrix = PenMatrix.Compose(TabletTurn.Quarter, new RectD(0.1, 0.2, 0.6, 0.5), new PenMatrix(1.02, 0.01, -0.01, -0.02, 0.98, 0.03));
        Assert.True(matrix.Inverse()!.Value.After(matrix).IsCloseTo(PenMatrix.Identity, 1e-9));
        Assert.Null(new PenMatrix(1, 2, 0, 2, 4, 0).Inverse());
    }

    [Fact]
    public void The_quarter_turn_can_be_read_back_from_a_composed_transform()
    {
        var area = new RectD(0.1, 0.2, 0.6, 0.5);
        var correction = new PenMatrix(1.02, 0.01, -0.01, -0.02, 0.98, 0.03);
        Assert.Equal(TabletTurn.None, PenMatrix.Compose(TabletTurn.None, area, correction).QuarterPart());
        Assert.Equal(TabletTurn.Quarter, PenMatrix.Compose(TabletTurn.Quarter, area, correction).QuarterPart());
        Assert.Equal(TabletTurn.ThreeQuarter, PenMatrix.Compose(TabletTurn.ThreeQuarter, area, correction).QuarterPart());
        Assert.Equal(TabletTurn.None, PenMatrix.Identity.QuarterPart());
    }

    [Fact]
    public void The_desktop_stores_sixteen_numbers_with_the_shift_in_the_third_column()
    {
        Assert.Equal("1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1", PenMatrix.Identity.ToDesktopString());
        Assert.Equal("0,-1,1,0,1,0,0,0,0,0,1,0,0,0,0,1", PenMatrix.QuarterTurn.ToDesktopString());
        var odd = new PenMatrix(1.25, 0.5, -0.125, -0.5, 0.75, 0.0625);
        Assert.Equal(odd, PenMatrix.Parse(odd.ToDesktopString()));
        Assert.Equal(PenMatrix.Identity, PenMatrix.Parse("1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,0,0")]
    [InlineData("1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,x")]
    [InlineData("1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,NaN")]
    public void Anything_else_reads_as_no_transform(string? text)
    {
        Assert.Equal(PenMatrix.Identity, PenMatrix.Parse(text));
    }

    [Fact]
    public void A_fit_recovers_the_transform_that_made_the_points()
    {
        var truth = new PenMatrix(1.03, 0.02, -0.015, -0.01, 0.97, 0.02);
        (double, double)[] from = [(0.1, 0.1), (0.9, 0.1), (0.9, 0.9), (0.1, 0.9), (0.5, 0.5)];
        var fitted = PenMatrix.Fit(from, [.. from.Select(p => truth.Apply(p.Item1, p.Item2))]);
        Assert.True(fitted!.Value.IsCloseTo(truth, 1e-9), fitted.ToString());

        Assert.Null(PenMatrix.Fit([(0, 0), (1, 1)], [(0, 0), (1, 1)]));
        Assert.Null(PenMatrix.Fit([(0, 0), (0.5, 0.5), (1, 1)], [(0, 0), (0.5, 0.5), (1, 1)]));
    }

    [Fact]
    public void The_turn_in_all_adds_the_desktops_flip_to_the_quarters()
    {
        var pen = new PenState("e", "Pen", ScreenTargetKind.OneScreen, "A", RectD.Full, Flipped: false, false, PressureSettings.Default, true, true, true);
        Assert.Equal(TabletTurn.None, pen.Turn);
        Assert.Equal(TabletTurn.Half, (pen with { Flipped = true }).Turn);
        Assert.Equal(TabletTurn.Quarter, (pen with { Matrix = PenMatrix.QuarterTurn }).Turn);
        Assert.Equal(TabletTurn.ThreeQuarter, (pen with { Matrix = PenMatrix.ThreeQuarterTurn }).Turn);
        Assert.Equal(TabletTurn.ThreeQuarter, (pen with { Matrix = PenMatrix.QuarterTurn, Flipped = true }).Turn);
    }
}

public class CalibrationTests
{
    [Fact]
    public void A_cross_is_wanted_where_the_desktop_would_put_the_pen()
    {
        var (x, y) = Calibration.Wanted((0.1, 0.1), ScreenRotation.None, RectD.Full);
        Assert.Equal((0.1, 0.1), (x, y));

        // On a screen turned upside down the top left of what is seen is the bottom right of the panel; with
        // the pen mapped to the right half of the panel, that is four fifths across the mapped area.
        (x, y) = Calibration.Wanted((0.1, 0.1), ScreenRotation.Half, new RectD(0.5, 0, 0.5, 1));
        Assert.Equal(0.8, x, 9);
        Assert.Equal(0.9, y, 9);
    }

    [Theory]
    [InlineData(TabletTurn.None, false)]
    [InlineData(TabletTurn.None, true)]
    [InlineData(TabletTurn.Quarter, false)]
    [InlineData(TabletTurn.ThreeQuarter, false)]
    public void Tapping_the_crosses_finds_the_correction_and_nothing_but(TabletTurn quarter, bool flipped)
    {
        var area = new RectD(0.1, 0.05, 0.8, 0.9);
        var truth = new PenMatrix(1.02, 0.01, -0.012, -0.008, 0.985, 0.02);
        var whole = PenMatrix.Compose(quarter, area, truth);
        var wanted = Calibration.Crosses.Select(c => Calibration.Wanted(c, ScreenRotation.None, RectD.Full)).ToList();
        // The raw position a pen has to report for the desktop to see each wanted point, before the flip is undone.
        var raw = wanted.Select(w => whole.Inverse()!.Value.Apply(w.X, w.Y)).Select(p => flipped ? (1 - p.X, 1 - p.Y) : p).ToList();

        var taps = new TapCollector(Calibration.Crosses.Count, flipped, reportsPerTap: 3);
        foreach (var point in raw)
        {
            Assert.False(taps.Add(false, point));
            Assert.False(taps.Add(true, point));
            Assert.False(taps.Add(true, point));
            Assert.True(taps.Add(true, point));
            // Staying down on the cross does not count for the next one.
            Assert.False(taps.Add(true, point));
        }
        Assert.True(taps.Done);

        var found = Calibration.Correction(taps.Measured, wanted, quarter, area);
        Assert.True(found!.Value.IsCloseTo(truth, 1e-6), found.ToString());
        Assert.True(Calibration.IsPlausible(found.Value));
    }

    [Fact]
    public void A_tap_needs_the_pen_lifted_first_held_long_enough_and_a_known_position()
    {
        var taps = new TapCollector(2, flipped: false, reportsPerTap: 2);
        // Already down when the screen opens: not a tap.
        Assert.False(taps.Add(true, (0.1, 0.1)));
        Assert.False(taps.Add(true, (0.1, 0.1)));
        Assert.False(taps.Add(false, null));
        // A touch too short to count.
        Assert.False(taps.Add(true, (0.2, 0.2)));
        Assert.False(taps.Add(false, null));
        // A position that is not known is no report.
        Assert.False(taps.Add(true, null));
        Assert.False(taps.Add(true, (0.3, 0.3)));
        Assert.True(taps.Add(true, (0.5, 0.3)));
        Assert.Equal([(0.4, 0.3)], taps.Measured.Select(m => (Math.Round(m.X, 9), Math.Round(m.Y, 9))));
    }

    [Fact]
    public void Taps_on_the_wrong_crosses_are_not_a_calibration()
    {
        var wanted = Calibration.Crosses.ToList();
        var mixedUp = new[] { wanted[2], wanted[3], wanted[0], wanted[1], wanted[4] };
        var correction = Calibration.Correction(mixedUp, wanted, TabletTurn.None, RectD.Full);
        Assert.False(correction is { } c && Calibration.IsPlausible(c));
        // Taps on a monitor half the size, off to one side.
        var elsewhere = wanted.Select(w => (w.X * 0.5 + 0.5, w.Y)).ToList();
        Assert.False(Calibration.IsPlausible(Calibration.Correction(elsewhere, wanted, TabletTurn.None, RectD.Full)!.Value));
    }
}

public class TurnedAreaTests
{
    [Theory]
    [InlineData(ScreenRotation.None)]
    [InlineData(ScreenRotation.Quarter)]
    [InlineData(ScreenRotation.Half)]
    [InlineData(ScreenRotation.ThreeQuarter)]
    public void Seen_and_panel_positions_convert_both_ways(ScreenRotation rotation)
    {
        var seen = (0.2, 0.7);
        var back = AreaMath.ToSeen(AreaMath.ToPanel(seen, rotation), rotation);
        Assert.Equal(seen.Item1, back.X, 9);
        Assert.Equal(seen.Item2, back.Y, 9);

        var rect = new RectD(0.1, 0.2, 0.3, 0.4);
        Assert.True(AreaMath.ToSeen(AreaMath.ToPanel(rect, rotation), rotation).IsCloseTo(rect, 1e-9));
    }

    [Fact]
    public void An_upside_down_screen_mirrors_an_area_both_ways_and_a_sideways_one_swaps_its_sides()
    {
        Assert.True(AreaMath.ToPanel(new RectD(0.1, 0.2, 0.3, 0.4), ScreenRotation.Half).IsCloseTo(new RectD(0.6, 0.4, 0.3, 0.4), 1e-9));
        var sideways = AreaMath.ToPanel(new RectD(0.1, 0.2, 0.3, 0.4), ScreenRotation.Quarter);
        Assert.Equal((0.4, 0.3), (Math.Round(sideways.Width, 9), Math.Round(sideways.Height, 9)));
        Assert.Equal(new RectD(0.1, 0.2, 0.3, 0.4), AreaMath.ToPanel(new RectD(0.1, 0.2, 0.3, 0.4), ScreenRotation.None));
    }

    [Fact]
    public void Precision_keeps_the_point_under_the_pen_where_it_is()
    {
        var area = new RectD(0.2, 0.1, 0.6, 0.8);
        var narrow = AreaMath.Precise(area, (0.75, 0.25), 3);
        Assert.Equal(0.2, narrow.Width, 9);
        Assert.Equal(0.8 / 3, narrow.Height, 9);
        Assert.Equal(area.X + 0.75 * area.Width, narrow.X + 0.75 * narrow.Width, 9);
        Assert.Equal(area.Y + 0.25 * area.Height, narrow.Y + 0.25 * narrow.Height, 9);
        // It never leaves the area it narrows.
        Assert.True(narrow.X >= area.X && narrow.Right <= area.Right + 1e-9 && narrow.Y >= area.Y && narrow.Bottom <= area.Bottom + 1e-9);
    }

    [Fact]
    public void Proportion_uses_the_part_of_the_tablet_shaped_like_the_screen()
    {
        // A 16:9 surface for a 4:3 screen uses three quarters of its width.
        var part = AreaMath.Proportional(16.0 / 9, 4.0 / 3);
        Assert.True(part.IsCloseTo(new RectD(0.125, 0, 0.75, 1), 1e-9), part.ToString());
        // And for an ultrawide screen, part of its height.
        part = AreaMath.Proportional(16.0 / 9, 32.0 / 9);
        Assert.True(part.IsCloseTo(new RectD(0, 0.25, 1, 0.5), 1e-9), part.ToString());
    }

    [Fact]
    public void A_screen_on_its_side_meets_the_pen_the_other_way_around()
    {
        var upright = new ScreenInfo("A", 1, new RectD(0, 0, 1920, 1080), 1, ScreenRotation.None);
        var sideways = new ScreenInfo("B", 2, new RectD(0, 0, 1080, 1920), 1, ScreenRotation.Quarter);
        Assert.Equal(16.0 / 9, upright.PenAspect, 9);
        Assert.Equal(16.0 / 9, sideways.PenAspect, 9);
        Assert.Equal(16.0 / 9, (upright with { Rotation = ScreenRotation.Half }).PenAspect, 9);
    }
}
