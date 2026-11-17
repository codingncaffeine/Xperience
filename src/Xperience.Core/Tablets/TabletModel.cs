namespace Xperience.Core.Tablets;

/// <summary>A key or a dial on the tablet's edge: where it sits, as shares of the tablet's body with the keys on the left.</summary>
public readonly record struct ControlSpot(double CenterY, double Length);

/// <summary>What is fixed about a tablet model: its identity, its surface and where its controls sit.</summary>
public sealed record TabletModel(
    string Name,
    ushort VendorId,
    ushort ProductId,
    double WidthMm,
    double HeightMm,
    RectD ActiveAreaMm,
    int PressureMax,
    IReadOnlyList<ControlSpot> Keys,
    IReadOnlyList<ControlSpot> Dials,
    bool HasScreen = true)
{
    /// <summary>
    /// The tablet this run of the app is for: found when it starts (<see cref="TabletDetector"/>), the Artist 16
    /// 3rd when none is plugged in.
    /// </summary>
    public static TabletModel Active
    {
        get => _active ?? Artist16Third;
        set => _active = value;
    }

    static TabletModel? _active;

    /// <summary>
    /// A model from what a tablet said it is: its range in its own units and their number per inch, its keys,
    /// and, from the model list, its dials and whether it is a pen display. The body is the active area with a
    /// margin, and a strip for the keys and dials on the left when there are any; the controls are spread
    /// evenly along it. The Artist 16 3rd keeps its measured body.
    /// </summary>
    public static TabletModel FromAnswer(string name, ushort productId, int xMax, int yMax, int pressureMax, int unitsPerInch,
        int keys, int dials, bool screen)
    {
        double width = xMax * 25.4 / unitsPerInch, height = yMax * 25.4 / unitsPerInch;
        if (productId == Artist16Third.ProductId && xMax == 68199 && yMax == 38359 && keys == 8 && dials == 2)
            return Artist16Third with { Name = name, PressureMax = pressureMax };

        keys = Math.Clamp(keys, 0, 16);
        dials = Math.Clamp(dials, 0, 2);
        double margin = height * 0.09, strip = keys + dials > 0 ? width * 0.12 : margin;
        double bodyWidth = strip + width + margin, bodyHeight = height + 2 * margin;
        // Keys above and below the dials, the way the maker places them: half the keys, the dials, the rest.
        int controls = keys + dials, upper = (keys + 1) / 2;
        double step = controls > 0 ? 0.84 / Math.Max(controls, 6) : 0, top = 0.5 - step * controls / 2;
        var keySpots = new List<ControlSpot>();
        var dialSpots = new List<ControlSpot>();
        for (int i = 0, place = 0; i < controls; i++, place++)
        {
            double center = top + step * (place + 0.5);
            if (i >= upper && i < upper + dials)
                dialSpots.Add(new ControlSpot(center, step * 1.1));
            else
                keySpots.Add(new ControlSpot(center, step * 0.8));
        }
        return new TabletModel(name, 0x28bd, productId, bodyWidth, bodyHeight, new RectD(strip, margin, width, height),
            pressureMax, keySpots, dialSpots, screen);
    }

    /// <summary>The share of the body the control strip takes on the key side.</summary>
    public double ControlStripShare => ProductId == 0x097e ? 0.058 : ActiveAreaMm.X / WidthMm * 0.5;

    /// <summary>
    /// XPPen Artist 16 3rd. The body and the active area are the maker's figures; the active area is centered
    /// top to bottom and pushed away from the key strip. Four keys, two dials, four keys run down one edge.
    /// </summary>
    public static readonly TabletModel Artist16Third = new(
        Name: "Artist 16 3rd",
        VendorId: 0x28bd,
        ProductId: 0x097e,
        WidthMm: 407.9,
        HeightMm: 232.8,
        ActiveAreaMm: new RectD(46.0, 20.5, 341.0, 191.8),
        PressureMax: 16383,
        Keys:
        [
            new(0.115, 0.058), new(0.190, 0.058), new(0.265, 0.058), new(0.340, 0.058),
            new(0.660, 0.058), new(0.735, 0.058), new(0.810, 0.058), new(0.885, 0.058),
        ],
        Dials: [new(0.445, 0.085), new(0.555, 0.085)]);
}
