using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>One line of the lists of what a pen button is to the driver.</summary>
public sealed record RoleChoice(PenButtonRole Role, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How the pen feels: its pressure response, its buttons, and what it is reporting right now.</summary>
public sealed partial class PenViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>What a pen button can be to the driver, as the lists show it.</summary>
    public static IReadOnlyList<RoleChoice> RoleChoices { get; } =
    [
        new(PenButtonRole.Button, "A button to assign"),
        new(PenButtonRole.Eraser, "Eraser while held"),
        new(PenButtonRole.Toggle, "Pen/Eraser switch"),
    ];

    public IReadOnlyList<RoleChoice> Roles => RoleChoices;

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
    [ObservableProperty] private RoleChoice _upperRole = RoleChoices[1];
    [ObservableProperty] private RoleChoice _lowerRole = RoleChoices[0];
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
            // Without the driver the tablet has the buttons as it comes: the upper one erases, the lower one is a button.
            var options = driven ? driver.Options : DriverOptions.Default;
            UpperRole = RoleChoices.First(c => c.Role == options.Upper);
            LowerRole = RoleChoices.First(c => c.Role == options.Lower);
            TiltOn = !(driven && driver.Options.TiltOff);
            CanSetDriverOptions = driven && driver.CanInstall && driver.LoaderInstalled;
            // With the driver in place the lists say in their own tips that changing them reloads it; the note is
            // for when they cannot be changed at all.
            DriverNote = driven ? ""
                : "Tilt and what the pen's buttons are to the driver are choices of the Xperience driver, which Settings can install.";
            UpperLabel = UpperRole.Role != PenButtonRole.Button ? UpperRole.Label : profile.PenUpper.Label;
            LowerLabel = LowerRole.Role != PenButtonRole.Button ? LowerRole.Label
                : profile.PenLower.Kind == ActionKind.Default ? "Pen button" : profile.PenLower.Label;
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

    partial void OnUpperRoleChanged(RoleChoice value)
    {
        if (!_loading && value != null)
            _ = SetDriverOptionsAsync(main.DriverStatus.Options with { Upper = value.Role });
    }

    partial void OnLowerRoleChanged(RoleChoice value)
    {
        if (!_loading && value != null)
            _ = SetDriverOptionsAsync(main.DriverStatus.Options with { Lower = value.Role });
    }

    Task SetDriverOptionsAsync(DriverOptions options) => main.Pending = main.Settings.InstallDriverAsync(options);

    [RelayCommand]
    public async Task EditLower()
    {
        if (LowerRole.Role != PenButtonRole.Button)
        {
            await SayRoleAsync("Lower pen button", "lower", LowerRole);
            return;
        }
        var profile = main.EditingProfile;
        var result = await main.Dialogs.EditActionAsync(new ActionRequest("Lower pen button", profile.PenLower, PadAction.PenButton, ForPenButton: true));
        if (result != null)
            await main.ChangeConfigAsync(c => c.Change(profile.App, p => p with { PenLower = result }));
    }

    [RelayCommand]
    public async Task EditUpper()
    {
        if (UpperRole.Role != PenButtonRole.Button)
        {
            await SayRoleAsync("Upper pen button", "upper", UpperRole);
            return;
        }
        var profile = main.EditingProfile;
        var result = await main.Dialogs.EditActionAsync(new ActionRequest("Upper pen button", profile.PenUpper, PadAction.PenButton, ForPenButton: true));
        if (result != null)
            await main.ChangeConfigAsync(c => c.Change(profile.App, p => p with { PenUpper = result }));
    }

    /// <summary>
    /// Puts the pen back as it comes: the straight pressure response, no mouse mode, and this profile's pen
    /// buttons doing what the desktop has them do. What the buttons are to the driver is the driver's and stays.
    /// </summary>
    [RelayCommand]
    public async Task ResetPen()
    {
        if (main.Snapshot.Pen is not { } pen || !await main.Dialogs.ConfirmAsync("Default pen settings",
            "Put the pressure response, mouse mode and this profile's pen buttons back to their defaults?", "Restore"))
            return;
        var profile = main.EditingProfile;
        await main.ChangeConfigAsync(c => (c with { PressureWhenOn = "" }).Change(profile.App, p => p with { PenLower = PadAction.PenButton, PenUpper = PadAction.PenButton }));
        await main.WriteAsync(async (desktop, id) =>
        {
            await desktop.SetPressureAsync(id, PressureSettings.Default).ConfigureAwait(false);
            if (pen.MouseMode)
                await desktop.SetMouseModeAsync(id, false).ConfigureAwait(false);
            if (pen.CanSetMouseSpeed && pen.MouseSpeed != 0)
                await desktop.SetMouseSpeedAsync(id, 0).ConfigureAwait(false);
        });
    }

    Task SayRoleAsync(string title, string which, RoleChoice role) => main.Dialogs.ConfirmAsync(title,
        role.Role == PenButtonRole.Eraser
            ? $"The {which} button is the pen's eraser: holding it turns the pen over, as far as applications can tell. To give it another job, set it to \"{RoleChoices[0].Label}\" under Options first."
            : $"The {which} button switches between pen and eraser each time it is pressed. To give it another job, set it to \"{RoleChoices[0].Label}\" under Options first.", "OK");

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
