using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Xperience.App.Controls;

/// <summary>The pen, upright, with its two buttons and what each one does.</summary>
public sealed class PenArt : Control
{
    public static readonly StyledProperty<string?> UpperLabelProperty = AvaloniaProperty.Register<PenArt, string?>(nameof(UpperLabel));
    public static readonly StyledProperty<string?> LowerLabelProperty = AvaloniaProperty.Register<PenArt, string?>(nameof(LowerLabel));
    public static readonly StyledProperty<bool> UpperPressedProperty = AvaloniaProperty.Register<PenArt, bool>(nameof(UpperPressed));
    public static readonly StyledProperty<bool> LowerPressedProperty = AvaloniaProperty.Register<PenArt, bool>(nameof(LowerPressed));
    public static readonly StyledProperty<bool> TouchingProperty = AvaloniaProperty.Register<PenArt, bool>(nameof(Touching));

    static PenArt() => AffectsRender<PenArt>(UpperLabelProperty, LowerLabelProperty, UpperPressedProperty, LowerPressedProperty, TouchingProperty);

    public PenArt() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public string? UpperLabel { get => GetValue(UpperLabelProperty); set => SetValue(UpperLabelProperty, value); }
    public string? LowerLabel { get => GetValue(LowerLabelProperty); set => SetValue(LowerLabelProperty, value); }
    public bool UpperPressed { get => GetValue(UpperPressedProperty); set => SetValue(UpperPressedProperty, value); }
    public bool LowerPressed { get => GetValue(LowerPressedProperty); set => SetValue(LowerPressedProperty, value); }
    public bool Touching { get => GetValue(TouchingProperty); set => SetValue(TouchingProperty, value); }

    public override void Render(DrawingContext context)
    {
        double height = Bounds.Height - 8, width = Math.Clamp(height * 0.062, 16, 26);
        if (height < 120)
            return;
        // The pen stands left of center so the labels have the room on its right.
        double x = Math.Min(Bounds.Width * 0.24, 54), top = 4;
        var body = Palette.Brush(this, "XpTabletBody");
        var grip = Palette.Brush(this, "XpTabletEdge");
        var edge = Palette.Pen(this, "XpTabletEdge", 1.2);
        var accent = Palette.Brush(this, "XpAccent");
        var key = Palette.Brush(this, "XpTabletKey");

        double gripTop = top + height * 0.52, coneTop = top + height * 0.86, tip = top + height;

        // Upper barrel, rounded at the end.
        context.DrawRectangle(body, edge, new Rect(x - width / 2, top, width, gripTop - top + width / 2), width / 2, width / 2);
        // The grip the fingers hold, a little darker, down to where the cone starts.
        context.DrawRectangle(grip, edge, new Rect(x - width / 2, gripTop, width, coneTop - gripTop), 3, 3);

        // The cone narrows to the nib.
        var cone = new StreamGeometry();
        using (var g = cone.Open())
        {
            g.BeginFigure(new Point(x - width / 2, coneTop), true);
            g.LineTo(new Point(x + width / 2, coneTop));
            g.LineTo(new Point(x + width * 0.13, tip - width * 0.35));
            g.LineTo(new Point(x - width * 0.13, tip - width * 0.35));
            g.EndFigure(true);
        }
        context.DrawGeometry(body, edge, cone);
        var nib = new StreamGeometry();
        using (var g = nib.Open())
        {
            g.BeginFigure(new Point(x - width * 0.13, tip - width * 0.35), true);
            g.LineTo(new Point(x + width * 0.13, tip - width * 0.35));
            g.LineTo(new Point(x, tip));
            g.EndFigure(true);
        }
        context.DrawGeometry(Touching ? accent : key, null, nib);

        double buttonWidth = width * 0.46, buttonHeight = height * 0.085;
        var upper = new Rect(x - buttonWidth / 2, gripTop + height * 0.05, buttonWidth, buttonHeight);
        var lower = new Rect(x - buttonWidth / 2, upper.Bottom + height * 0.03, buttonWidth, buttonHeight);
        context.DrawRectangle(UpperPressed ? accent : key, null, upper, buttonWidth / 2, buttonWidth / 2);
        context.DrawRectangle(LowerPressed ? accent : key, null, lower, buttonWidth / 2, buttonWidth / 2);

        Label(context, upper, "Upper button", UpperLabel, x + width / 2);
        Label(context, lower, "Lower button", LowerLabel, x + width / 2);
    }

    void Label(DrawingContext context, Rect button, string name, string? does, double from)
    {
        double y = button.Center.Y, start = from + 26;
        context.DrawLine(Palette.Pen(this, "XpTextFaint", 1), new Point(from + 4, y), new Point(start - 6, y));
        var title = Palette.Text(this, name, 11, "XpTextFaint");
        var text = Palette.Text(this, does ?? "", 13, "XpText", FontWeight.Medium);
        text.MaxTextWidth = Math.Max(40, Bounds.Width - start - 2);
        text.MaxLineCount = 2;
        context.DrawText(title, new Point(start, y - title.Height - 1));
        context.DrawText(text, new Point(start, y + 1));
    }
}
