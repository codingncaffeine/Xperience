using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Xperience.App.Controls;

/// <summary>A patch to try the pen on: strokes are as thick as the pressure at that moment.</summary>
public sealed class ScribbleArea : Control
{
    public static readonly StyledProperty<double> PressureProperty = AvaloniaProperty.Register<ScribbleArea, double>(nameof(Pressure));

    const double Thinnest = 1.2, Thickest = 14;

    readonly List<(Point From, Point To, double Width)> _strokes = [];
    Point? _last;

    public ScribbleArea()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ClipToBounds = true;
    }

    /// <summary>The pen's pressure right now, 0 to 1. A mouse has none and draws a thin line.</summary>
    public double Pressure { get => GetValue(PressureProperty); set => SetValue(PressureProperty, value); }

    public bool IsEmpty => _strokes.Count == 0;

    public void Clear()
    {
        _strokes.Clear();
        _last = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var ink = Palette.Brush(this, "XpText");
        foreach (var (from, to, width) in _strokes)
            context.DrawLine(new Pen(ink, width) { LineCap = PenLineCap.Round }, from, to);
        if (_strokes.Count == 0)
        {
            var hint = Palette.Text(this, "Draw here to try the pressure", 13, "XpTextFaint");
            context.DrawText(hint, new Point((Bounds.Width - hint.Width) / 2, (Bounds.Height - hint.Height) / 2));
        }
    }

    /// <summary>
    /// Raised when a stroke on the patch ends, with what the window system handed this program for it: the
    /// kind of pointer and the pressure it carried. That is the pressure after the desktop's own curve, which
    /// the pen's raw reading cannot show.
    /// </summary>
    public event Action<string>? StrokeSeen;

    float _least, _most;
    int _points;
    string _pointer = "";

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        _last = e.GetPosition(this);
        (_least, _most, _points, _pointer) = (point.Properties.Pressure, point.Properties.Pressure, 1, e.Pointer.Type.ToString());
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_last is not { } from || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var to = e.GetPosition(this);
        float seen = e.GetCurrentPoint(this).Properties.Pressure;
        (_least, _most, _points) = (Math.Min(_least, seen), Math.Max(_most, seen), _points + 1);
        double share = Pressure > 0 ? Math.Clamp(Pressure, 0, 1) : 0.08;
        _strokes.Add((from, to, Thinnest + share * (Thickest - Thinnest)));
        // Old ink goes first: a test patch never needs more than this.
        if (_strokes.Count > 6000)
            _strokes.RemoveRange(0, 1000);
        _last = to;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_last != null && _points > 0)
            StrokeSeen?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"stroke on the test patch as this program received it: {_pointer.ToLowerInvariant()} pointer, {_points} points, pressure {_least:0.000}..{_most:0.000} of 1"));
        _last = null;
        _points = 0;
        e.Pointer.Capture(null);
    }
}
