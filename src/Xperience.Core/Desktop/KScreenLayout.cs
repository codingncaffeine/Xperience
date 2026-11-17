using System.Text.Json;

namespace Xperience.Core.Desktop;

/// <summary>Reads the screen layout out of what <c>kscreen-doctor -j</c> prints.</summary>
public static class KScreenLayout
{
    /// <summary>The enabled screens, in the desktop's own numbering. Text that is not a layout gives no screens.</summary>
    public static IReadOnlyList<ScreenInfo> Parse(string json)
    {
        var screens = new List<ScreenInfo>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
                return screens;
            foreach (var output in outputs.EnumerateArray())
            {
                if (output.ValueKind != JsonValueKind.Object || !Flag(output, "enabled") || !Flag(output, "connected", missing: true))
                    continue;
                string name = output.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                if (name.Length == 0 ||
                    !output.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Object ||
                    !output.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Object)
                    continue;
                double scale = Number(output, "scale", 1);
                if (scale <= 0)
                    scale = 1;
                var rotation = Number(output, "rotation", 1) switch
                {
                    2 => ScreenRotation.ThreeQuarter,
                    4 => ScreenRotation.Half,
                    8 => ScreenRotation.Quarter,
                    _ => ScreenRotation.None,
                };
                // "size" is the panel's own pixels. The desktop lays the screen out scaled, and turned if rotated.
                double width = Number(size, "width", 0) / scale, height = Number(size, "height", 0) / scale;
                if (rotation is ScreenRotation.Quarter or ScreenRotation.ThreeQuarter)
                    (width, height) = (height, width);
                if (width <= 0 || height <= 0)
                    continue;
                var bounds = new RectD(Number(pos, "x", 0), Number(pos, "y", 0), Math.Ceiling(width), Math.Ceiling(height));
                double widthMm = 0, heightMm = 0;
                if (output.TryGetProperty("sizeMM", out var mm) && mm.ValueKind == JsonValueKind.Object)
                    (widthMm, heightMm) = (Number(mm, "width", 0), Number(mm, "height", 0));
                double brightness = Number(output, "brightness", -1);
                screens.Add(new ScreenInfo(name, (int)Number(output, "priority", 0), bounds, scale, rotation)
                {
                    WidthMm = widthMm,
                    HeightMm = heightMm,
                    Brightness = brightness is >= 0 and <= 1 ? brightness : null,
                });
            }
        }
        catch (JsonException)
        {
            return [];
        }

        // A desktop that numbers nothing still gets a stable order: left to right, top to bottom.
        if (screens.Any(s => s.Number <= 0) || screens.Select(s => s.Number).Distinct().Count() != screens.Count)
        {
            var ordered = screens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i] = ordered[i] with { Number = i + 1 };
            return ordered;
        }
        return [.. screens.OrderBy(s => s.Number)];
    }

    static bool Flag(JsonElement element, string name, bool missing = false) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.True : missing;

    static double Number(JsonElement element, string name, double missing) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) ? d : missing;
}
