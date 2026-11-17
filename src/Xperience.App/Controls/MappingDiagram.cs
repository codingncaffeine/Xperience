using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.App.Controls;

/// <summary>The desktop's screens above, the tablet below, and lines joining the pen's surface to the part of the
/// desktop it reaches.</summary>
public sealed class MappingDiagram : Control
{
    public static readonly StyledProperty<IReadOnlyList<ScreenInfo>?> ScreensProperty = AvaloniaProperty.Register<MappingDiagram, IReadOnlyList<ScreenInfo>?>(nameof(Screens));
    public static readonly StyledProperty<ScreenTargetKind> TargetKindProperty = AvaloniaProperty.Register<MappingDiagram, ScreenTargetKind>(nameof(TargetKind));
    public static readonly StyledProperty<string?> TargetScreenProperty = AvaloniaProperty.Register<MappingDiagram, string?>(nameof(TargetScreen));
    public static readonly StyledProperty<RectD> ScreenAreaProperty = AvaloniaProperty.Register<MappingDiagram, RectD>(nameof(ScreenArea), RectD.Full);
    public static readonly StyledProperty<TabletModel?> ModelProperty = AvaloniaProperty.Register<MappingDiagram, TabletModel?>(nameof(Model));
    public static readonly StyledProperty<bool> TurnedProperty = AvaloniaProperty.Register<MappingDiagram, bool>(nameof(Turned));
    public static readonly StyledProperty<RectD> ActiveAreaProperty = AvaloniaProperty.Register<MappingDiagram, RectD>(nameof(ActiveArea), RectD.Full);

    static MappingDiagram() => AffectsRender<MappingDiagram>(ScreensProperty, TargetKindProperty, TargetScreenProperty, ScreenAreaProperty, ModelProperty, TurnedProperty, ActiveAreaProperty);

    public MappingDiagram() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public IReadOnlyList<ScreenInfo>? Screens { get => GetValue(ScreensProperty); set => SetValue(ScreensProperty, value); }
    public ScreenTargetKind TargetKind { get => GetValue(TargetKindProperty); set => SetValue(TargetKindProperty, value); }
    public string? TargetScreen { get => GetValue(TargetScreenProperty); set => SetValue(TargetScreenProperty, value); }
    public RectD ScreenArea { get => GetValue(ScreenAreaProperty); set => SetValue(ScreenAreaProperty, value); }
    public TabletModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool Turned { get => GetValue(TurnedProperty); set => SetValue(TurnedProperty, value); }

    /// <summary>The part of the pen's surface in use, as shares of it the way the tablet is drawn.</summary>
    public RectD ActiveArea { get => GetValue(ActiveAreaProperty); set => SetValue(ActiveAreaProperty, value); }

    public override void Render(DrawingContext context)
    {
        var screens = Screens ?? [];
        var all = new Rect(Bounds.Size);
        if (all.Width < 80 || all.Height < 80)
            return;

        // Screens take the upper part, the tablet the lower, with room between for the joining lines.
        var upper = new Rect(all.X, all.Y, all.Width, all.Height * 0.54);
        var lower = new Rect(all.X, all.Y + all.Height * 0.68, all.Width, all.Height * 0.32);

        var desktop = RectD.Around(screens.Select(s => s.Bounds));
        Rect? reached = null;
        if (screens.Count > 0 && desktop.Width > 0 && desktop.Height > 0)
        {
            var frame = Palette.Fit(upper, desktop.Aspect);
            double scale = frame.Width / desktop.Width;
            Rect On(RectD r) => new(frame.X + (r.X - desktop.X) * scale, frame.Y + (r.Y - desktop.Y) * scale, r.Width * scale, r.Height * scale);

            var fill = Palette.Brush(this, "XpScreenFill");
            var line = Palette.Pen(this, "XpLine", 1);
            foreach (var screen in screens)
            {
                var r = On(screen.Bounds).Deflate(1.5);
                context.DrawRectangle(fill, line, r, 4, 4);
                var number = Palette.Text(this, screen.Number.ToString(System.Globalization.CultureInfo.CurrentCulture), Math.Clamp(r.Height * 0.34, 12, 30), "XpTextMuted", FontWeight.SemiBold);
                context.DrawText(number, new Point(r.Center.X - number.Width / 2, r.Center.Y - number.Height / 2 - 6));
                var name = Palette.Text(this, screen.Name, 11, "XpTextFaint");
                if (name.Width < r.Width - 8)
                    context.DrawText(name, new Point(r.Center.X - name.Width / 2, r.Center.Y + number.Height / 2 - 6));
            }

            var one = TargetKind == ScreenTargetKind.OneScreen ? screens.FirstOrDefault(s => s.Name == TargetScreen) : null;
            RectD? target = TargetKind switch
            {
                ScreenTargetKind.OneScreen => one?.Bounds,
                ScreenTargetKind.AllScreens => desktop,
                _ => null,
            };
            if (target is { } t)
            {
                // The area is kept in the panel's own orientation; it is drawn the way the screen is looked at.
                var area = one != null ? AreaMath.ToSeen(ScreenArea, one.Rotation) : ScreenArea;
                var part = new RectD(t.X + area.X * t.Width, t.Y + area.Y * t.Height, area.Width * t.Width, area.Height * t.Height);
                reached = On(part).Deflate(1.5);
                context.DrawRectangle(Palette.Brush(this, "XpAccentSoft"), Palette.Pen(this, "XpAccent", 2), reached.Value, 4, 4);
            }
            else
            {
                // The pen goes wherever the active window is: every screen is a candidate.
                var dashed = Palette.Pen(this, "XpAccent", 1.5, new DashStyle([4, 4], 0));
                foreach (var screen in screens)
                    context.DrawRectangle(null, dashed, On(screen.Bounds).Deflate(1.5), 4, 4);
            }
        }

        if (Model is not { } model)
            return;
        var body = Palette.Fit(lower, model.WidthMm / model.HeightMm);
        var layout = TabletPainter.Measure(model, body, Turned);
        TabletPainter.Draw(context, this, layout);
        var active = ActiveArea;
        var used = new Rect(layout.Surface.X + active.X * layout.Surface.Width, layout.Surface.Y + active.Y * layout.Surface.Height,
            active.Width * layout.Surface.Width, active.Height * layout.Surface.Height);
        context.DrawRectangle(Palette.Brush(this, "XpAccentSoft"), Palette.Pen(this, "XpAccent", 2), used, 2, 2);

        if (reached is { } region)
        {
            var join = Palette.Pen(this, "XpAccent", 1.2, new DashStyle([5, 5], 0));
            context.DrawLine(join, region.BottomLeft, used.TopLeft);
            context.DrawLine(join, region.BottomRight, used.TopRight);
        }
    }
}
