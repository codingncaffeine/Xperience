namespace Xperience.Core.Desktop;

/// <summary>The arithmetic of mapping the pen's surface onto a screen.</summary>
public static class AreaMath
{
    /// <summary>
    /// The largest part of a screen with the same shape as the pen's surface, centered, as shares of the screen.
    /// Mapping the pen there keeps circles round at the cost of the screen's edges.
    /// </summary>
    public static RectD SameShape(double surfaceAspect, double screenAspect)
    {
        if (surfaceAspect <= 0 || screenAspect <= 0)
            return RectD.Full;
        if (screenAspect > surfaceAspect)
        {
            double width = surfaceAspect / screenAspect;
            return new RectD((1 - width) / 2, 0, width, 1);
        }
        double height = screenAspect / surfaceAspect;
        return new RectD(0, (1 - height) / 2, 1, height);
    }

    /// <summary>How much wider than tall a stroke comes out: 1 means circles stay round.</summary>
    public static double Stretch(double surfaceAspect, double screenAspect, RectD screenArea)
    {
        if (surfaceAspect <= 0 || screenArea.Height <= 0)
            return 1;
        return screenAspect * screenArea.Width / screenArea.Height / surfaceAspect;
    }

    /// <summary>
    /// A smaller area inside <paramref name="area"/> for finer pen movement, placed so that the point of the
    /// surface the pen is on, given as shares of the surface, lands on the same spot of the screen as before.
    /// </summary>
    public static RectD Precise(RectD area, (double X, double Y) at, double factor)
    {
        double width = area.Width / factor, height = area.Height / factor;
        return new RectD(area.X + at.X * (area.Width - width), area.Y + at.Y * (area.Height - height), width, height);
    }

    /// <summary>
    /// The largest part of the pen's surface with the same shape as what it is mapped to, centered, as shares of
    /// the surface. Using only that part keeps circles round and still reaches all of the screen area.
    /// </summary>
    public static RectD Proportional(double surfaceAspect, double targetAspect) => SameShape(targetAspect, surfaceAspect);

    /// <summary>
    /// A point given as shares of a screen as it is looked at, turned into shares of the panel as it was built,
    /// which is what the pen is mapped onto. The two differ only on a turned screen.
    /// </summary>
    public static (double X, double Y) ToPanel((double X, double Y) seen, ScreenRotation rotation) => rotation switch
    {
        ScreenRotation.Half => (1 - seen.X, 1 - seen.Y),
        ScreenRotation.Quarter => (seen.Y, 1 - seen.X),
        ScreenRotation.ThreeQuarter => (1 - seen.Y, seen.X),
        _ => seen,
    };

    /// <summary>The reverse of <see cref="ToPanel(ValueTuple{double, double}, ScreenRotation)"/>.</summary>
    public static (double X, double Y) ToSeen((double X, double Y) panel, ScreenRotation rotation) => rotation switch
    {
        ScreenRotation.Half => (1 - panel.X, 1 - panel.Y),
        ScreenRotation.Quarter => (1 - panel.Y, panel.X),
        ScreenRotation.ThreeQuarter => (panel.Y, 1 - panel.X),
        _ => panel,
    };

    public static RectD ToPanel(RectD seen, ScreenRotation rotation) => rotation switch
    {
        ScreenRotation.Half => new RectD(1 - seen.Right, 1 - seen.Bottom, seen.Width, seen.Height),
        ScreenRotation.Quarter => new RectD(seen.Y, 1 - seen.Right, seen.Height, seen.Width),
        ScreenRotation.ThreeQuarter => new RectD(1 - seen.Bottom, seen.X, seen.Height, seen.Width),
        _ => seen,
    };

    public static RectD ToSeen(RectD panel, ScreenRotation rotation) => rotation switch
    {
        ScreenRotation.Half => new RectD(1 - panel.Right, 1 - panel.Bottom, panel.Width, panel.Height),
        ScreenRotation.Quarter => new RectD(1 - panel.Bottom, panel.X, panel.Height, panel.Width),
        ScreenRotation.ThreeQuarter => new RectD(panel.Y, 1 - panel.Right, panel.Height, panel.Width),
        _ => panel,
    };

    /// <summary>The smallest side an area picked by its corners may have, as a share of the surface.</summary>
    public const double SmallestSide = 0.05;

    /// <summary>
    /// The area between two opposite corners, whichever way round they were given. Null when it would be
    /// narrower or lower than <see cref="SmallestSide"/>: two taps on nearly the same spot are not an area.
    /// </summary>
    public static RectD? FromCorners((double X, double Y) first, (double X, double Y) second)
    {
        double left = Math.Clamp(Math.Min(first.X, second.X), 0, 1), right = Math.Clamp(Math.Max(first.X, second.X), 0, 1);
        double top = Math.Clamp(Math.Min(first.Y, second.Y), 0, 1), bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, 1);
        return right - left < SmallestSide || bottom - top < SmallestSide ? null : new RectD(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Where a tap on the tablet lies in the frame the active area is given in: the surface as the person
    /// looks at it, after the desktop's half turn and the tablet's quarter turn.
    /// </summary>
    public static (double X, double Y) TapOnSurface((double X, double Y) raw, bool flipped, TabletTurn quarterPart)
    {
        var (x, y) = flipped ? (1 - raw.X, 1 - raw.Y) : raw;
        return PenMatrix.ForTurn(quarterPart).Apply(x, y);
    }

    /// <summary>A share-of-screen rectangle kept inside the screen and never thinner than a twentieth of it.</summary>
    public static RectD Clamp(RectD area)
    {
        double width = Math.Clamp(area.Width, 0.05, 1), height = Math.Clamp(area.Height, 0.05, 1);
        return new RectD(Math.Clamp(area.X, 0, 1 - width), Math.Clamp(area.Y, 0, 1 - height), width, height);
    }
}
