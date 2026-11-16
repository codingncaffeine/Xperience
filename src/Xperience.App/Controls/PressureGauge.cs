using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Xperience.App.Controls;

/// <summary>The pen's pressure right now as a half-circle gauge with the number in the middle.</summary>
public sealed class PressureGauge : Control
{
    public static readonly StyledProperty<int> ValueProperty = AvaloniaProperty.Register<PressureGauge, int>(nameof(Value));
    public static readonly StyledProperty<int> MaximumProperty = AvaloniaProperty.Register<PressureGauge, int>(nameof(Maximum), 16383);

    static PressureGauge() => AffectsRender<PressureGauge>(ValueProperty, MaximumProperty);

    public PressureGauge() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public override void Render(DrawingContext context)
    {
        double thickness = 12;
        double radius = Math.Min(Bounds.Width / 2 - thickness, Bounds.Height - thickness - 26);
        if (radius < 20)
            return;
        var center = new Point(Bounds.Width / 2, radius + thickness / 2 + 2);

        Point At(double share)
        {
            double angle = Math.PI * (1 - share);
            return new Point(center.X + Math.Cos(angle) * radius, center.Y - Math.Sin(angle) * radius);
        }

        void Arc(double share, IPen pen)
        {
            if (share <= 0)
                return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(At(0), false);
                g.ArcTo(At(share), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        Arc(1, Palette.Pen(this, "XpLine", thickness));
        double share = Maximum > 0 ? Math.Clamp(Value / (double)Maximum, 0, 1) : 0;
        Arc(share, Palette.Pen(this, "XpAccent", thickness));

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var number = Palette.Text(this, Value.ToString(culture), Math.Clamp(radius * 0.36, 16, 30), "XpText", FontWeight.SemiBold);
        context.DrawText(number, new Point(center.X - number.Width / 2, center.Y - number.Height - 2));
        var low = Palette.Text(this, "0", 11, "XpTextFaint");
        var high = Palette.Text(this, Maximum.ToString(culture), 11, "XpTextFaint");
        context.DrawText(low, new Point(center.X - radius - low.Width / 2, center.Y + 10));
        context.DrawText(high, new Point(center.X + radius - high.Width / 2, center.Y + 10));
    }
}
