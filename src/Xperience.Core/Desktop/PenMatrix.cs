using System.Globalization;

namespace Xperience.Core.Desktop;

/// <summary>How far the tablet is turned from keys-on-the-left, clockwise.</summary>
public enum TabletTurn { None = 0, Quarter = 90, Half = 180, ThreeQuarter = 270 }

/// <summary>
/// A 2D transform of the pen's position, x' = ax + by + c and y' = dx + ey + f, on a surface that runs 0 to 1
/// each way. This is the calibration matrix the desktop hands to the input library.
/// </summary>
public readonly record struct PenMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly PenMatrix Identity = new(1, 0, 0, 0, 1, 0);

    /// <summary>The tablet turned a quarter clockwise: its keys at the top.</summary>
    public static readonly PenMatrix QuarterTurn = new(0, -1, 1, 1, 0, 0);

    /// <summary>The tablet turned three quarters clockwise: its keys at the bottom.</summary>
    public static readonly PenMatrix ThreeQuarterTurn = new(0, 1, 0, -1, 0, 1);

    public (double X, double Y) Apply(double x, double y) => (A * x + B * y + C, D * x + E * y + F);

    /// <summary>The transform that applies <paramref name="first"/> and then this one.</summary>
    public PenMatrix After(PenMatrix first) => new(
        A * first.A + B * first.D, A * first.B + B * first.E, A * first.C + B * first.F + C,
        D * first.A + E * first.D, D * first.B + E * first.E, D * first.C + E * first.F + F);

    public double Determinant => A * E - B * D;

    public PenMatrix? Inverse()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-9)
            return null;
        double a = E / det, b = -B / det, d = -D / det, e = A / det;
        return new PenMatrix(a, b, -(a * C + b * F), d, e, -(d * C + e * F));
    }

    public bool IsCloseTo(PenMatrix other, double tolerance = 0.002) =>
        Math.Abs(A - other.A) <= tolerance && Math.Abs(B - other.B) <= tolerance && Math.Abs(C - other.C) <= tolerance &&
        Math.Abs(D - other.D) <= tolerance && Math.Abs(E - other.E) <= tolerance && Math.Abs(F - other.F) <= tolerance;

    /// <summary>Stretches one part of the surface over all of it, so only that part is in use.</summary>
    public static PenMatrix ForActiveArea(RectD area) =>
        area.Width <= 0 || area.Height <= 0 ? Identity : new(1 / area.Width, 0, -area.X / area.Width, 0, 1 / area.Height, -area.Y / area.Height);

    public static PenMatrix ForTurn(TabletTurn turn) => turn switch
    {
        TabletTurn.Quarter => QuarterTurn,
        TabletTurn.ThreeQuarter => ThreeQuarterTurn,
        _ => Identity,
    };

    public static PenMatrix FromList(IReadOnlyList<double> v) => v.Count == 6 ? new(v[0], v[1], v[2], v[3], v[4], v[5]) : Identity;

    public IReadOnlyList<double> ToList() => [A, B, C, D, E, F];

    /// <summary>
    /// The whole transform: the tablet's quarter turn first, then the part of the surface in use, then the
    /// calibration correction. A half turn is not in here; the desktop does that one itself.
    /// </summary>
    public static PenMatrix Compose(TabletTurn turn, RectD activeArea, PenMatrix correction) =>
        correction.After(ForActiveArea(activeArea).After(ForTurn(turn)));

    /// <summary>
    /// Whether the transform turns the surface by a quarter, read from which way its axes point. A flip the
    /// desktop does separately adds a half turn on top.
    /// </summary>
    public TabletTurn QuarterPart()
    {
        if (Math.Abs(B) + Math.Abs(D) <= Math.Abs(A) + Math.Abs(E))
            return TabletTurn.None;
        return D > 0 ? TabletTurn.Quarter : TabletTurn.ThreeQuarter;
    }

    /// <summary>The 16 numbers the desktop stores: a 4 x 4 matrix, row by row, with the shift in the third column.</summary>
    public string ToDesktopString() => string.Join(',', new[] { A, B, C, 0, D, E, F, 0, 0, 0, 1, 0, 0, 0, 0, 1 }.Select(v => ((float)v).ToString("R", CultureInfo.InvariantCulture)));

    public static PenMatrix Parse(string? text)
    {
        string[] parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 16)
            return Identity;
        double[] v = new double[16];
        for (int i = 0; i < 16; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !double.IsFinite(v[i]))
                return Identity;
        }
        return new PenMatrix(v[0], v[1], v[2], v[4], v[5], v[6]);
    }

    /// <summary>
    /// The transform that best carries each measured point to where it should have been, by least squares.
    /// Null with fewer than three points or points on one line.
    /// </summary>
    public static PenMatrix? Fit(IReadOnlyList<(double X, double Y)> measured, IReadOnlyList<(double X, double Y)> wanted)
    {
        int n = Math.Min(measured.Count, wanted.Count);
        if (n < 3)
            return null;
        // Normal equations for [x y 1] against each output axis.
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0;
        double bx0 = 0, bx1 = 0, bx2 = 0, by0 = 0, by1 = 0, by2 = 0;
        for (int i = 0; i < n; i++)
        {
            var (x, y) = measured[i];
            sxx += x * x; sxy += x * y; sx += x; syy += y * y; sy += y;
            bx0 += x * wanted[i].X; bx1 += y * wanted[i].X; bx2 += wanted[i].X;
            by0 += x * wanted[i].Y; by1 += y * wanted[i].Y; by2 += wanted[i].Y;
        }
        double det = sxx * (syy * n - sy * sy) - sxy * (sxy * n - sy * sx) + sx * (sxy * sy - syy * sx);
        if (Math.Abs(det) < 1e-12)
            return null;
        (double, double, double) Solve(double b0, double b1, double b2) => (
            (b0 * (syy * n - sy * sy) - sxy * (b1 * n - sy * b2) + sx * (b1 * sy - syy * b2)) / det,
            (sxx * (b1 * n - b2 * sy) - b0 * (sxy * n - sy * sx) + sx * (sxy * b2 - b1 * sx)) / det,
            (sxx * (syy * b2 - sy * b1) - sxy * (sxy * b2 - b1 * sx) + b0 * (sxy * sy - syy * sx)) / det);
        var (a, b, c) = Solve(bx0, bx1, bx2);
        var (d, e, f) = Solve(by0, by1, by2);
        return new PenMatrix(a, b, c, d, e, f);
    }
}
