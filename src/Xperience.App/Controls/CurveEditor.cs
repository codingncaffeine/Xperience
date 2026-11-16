using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Xperience.Core.Pen;

namespace Xperience.App.Controls;

/// <summary>
/// The pressure response as a graph with three handles: where output starts, how it bends, and where it reaches
/// full. Dragging changes the values at once; <see cref="Edited"/> fires when the drag ends.
/// </summary>
public sealed class CurveEditor : Control
{
    public static readonly StyledProperty<PressureCurve> CurveProperty =
        AvaloniaProperty.Register<CurveEditor, PressureCurve>(nameof(Curve), PressureCurve.Linear, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> RangeMinProperty =
        AvaloniaProperty.Register<CurveEditor, double>(nameof(RangeMin), 0, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> RangeMaxProperty =
        AvaloniaProperty.Register<CurveEditor, double>(nameof(RangeMax), 1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> CanMoveEndsProperty = AvaloniaProperty.Register<CurveEditor, bool>(nameof(CanMoveEnds), true);
    public static readonly StyledProperty<double> LiveProperty = AvaloniaProperty.Register<CurveEditor, double>(nameof(Live));

    const double HandleRadius = 9, Reach = 22;
    const double SmallestSpan = 0.15;

    enum Handle { None, Start, Middle, End }
    Handle _held;

    static CurveEditor() => AffectsRender<CurveEditor>(CurveProperty, RangeMinProperty, RangeMaxProperty, CanMoveEndsProperty, LiveProperty);

    public CurveEditor()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public PressureCurve Curve { get => GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public double RangeMin { get => GetValue(RangeMinProperty); set => SetValue(RangeMinProperty, value); }
    public double RangeMax { get => GetValue(RangeMaxProperty); set => SetValue(RangeMaxProperty, value); }
    /// <summary>False when the desktop cannot move where output starts and ends; those handles are then fixed.</summary>
    public bool CanMoveEnds { get => GetValue(CanMoveEndsProperty); set => SetValue(CanMoveEndsProperty, value); }
    /// <summary>The pen's pressure right now, 0 to 1, shown as a marker on the curve.</summary>
    public double Live { get => GetValue(LiveProperty); set => SetValue(LiveProperty, value); }

    /// <summary>Raised when a drag ends: the values are final and can be written.</summary>
    public event EventHandler? Edited;

    Rect Plot => new Rect(Bounds.Size).Deflate(HandleRadius + 2);

    Point ToScreen(double x, double y)
    {
        var p = Plot;
        return new Point(p.X + x * p.Width, p.Bottom - y * p.Height);
    }

    (double X, double Y) FromScreen(Point point)
    {
        var p = Plot;
        return (Math.Clamp((point.X - p.X) / p.Width, 0, 1), Math.Clamp((p.Bottom - point.Y) / p.Height, 0, 1));
    }

    double Span => Math.Max(RangeMax - RangeMin, 0.0001);

    Point MiddlePoint
    {
        get
        {
            var (mx, my) = Curve.Middle;
            return ToScreen(RangeMin + mx * Span, my);
        }
    }

    /// <summary>The output for an input pressure, both 0 to 1, as the desktop computes it.</summary>
    double Output(double input)
    {
        if (input <= RangeMin)
            return 0;
        if (input >= RangeMax)
            return 1;
        double wanted = (input - RangeMin) / Span, low = 0, high = 1;
        for (int i = 0; i < 24; i++)
        {
            double t = (low + high) / 2;
            if (Curve.At(t).X < wanted)
                low = t;
            else
                high = t;
        }
        return Curve.At((low + high) / 2).Y;
    }

    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        if (plot.Width < 40 || plot.Height < 40)
            return;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var grid = Palette.Pen(this, "XpLine", 1);
        for (int i = 0; i <= 10; i++)
        {
            double x = plot.X + plot.Width * i / 10, y = plot.Y + plot.Height * i / 10;
            context.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
        }

        var points = new List<Point> { ToScreen(0, 0), ToScreen(RangeMin, 0) };
        for (int i = 1; i < 48; i++)
        {
            var (x, y) = Curve.At(i / 48.0);
            points.Add(ToScreen(RangeMin + x * Span, y));
        }
        points.Add(ToScreen(RangeMax, 1));
        points.Add(ToScreen(1, 1));

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(points[0], true);
            foreach (var p in points.Skip(1))
                g.LineTo(p);
            g.LineTo(ToScreen(1, 0));
            g.EndFigure(true);
        }
        context.DrawGeometry(Palette.Brush(this, "XpAccentSoft"), null, area);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false);
            foreach (var p in points.Skip(1))
                g.LineTo(p);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, Palette.Pen(this, "XpAccent", 2.5), line);

        if (Live > 0)
        {
            var at = ToScreen(Live, Output(Live));
            var guide = Palette.Pen(this, "XpGood", 1, new DashStyle([3, 3], 0));
            context.DrawLine(guide, new Point(at.X, plot.Bottom), at);
            context.DrawEllipse(Palette.Brush(this, "XpGood"), null, at, 4.5, 4.5);
        }

        var fill = Palette.Brush(this, "XpAccent");
        var ring = Palette.Pen(this, "XpCard", 2.5);
        var fixedFill = Palette.Brush(this, "XpTextFaint");
        context.DrawEllipse(CanMoveEnds ? fill : fixedFill, ring, ToScreen(RangeMin, 0), HandleRadius, HandleRadius);
        context.DrawEllipse(CanMoveEnds ? fill : fixedFill, ring, ToScreen(RangeMax, 1), HandleRadius, HandleRadius);
        context.DrawEllipse(fill, ring, MiddlePoint, HandleRadius, HandleRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var at = e.GetPosition(this);
        static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        var candidates = new List<(Handle Handle, double Distance)> { (Handle.Middle, Distance(at, MiddlePoint)) };
        if (CanMoveEnds)
        {
            candidates.Add((Handle.Start, Distance(at, ToScreen(RangeMin, 0))));
            candidates.Add((Handle.End, Distance(at, ToScreen(RangeMax, 1))));
        }
        var nearest = candidates.MinBy(c => c.Distance);
        _held = nearest.Distance <= Reach ? nearest.Handle : Handle.None;
        if (_held != Handle.None)
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_held == Handle.None)
            return;
        var (x, y) = FromScreen(e.GetPosition(this));
        switch (_held)
        {
            case Handle.Start:
                RangeMin = Math.Round(Math.Min(x, RangeMax - SmallestSpan), 3);
                break;
            case Handle.End:
                RangeMax = Math.Round(Math.Max(x, RangeMin + SmallestSpan), 3);
                break;
            case Handle.Middle:
                Curve = PressureCurve.Through(Math.Clamp((x - RangeMin) / Span, 0, 1), y);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_held == Handle.None)
            return;
        _held = Handle.None;
        e.Pointer.Capture(null);
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
