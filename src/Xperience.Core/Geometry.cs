namespace Xperience.Core;

/// <summary>A rectangle in whatever unit its owner says: pixels, millimeters or a 0-1 share of a surface.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    /// <summary>The whole of a surface, as shares of it.</summary>
    public static readonly RectD Full = new(0, 0, 1, 1);

    [System.Text.Json.Serialization.JsonIgnore] public double Right => X + Width;
    [System.Text.Json.Serialization.JsonIgnore] public double Bottom => Y + Height;
    [System.Text.Json.Serialization.JsonIgnore] public double Aspect => Height > 0 ? Width / Height : 0;

    /// <summary>True when both cover the same area to within a rounding step.</summary>
    public bool IsCloseTo(RectD other, double tolerance = 0.0005) =>
        Math.Abs(X - other.X) <= tolerance && Math.Abs(Y - other.Y) <= tolerance &&
        Math.Abs(Width - other.Width) <= tolerance && Math.Abs(Height - other.Height) <= tolerance;

    /// <summary>The union of several rectangles; empty input gives an empty rectangle.</summary>
    public static RectD Around(IEnumerable<RectD> rects)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }
        return right < left ? default : new RectD(left, top, right - left, bottom - top);
    }
}
