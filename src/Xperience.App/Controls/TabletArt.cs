using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Xperience.Core.Tablets;

namespace Xperience.App.Controls;

/// <summary>The tablet with what each key and dial does written beside it.</summary>
public sealed class TabletArt : Control
{
    public static readonly StyledProperty<TabletModel?> ModelProperty = AvaloniaProperty.Register<TabletArt, TabletModel?>(nameof(Model));
    public static readonly StyledProperty<bool> TurnedProperty = AvaloniaProperty.Register<TabletArt, bool>(nameof(Turned));
    public static readonly StyledProperty<IReadOnlyList<string>?> KeyLabelsProperty = AvaloniaProperty.Register<TabletArt, IReadOnlyList<string>?>(nameof(KeyLabels));
    public static readonly StyledProperty<IReadOnlyList<string>?> DialLabelsProperty = AvaloniaProperty.Register<TabletArt, IReadOnlyList<string>?>(nameof(DialLabels));

    static TabletArt() => AffectsRender<TabletArt>(ModelProperty, TurnedProperty, KeyLabelsProperty, DialLabelsProperty);

    public TabletArt() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public TabletModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool Turned { get => GetValue(TurnedProperty); set => SetValue(TurnedProperty, value); }
    public IReadOnlyList<string>? KeyLabels { get => GetValue(KeyLabelsProperty); set => SetValue(KeyLabelsProperty, value); }
    public IReadOnlyList<string>? DialLabels { get => GetValue(DialLabelsProperty); set => SetValue(DialLabelsProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Model is not { } model)
            return;
        var body = Palette.Fit(new Rect(Bounds.Size).Deflate(2), model.WidthMm / model.HeightMm);
        if (body.Width < 40)
            return;
        var layout = TabletPainter.Measure(model, body, Turned);
        TabletPainter.Draw(context, this, layout);

        double size = Math.Clamp(body.Height * 0.042, 11, 16);
        Label(context, layout, layout.Keys, KeyLabels, size, "XpOnTablet", FontWeight.Medium);
        Label(context, layout, layout.Dials, DialLabels, size, "XpOnTabletAccent", FontWeight.Medium);
    }

    void Label(DrawingContext context, TabletPainter.Layout layout, IReadOnlyList<Rect> spots, IReadOnlyList<string>? labels, double size, string color, FontWeight weight)
    {
        if (labels == null)
            return;
        var leader = Palette.Pen(this, "XpOnTabletFaint", 1);
        double gap = layout.Surface.Width * 0.022;
        for (int i = 0; i < spots.Count && i < labels.Count; i++)
        {
            var text = Palette.Text(this, labels[i], size, color, weight);
            double y = spots[i].Center.Y;
            if (layout.KeysOnRight)
            {
                double end = layout.Surface.Right - gap;
                context.DrawLine(leader, new Point(layout.Surface.Right - gap * 0.55, y), new Point(spots[i].X - 3, y));
                context.DrawText(text, new Point(end - gap * 0.4 - text.Width, y - text.Height / 2));
            }
            else
            {
                double start = layout.Surface.X + gap;
                context.DrawLine(leader, new Point(spots[i].Right + 3, y), new Point(layout.Surface.X + gap * 0.55, y));
                context.DrawText(text, new Point(start + gap * 0.4, y - text.Height / 2));
            }
        }
    }
}
