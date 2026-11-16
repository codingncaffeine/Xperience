using System.Globalization;

namespace Xperience.Core.Pen;

/// <summary>
/// How pen pressure bends between nothing and full: a cubic Bezier from (0,0) to (1,1) with two control
/// points, which is the form the desktop stores.
/// </summary>
public readonly record struct PressureCurve(double X1, double Y1, double X2, double Y2)
{
    /// <summary>Output equals input.</summary>
    public static readonly PressureCurve Linear = new(0, 0, 1, 1);

    /// <summary>The point the curve passes through at its middle.</summary>
    public (double X, double Y) Middle => (0.375 * X1 + 0.375 * X2 + 0.125, 0.375 * Y1 + 0.375 * Y2 + 0.125);

    /// <summary>
    /// The curve that passes through <paramref name="x"/>, <paramref name="y"/> at its middle, with both control
    /// points on one spot. A point too near a corner is pulled in to the nearest curve this form can make.
    /// </summary>
    public static PressureCurve Through(double x, double y)
    {
        double cx = Math.Clamp((x - 0.125) / 0.75, 0, 1);
        double cy = Math.Clamp((y - 0.125) / 0.75, 0, 1);
        return new PressureCurve(cx, cy, cx, cy);
    }

    /// <summary>The curve's point at <paramref name="t"/> from 0 to 1.</summary>
    public (double X, double Y) At(double t)
    {
        double u = 1 - t;
        double b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
        return (b1 * X1 + b2 * X2 + b3, b1 * Y1 + b2 * Y2 + b3);
    }

    /// <summary>Seven curves from soft (a light touch already gives a lot) to hard, the fourth being linear.</summary>
    public static readonly IReadOnlyList<PressureCurve> Presets =
    [
        Through(0.20, 0.80), Through(0.30, 0.70), Through(0.40, 0.60), Linear,
        Through(0.60, 0.40), Through(0.70, 0.30), Through(0.80, 0.20),
    ];

    /// <summary>The desktop's text form, "x1,y1;x2,y2;".</summary>
    public string ToDesktopString() => string.Create(CultureInfo.InvariantCulture, $"{X1:0.###},{Y1:0.###};{X2:0.###},{Y2:0.###};");

    /// <summary>Reads the desktop's text form; anything else gives the linear curve.</summary>
    public static PressureCurve Parse(string? text)
    {
        var numbers = new List<double>(4);
        foreach (string part in (text ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                return Linear;
            numbers.Add(Math.Clamp(value, 0, 1));
        }
        return numbers.Count == 4 ? new PressureCurve(numbers[0], numbers[1], numbers[2], numbers[3]) : Linear;
    }

    public bool IsCloseTo(PressureCurve other, double tolerance = 0.002) =>
        Math.Abs(X1 - other.X1) <= tolerance && Math.Abs(Y1 - other.Y1) <= tolerance &&
        Math.Abs(X2 - other.X2) <= tolerance && Math.Abs(Y2 - other.Y2) <= tolerance;
}
