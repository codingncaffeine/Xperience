using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Xperience.App.Controls;

/// <summary>Looks up the theme's named colors for controls that paint themselves.</summary>
static class Palette
{
    static readonly IBrush Missing = Brushes.Magenta;

    public static IBrush Brush(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Missing;

    public static Pen Pen(Control control, string key, double thickness = 1, IDashStyle? dash = null) =>
        new(Brush(control, key), thickness, dash) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    public static FormattedText Text(Control control, string text, double size, string colorKey, FontWeight weight = FontWeight.Normal) =>
        new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElementFont(control), FontStyle.Normal, weight), size, Brush(control, colorKey));

    static FontFamily TextElementFont(Control control) => control.GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty);

    /// <summary>The largest rectangle of the given shape that fits inside <paramref name="bounds"/>, centered.</summary>
    public static Rect Fit(Rect bounds, double aspect)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || aspect <= 0)
            return default;
        double width = Math.Min(bounds.Width, bounds.Height * aspect), height = width / aspect;
        return new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }
}
