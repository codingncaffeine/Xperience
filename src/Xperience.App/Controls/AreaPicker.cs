using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Xperience.Core;

namespace Xperience.App.Controls;

/// <summary>
/// A surface with one rectangle on it that can be dragged about and resized by its edges and corners. The
/// rectangle is given as shares of the surface; <see cref="Parts"/> draws what the surface is made of, such as
/// the monitors of a desktop.
/// </summary>
public sealed class AreaPicker : Control
{
    public static readonly StyledProperty<RectD> AreaProperty =
        AvaloniaProperty.Register<AreaPicker, RectD>(nameof(Area), RectD.Full, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> SurfaceAspectProperty = AvaloniaProperty.Register<AreaPicker, double>(nameof(SurfaceAspect), 16.0 / 9);
    public static readonly StyledProperty<IReadOnlyList<RectD>?> PartsProperty = AvaloniaProperty.Register<AreaPicker, IReadOnlyList<RectD>?>(nameof(Parts));
    public static readonly StyledProperty<bool> DarkSurfaceProperty = AvaloniaProperty.Register<AreaPicker, bool>(nameof(DarkSurface));

    const double Grip = 12, Smallest = 0.05;

    [Flags]
    enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    Edge _drag;
    Point _dragFrom;
    RectD _areaFrom;

    static AreaPicker() => AffectsRender<AreaPicker>(AreaProperty, SurfaceAspectProperty, PartsProperty, DarkSurfaceProperty);

    public AreaPicker() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public RectD Area { get => GetValue(AreaProperty); set => SetValue(AreaProperty, value); }
    public double SurfaceAspect { get => GetValue(SurfaceAspectProperty); set => SetValue(SurfaceAspectProperty, value); }
    public IReadOnlyList<RectD>? Parts { get => GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    /// <summary>Draws the surface as the tablet's dark screen instead of as a monitor.</summary>
    public bool DarkSurface { get => GetValue(DarkSurfaceProperty); set => SetValue(DarkSurfaceProperty, value); }

    /// <summary>Raised when a drag ends.</summary>
    public event EventHandler? Edited;

    Rect Surface => Palette.Fit(new Rect(Bounds.Size).Deflate(8), SurfaceAspect);

    Rect OnSurface(RectD share)
    {
        var s = Surface;
        return new Rect(s.X + share.X * s.Width, s.Y + share.Y * s.Height, share.Width * s.Width, share.Height * s.Height);
    }

    public override void Render(DrawingContext context)
    {
        var surface = Surface;
        if (surface.Width < 20)
            return;
        var fill = Palette.Brush(this, DarkSurface ? "XpTabletScreen" : "XpScreenFill");
        var line = Palette.Pen(this, "XpLine", 1);
        if (Parts is { Count: > 0 } parts)
        {
            foreach (var part in parts)
                context.DrawRectangle(fill, line, OnSurface(part).Deflate(1), 3, 3);
        }
        else
            context.DrawRectangle(fill, line, surface, 4, 4);

        var area = OnSurface(Area);
        context.DrawRectangle(Palette.Brush(this, "XpAccentSoft"), Palette.Pen(this, "XpAccent", 2), area, 3, 3);
        var handle = Palette.Brush(this, "XpAccent");
        foreach (var point in (Point[])[area.TopLeft, area.TopRight, area.BottomLeft, area.BottomRight,
            new(area.Center.X, area.Y), new(area.Center.X, area.Bottom), new(area.X, area.Center.Y), new(area.Right, area.Center.Y)])
            context.DrawEllipse(handle, null, point, 4.5, 4.5);
    }

    Edge Hit(Point at)
    {
        var area = OnSurface(Area);
        if (!area.Inflate(Grip).Contains(at))
            return Edge.None;
        var edge = Edge.None;
        if (Math.Abs(at.X - area.X) <= Grip) edge |= Edge.Left;
        else if (Math.Abs(at.X - area.Right) <= Grip) edge |= Edge.Right;
        if (Math.Abs(at.Y - area.Y) <= Grip) edge |= Edge.Top;
        else if (Math.Abs(at.Y - area.Bottom) <= Grip) edge |= Edge.Bottom;
        return edge == Edge.None ? Edge.Move : edge;
    }

    static StandardCursorType CursorFor(Edge edge) => edge switch
    {
        Edge.Move => StandardCursorType.SizeAll,
        Edge.Left or Edge.Right => StandardCursorType.SizeWestEast,
        Edge.Top or Edge.Bottom => StandardCursorType.SizeNorthSouth,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => StandardCursorType.TopLeftCorner,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => StandardCursorType.TopRightCorner,
        _ => StandardCursorType.Arrow,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var at = e.GetPosition(this);
        _drag = Hit(at);
        if (_drag == Edge.None)
            return;
        _dragFrom = at;
        _areaFrom = Area;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_drag == Edge.None)
        {
            Cursor = new Cursor(CursorFor(Hit(at)));
            return;
        }
        var surface = Surface;
        if (surface.Width <= 0 || surface.Height <= 0)
            return;
        double dx = (at.X - _dragFrom.X) / surface.Width, dy = (at.Y - _dragFrom.Y) / surface.Height;
        Area = Dragged(_areaFrom, _drag, dx, dy);
    }

    /// <summary>The rectangle after a drag of one of its parts by a share of the surface, kept on the surface.</summary>
    static RectD Dragged(RectD from, Edge edge, double dx, double dy)
    {
        if (edge == Edge.Move)
            return new RectD(Math.Clamp(from.X + dx, 0, 1 - from.Width), Math.Clamp(from.Y + dy, 0, 1 - from.Height), from.Width, from.Height);
        double left = from.X, top = from.Y, right = from.Right, bottom = from.Bottom;
        if (edge.HasFlag(Edge.Left)) left = Math.Clamp(left + dx, 0, right - Smallest);
        if (edge.HasFlag(Edge.Right)) right = Math.Clamp(right + dx, left + Smallest, 1);
        if (edge.HasFlag(Edge.Top)) top = Math.Clamp(top + dy, 0, bottom - Smallest);
        if (edge.HasFlag(Edge.Bottom)) bottom = Math.Clamp(bottom + dy, top + Smallest, 1);
        return new RectD(left, top, right - left, bottom - top);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Edge.None)
            return;
        _drag = Edge.None;
        e.Pointer.Capture(null);
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
