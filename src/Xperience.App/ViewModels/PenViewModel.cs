using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>How the pen feels: its pressure response, its buttons, and what it is reporting right now.</summary>
public sealed partial class PenViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>With the top of the range this low, any touch at all is full pressure.</summary>
    const double PressureOffMax = 0.002;

    bool _loading;

    [ObservableProperty] private PressureCurve _curve = PressureCurve.Linear;
    [ObservableProperty] private double _rangeMin;
    [ObservableProperty] private double _rangeMax = 1;
    [ObservableProperty] private bool _canSetRange;
    [ObservableProperty] private int _preset = -1;
    [ObservableProperty] private bool _mouseMode;
    [ObservableProperty] private double _mouseSpeed;
    [ObservableProperty] private bool _canSetMouseSpeed;
    [ObservableProperty] private bool _pressureOn = true;
    [ObservableProperty] private bool _tiltOn = true;
    [ObservableProperty] private bool _upperIsEraser = true;
    [ObservableProperty] private bool _canSetDriverOptions;
    [ObservableProperty] private string _driverNote = "";
    [ObservableProperty] private bool _hasPen;
    [ObservableProperty] private bool _isDefault = true;
    [ObservableProperty] private string _profileName = "";

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
            var profile = main.EditingProfile;
            var driver = main.DriverStatus;
            HasPen = pen != null;
            var pressure = pen?.Pressure ?? PressureSettings.Default;
            PressureOn = pressure.RangeMax > PressureOffMax * 2;
            if (PressureOn)
            {
                Curve = pressure.Curve;
                RangeMin = pressure.RangeMin;
                RangeMax = pressure.RangeMax;
            }
            CanSetRange = pen?.CanSetPressureRange ?? false;
            MouseMode = pen?.MouseMode ?? false;
            MouseSpeed = pen?.MouseSpeed ?? 0;
            CanSetMouseSpeed = pen?.CanSetMouseSpeed ?? false;
            Mark();

            bool driven = main.Presence.Mode == DriveMode.Driver;
            UpperIsEraser = !(driven && driver.Options.UpperIsButton);
            TiltOn = !(driven && driver.Options.TiltOff);
            CanSetDriverOptions = driven && driver.CanInstall && driver.LoaderInstalled;
            DriverNote = driven ? (CanSetDriverOptions ? "Changing these reloads the driver, which asks for administrator rights. The pen is away for a moment." : "")
                : "These two are choices of the Xperience driver, which Settings can install.";
            UpperLabel = UpperIsEraser ? "Eraser while held" : profile.PenUpper.Label;
            LowerLabel = profile.PenLower.Kind == ActionKind.Default ? "Pen button" : profile.PenLower.Label;
            ProfileName = main.Config.Profiles.Count > 1 ? $"Buttons for: {profile.Name}" : "";
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
        for (int i = 0; i < PressureCurve.Presets.Count && fullRange && PressureOn; i++)
        {
            if (PressureCurve.Presets[i].IsCloseTo(Curve, 0.01))
                Preset = i;
        }
        IsDefault = PressureOn && fullRange && Curve.IsCloseTo(PressureCurve.Linear, 0.01);
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
        if (!_loading)
            _ = main.WriteAsync((desktop, pen) => desktop.SetMouseModeAsync(pen, value));
    }

    /// <summary>Writes the mouse-mode speed as it stands. Called when the slider is let go.</summary>
    public Task ApplySpeedAsync()
    {
        double speed = MouseSpeed;
        return main.WriteAsync((desktop, pen) => desktop.SetMouseSpeedAsync(pen, speed));
    }

    partial void OnPressureOnChanged(bool value)
    {
        if (!_loading)
            _ = SetPressureOnAsync(value);
    }

    /// <summary>
    /// Pressure off makes every touch full pressure, by squeezing the range to nothing. The response in use is
    /// kept in the settings so that turning pressure on again brings it back.
    /// </summary>
    async Task SetPressureOnAsync(bool on)
    {
        if (main.Snapshot.Pen is not { } pen)
            return;
        if (!on)
        {
            string keep = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pen.Pressure.Curve.ToDesktopString()}|{pen.Pressure.RangeMin:R}|{pen.Pressure.RangeMax:R}");
            await main.ChangeConfigAsync(c => c with { PressureWhenOn = keep });
            await main.WriteAsync((desktop, id) => desktop.SetPressureAsync(id, new PressureSettings(PressureCurve.Linear, 0, PressureOffMax)));
        }
        else
        {
            var back = ParseKept(main.Config.PressureWhenOn);
            await main.WriteAsync((desktop, id) => desktop.SetPressureAsync(id, back));
        }
    }

    internal static PressureSettings ParseKept(string kept)
    {
        string[] parts = kept.Split('|');
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length == 3 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out double min) &&
            double.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out double max) && max > PressureOffMax * 2 && min >= 0 && min < max)
            return new PressureSettings(PressureCurve.Parse(parts[0]), min, max);
        return PressureSettings.Default;
    }

    partial void OnTiltOnChanged(bool value)
    {
        if (!_loading)
            _ = SetDriverOptionsAsync(main.DriverStatus.Options with { TiltOff = !value });
    }

    partial void OnUpperIsEraserChanged(bool value)
    {
        if (!_loading)
            _ = SetDriverOptionsAsync(main.DriverStatus.Options with { UpperIsButton = !value });
    }

    Task SetDriverOptionsAsync(DriverOptions options) => main.Pending = main.Settings.InstallDriverAsync(options);

    [RelayCommand]
    public async Task EditLower()
    {
        var profile = main.EditingProfile;
        var result = await main.Dialogs.EditActionAsync(new ActionRequest("Lower pen button", profile.PenLower, PadAction.PenButton, ForPenButton: true));
        if (result != null)
            await main.ChangeConfigAsync(c => c.Change(profile.App, p => p with { PenLower = result }));
    }

    [RelayCommand]
    public async Task EditUpper()
    {
        if (UpperIsEraser)
        {
            await main.Dialogs.ConfirmAsync("Upper pen button",
                "The upper button is the pen's eraser: holding it turns the pen over, as far as applications can tell. To give it another job, switch \"Upper button erases\" off under Options first.", "OK");
            return;
        }
        var profile = main.EditingProfile;
        var result = await main.Dialogs.EditActionAsync(new ActionRequest("Upper pen button", profile.PenUpper, PadAction.PenButton, ForPenButton: true));
        if (result != null)
            await main.ChangeConfigAsync(c => c.Change(profile.App, p => p with { PenUpper = result }));
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
