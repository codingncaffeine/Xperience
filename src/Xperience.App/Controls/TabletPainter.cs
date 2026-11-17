using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Xperience.Core;
using Xperience.Core.Tablets;

namespace Xperience.App.Controls;

/// <summary>
/// Draws the tablet from its model: body, screen (on a pen display), keys and dials. When the tablet is used turned
/// around, the drawing turns with it, so the picture matches what is on the desk.
/// </summary>
static class TabletPainter
{
    /// <summary>Where the parts ended up, for callers that label or connect them.</summary>
    public sealed record Layout(Rect Body, Rect Surface, IReadOnlyList<Rect> Keys, IReadOnlyList<Rect> Dials, bool KeysOnRight, bool Screen = true);

    public static Layout Measure(TabletModel model, Rect body, bool turned)
    {
        Rect Place(RectD share)
        {
            // Shares are given with the keys on the left; a turned tablet mirrors both ways.
            double x = turned ? 1 - share.Right : share.X, y = turned ? 1 - share.Bottom : share.Y;
            return new Rect(body.X + x * body.Width, body.Y + y * body.Height, share.Width * body.Width, share.Height * body.Height);
        }

        var surface = Place(new RectD(model.ActiveAreaMm.X / model.WidthMm, model.ActiveAreaMm.Y / model.HeightMm,
            model.ActiveAreaMm.Width / model.WidthMm, model.ActiveAreaMm.Height / model.HeightMm));
        double strip = model.ControlStripShare, inset = 0.022;
        Rect Control(ControlSpot spot, double widthShare) =>
            Place(new RectD(inset + (strip - widthShare) / 2, spot.CenterY - spot.Length / 2, widthShare, spot.Length));
        return new Layout(body, surface,
            [.. model.Keys.Select(k => Control(k, strip))],
            [.. model.Dials.Select(d => Control(d, strip * 0.62))],
            turned, model.HasScreen);
    }

    public static void Draw(DrawingContext context, Control owner, Layout layout, int highlightedKey = -1, int highlightedDial = -1)
    {
        double radius = layout.Body.Height * 0.045;
        context.DrawRectangle(Palette.Brush(owner, "XpTabletBody"), Palette.Pen(owner, "XpTabletEdge", 1.5), layout.Body, radius, radius);
        // A pen display shows its screen; a tablet without one, the outline of the area the pen works on.
        if (layout.Screen)
            context.DrawRectangle(Palette.Brush(owner, "XpTabletScreen"), null, layout.Surface, 2, 2);
        else
            context.DrawRectangle(null, Palette.Pen(owner, "XpTabletKey", 2), layout.Surface.Deflate(1), 6, 6);

        var key = Palette.Brush(owner, "XpTabletKey");
        var accent = Palette.Brush(owner, "XpAccent");
        for (int i = 0; i < layout.Keys.Count; i++)
        {
            var r = layout.Keys[i];
            context.DrawRectangle(i == highlightedKey ? accent : key, null, r, r.Width * 0.22, r.Width * 0.22);
        }
        for (int d = 0; d < layout.Dials.Count; d++)
        {
            var r = layout.Dials[d];
            context.DrawRectangle(d == highlightedDial ? accent : key, null, r, r.Width / 2, r.Width / 2);
            // The grip lines of a roller.
            var grip = Palette.Pen(owner, "XpTabletBody", 1);
            for (int i = 1; i <= 3; i++)
            {
                double y = r.Y + r.Height * i / 4;
                context.DrawLine(grip, new Point(r.X + r.Width * 0.2, y), new Point(r.Right - r.Width * 0.2, y));
            }
        }
    }
}
