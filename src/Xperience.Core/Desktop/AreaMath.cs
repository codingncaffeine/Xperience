namespace Xperience.Core.Desktop;

/// <summary>The arithmetic of mapping the pen's surface onto a screen.</summary>
public static class AreaMath
{
    /// <summary>
    /// The largest part of a screen with the same shape as the pen's surface, centered, as shares of the screen.
    /// Mapping the pen there keeps circles round at the cost of the screen's edges.
    /// </summary>
    public static RectD SameShape(double surfaceAspect, double screenAspect)
    {
        if (surfaceAspect <= 0 || screenAspect <= 0)
            return RectD.Full;
        if (screenAspect > surfaceAspect)
        {
            double width = surfaceAspect / screenAspect;
            return new RectD((1 - width) / 2, 0, width, 1);
        }
        double height = screenAspect / surfaceAspect;
        return new RectD(0, (1 - height) / 2, 1, height);
    }

    /// <summary>How much wider than tall a stroke comes out: 1 means circles stay round.</summary>
    public static double Stretch(double surfaceAspect, double screenAspect, RectD screenArea)
    {
        if (surfaceAspect <= 0 || screenArea.Height <= 0)
            return 1;
        return screenAspect * screenArea.Width / screenArea.Height / surfaceAspect;
    }

    /// <summary>A share-of-screen rectangle kept inside the screen and never thinner than a twentieth of it.</summary>
    public static RectD Clamp(RectD area)
    {
        double width = Math.Clamp(area.Width, 0.05, 1), height = Math.Clamp(area.Height, 0.05, 1);
        return new RectD(Math.Clamp(area.X, 0, 1 - width), Math.Clamp(area.Y, 0, 1 - height), width, height);
    }
}
