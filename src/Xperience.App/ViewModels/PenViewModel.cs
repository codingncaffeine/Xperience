using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>How the pen feels: its pressure response, its buttons, and what it is reporting right now.</summary>
public sealed partial class PenViewModel(MainViewModel main) : ObservableObject
{
    bool _loading;

    [ObservableProperty] private PressureCurve _curve = PressureCurve.Linear;
    [ObservableProperty] private double _rangeMin;
    [ObservableProperty] private double _rangeMax = 1;
    [ObservableProperty] private bool _canSetRange;
    [ObservableProperty] private int _preset = -1;
    [ObservableProperty] private bool _mouseMode;
    [ObservableProperty] private bool _hasPen;
    [ObservableProperty] private bool _isDefault = true;

    [ObservableProperty] private int _pressureRaw;
    [ObservableProperty] private double _pressureShare;
    [ObservableProperty] private bool _upperPressed;
    [ObservableProperty] private bool _lowerPressed;
    [ObservableProperty] private bool _touching;
    [ObservableProperty] private string _liveNote = "";
    [ObservableProperty] private string _upperLabel = "";
    [ObservableProperty] private string _lowerLabel = "";

    public MainViewModel Main => main;
    public int PressureMax => main.Model.PressureMax;

    public void Update()
    {
        _loading = true;
        try
        {
            var pen = main.Snapshot.Pen;
            HasPen = pen != null;
            var pressure = pen?.Pressure ?? PressureSettings.Default;
            Curve = pressure.Curve;
            RangeMin = pressure.RangeMin;
            RangeMax = pressure.RangeMax;
            CanSetRange = pen?.CanSetPressureRange ?? false;
            MouseMode = pen?.MouseMode ?? false;
            Mark();
            UpperLabel = "Eraser while held";
            LowerLabel = main.Presence.Mode == DriveMode.Driver ? "Pen button" : "Pen button, as each app uses it";
            LiveNote = !HasPen ? "" : main.CanReadPen ? "" : "Live pressure cannot be read: this account has no access to the pen's input device.";
        }
        finally
        {
            _loading = false;
        }
    }

    void Mark()
    {
        bool fullRange = RangeMin <= 0.001 && RangeMax >= 0.999;
        Preset = -1;
        for (int i = 0; i < PressureCurve.Presets.Count && fullRange; i++)
        {
            if (PressureCurve.Presets[i].IsCloseTo(Curve, 0.01))
                Preset = i;
        }
        IsDefault = fullRange && Curve.IsCloseTo(PressureCurve.Linear, 0.01);
    }

    /// <summary>Writes the curve as it stands in the editor. Called when a drag ends.</summary>
    public Task ApplyAsync()
    {
        var pressure = new PressureSettings(Curve, RangeMin, RangeMax);
        return main.WriteAsync((desktop, pen) => desktop.SetPressureAsync(pen, pressure));
    }

    [RelayCommand]
    Task UsePreset(string index)
    {
        var curve = PressureCurve.Presets[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)];
        return main.WriteAsync((desktop, pen) => desktop.SetPressureAsync(pen, new PressureSettings(curve, 0, 1)));
    }

    [RelayCommand]
    Task ResetPressure() => main.WriteAsync((desktop, pen) => desktop.SetPressureAsync(pen, PressureSettings.Default));

    partial void OnMouseModeChanged(bool value)
    {
        if (_loading)
            return;
        _ = main.WriteAsync((desktop, pen) => desktop.SetMouseModeAsync(pen, value));
    }

    /// <summary>Takes the pen's newest report. Called by the page a few dozen times a second while it is shown.</summary>
    public void Tick()
    {
        var sample = main.LatestPen;
        PressureRaw = sample.Pressure;
        PressureShare = PressureMax > 0 ? Math.Clamp(sample.Pressure / (double)PressureMax, 0, 1) : 0;
        Touching = sample.Touching;
        LowerPressed = sample.LowerButton;
        // In built-in mode the upper button arrives as the eraser.
        UpperPressed = sample.UpperButton || sample.Eraser;
    }
}
