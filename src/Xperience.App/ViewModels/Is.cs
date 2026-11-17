using System.Globalization;
using Avalonia.Data.Converters;

namespace Xperience.App.ViewModels;

/// <summary>Small converters the views share.</summary>
public static class Is
{
    /// <summary>True when the number bound equals the number given as the parameter.</summary>
    public static readonly IValueConverter Equal = new FuncValueConverter<int, string?, bool>(
        (value, parameter) => int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted) && value == wanted);

    /// <summary>Full strength when true, faded when false: for parts that are there but switched off.</summary>
    public static readonly IValueConverter Dimmed = new FuncValueConverter<bool, double>(on => on ? 1 : 0.4);
}
