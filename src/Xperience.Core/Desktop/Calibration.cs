namespace Xperience.Core.Desktop;

/// <summary>
/// Turns the pen's stream of reports into one position per tap. A tap counts once the pen has been held down
/// for a few reports in a row, and the next one only after the pen has lifted, so resting the pen on one cross
/// cannot answer for the next.
/// </summary>
public sealed class TapCollector(int taps, bool flipped, int reportsPerTap = 8)
{
    readonly List<(double X, double Y)> _measured = [];
    readonly List<(double X, double Y)> _reports = [];
    bool _lifted;

    /// <summary>The taps so far, each as the calibration transform receives it.</summary>
    public IReadOnlyList<(double X, double Y)> Measured => _measured;

    public bool Done => _measured.Count >= taps;

    /// <summary>Takes one report: whether the pen touches, and where it is as shares of its surface. True when that completed a tap.</summary>
    public bool Add(bool touching, (double X, double Y)? at)
    {
        if (Done)
            return false;
        if (!touching)
        {
            _lifted = true;
            _reports.Clear();
            return false;
        }
        if (!_lifted || at is not { } position)
            return false;
        _reports.Add(position);
        if (_reports.Count < reportsPerTap)
            return false;
        _measured.Add(Calibration.Measured((_reports.Average(r => r.X), _reports.Average(r => r.Y)), flipped));
        _reports.Clear();
        _lifted = false;
        return true;
    }
}

/// <summary>The arithmetic of calibrating by tapping crosses on the screen.</summary>
public static class Calibration
{
    /// <summary>Where the crosses go, as shares of the screen as it is looked at: four near the corners, one in the middle.</summary>
    public static readonly IReadOnlyList<(double X, double Y)> Crosses = [(0.1, 0.1), (0.9, 0.1), (0.9, 0.9), (0.1, 0.9), (0.5, 0.5)];

    /// <summary>
    /// Where the pen has to report a cross to be, as shares of the area it is mapped to. The desktop maps the
    /// pen onto the panel as built and into the screen area; this runs that backwards from the cross.
    /// </summary>
    public static (double X, double Y) Wanted((double X, double Y) cross, ScreenRotation rotation, RectD screenArea)
    {
        var (x, y) = AreaMath.ToPanel(cross, rotation);
        return screenArea.Width > 0 && screenArea.Height > 0 ? ((x - screenArea.X) / screenArea.Width, (y - screenArea.Y) / screenArea.Height) : (x, y);
    }

    /// <summary>A raw pen position as the calibration transform receives it: after the desktop's own half turn.</summary>
    public static (double X, double Y) Measured((double X, double Y) raw, bool flipped) => flipped ? (1 - raw.X, 1 - raw.Y) : raw;

    /// <summary>
    /// The fine correction that makes the taps land on the crosses, given what the quarter turn and the active
    /// area already do. Null when the taps do not determine one.
    /// </summary>
    public static PenMatrix? Correction(IReadOnlyList<(double X, double Y)> measured, IReadOnlyList<(double X, double Y)> wanted, TabletTurn quarterPart, RectD activeArea)
    {
        if (PenMatrix.Fit(measured, wanted) is not { } whole)
            return null;
        var already = PenMatrix.ForActiveArea(activeArea).After(PenMatrix.ForTurn(quarterPart));
        return already.Inverse() is { } undo ? whole.After(undo) : null;
    }

    /// <summary>
    /// True for a correction a real pen and screen could need: a small shift, stretch or slant. Taps on the wrong
    /// crosses, or on another monitor, give something far from this, and that must not be applied.
    /// </summary>
    public static bool IsPlausible(PenMatrix correction) =>
        Math.Abs(correction.A - 1) < 0.2 && Math.Abs(correction.E - 1) < 0.2 &&
        Math.Abs(correction.B) < 0.2 && Math.Abs(correction.D) < 0.2 &&
        Math.Abs(correction.C) < 0.2 && Math.Abs(correction.F) < 0.2;
}
