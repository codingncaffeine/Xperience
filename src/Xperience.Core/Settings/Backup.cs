using System.Text.Json;
using System.Text.Json.Serialization;
using Xperience.Core.Desktop;

namespace Xperience.Core.Settings;

/// <summary>The pen settings the desktop keeps, as they are written into an exported file.</summary>
public sealed record PenBackup
{
    public ScreenTargetKind TargetKind { get; init; }
    public string TargetScreen { get; init; } = "";
    public RectD ScreenArea { get; init; } = RectD.Full;
    public bool Flipped { get; init; }
    public bool MouseMode { get; init; }
    public double MouseSpeed { get; init; }
    public string PressureCurve { get; init; } = "";
    public double PressureMin { get; init; }
    public double PressureMax { get; init; } = 1;
    public string Matrix { get; init; } = "";

    public static PenBackup Of(PenState pen) => new()
    {
        TargetKind = pen.TargetKind,
        TargetScreen = pen.TargetScreen,
        ScreenArea = pen.ScreenArea,
        Flipped = pen.Flipped,
        MouseMode = pen.MouseMode,
        MouseSpeed = pen.MouseSpeed,
        PressureCurve = pen.Pressure.Curve.ToDesktopString(),
        PressureMin = pen.Pressure.RangeMin,
        PressureMax = pen.Pressure.RangeMax,
        Matrix = pen.Matrix.ToDesktopString(),
    };

    public PressureSettings Pressure => new(Pen.PressureCurve.Parse(PressureCurve), PressureMin, PressureMax);

    /// <summary>
    /// Writes these settings to a pen. The screen is only set when the desktop has one of that name, so a file
    /// from another machine cannot send the pen to a monitor that is not there.
    /// </summary>
    public async Task ApplyAsync(ICompositor desktop, PenState pen, IReadOnlyList<ScreenInfo> screens)
    {
        if (TargetKind != ScreenTargetKind.OneScreen || screens.Any(s => s.Name == TargetScreen))
            await desktop.SetTargetAsync(pen.Id, TargetKind, TargetScreen).ConfigureAwait(false);
        if (pen.CanSetScreenArea)
            await desktop.SetScreenAreaAsync(pen.Id, AreaMath.Clamp(ScreenArea)).ConfigureAwait(false);
        if (pen.CanFlip)
            await desktop.SetFlippedAsync(pen.Id, Flipped).ConfigureAwait(false);
        await desktop.SetMouseModeAsync(pen.Id, MouseMode).ConfigureAwait(false);
        if (pen.CanSetMouseSpeed)
            await desktop.SetMouseSpeedAsync(pen.Id, MouseSpeed).ConfigureAwait(false);
        await desktop.SetPressureAsync(pen.Id, Pressure).ConfigureAwait(false);
        if (pen.CanSetMatrix)
            await desktop.SetMatrixAsync(pen.Id, PenMatrix.Parse(Matrix)).ConfigureAwait(false);
    }
}

/// <summary>An exported settings file: what Xperience keeps and what the desktop keeps for the pen, together.</summary>
public sealed record Backup
{
    /// <summary>Marks the file as one of ours, with the format's number.</summary>
    public int Xperience { get; init; } = 1;

    public XperienceConfig Config { get; init; } = XperienceConfig.Default;
    public PenBackup? Pen { get; init; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads an exported file; null when it is not one.</summary>
    public static Backup? FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("xperience", out var mark) || mark.ValueKind != JsonValueKind.Number)
                return null;
            var backup = JsonSerializer.Deserialize<Backup>(json, Json);
            return backup == null ? null : backup with { Config = (backup.Config ?? XperienceConfig.Default).Completed() };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
