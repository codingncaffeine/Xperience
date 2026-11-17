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
    IReadOnlyList<ControlSpot> Dials)
{
    /// <summary>The share of the body the control strip takes on the key side.</summary>
    public double ControlStripShare => 0.058;

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
