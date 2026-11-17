using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>Switches for the whole program, the settings file, the driver, and what was found on this machine.</summary>
public sealed partial class SettingsViewModel(MainViewModel main, string version, string built) : ObservableObject
{
    bool _loading;

    [ObservableProperty] private bool _makersAppInstalled;
    [ObservableProperty] private bool _makersAppStarts;
    [ObservableProperty] private string _tabletLine = "";
    [ObservableProperty] private string _modeLine = "";
    [ObservableProperty] private string _desktopLine = "";
    [ObservableProperty] private string _penLine = "";
    [ObservableProperty] private string _mappingLine = "";
    [ObservableProperty] private string _position = "—";
    [ObservableProperty] private string _pressure = "—";
    [ObservableProperty] private string _tilt = "—";
    [ObservableProperty] private string _tool = "—";

    [ObservableProperty] private bool _hints;
    [ObservableProperty] private bool _keysDisabled;
    [ObservableProperty] private bool _keepRunning;
    [ObservableProperty] private bool _startWithDesktop;

    [ObservableProperty] private string _driverTitle = "";
    [ObservableProperty] private string _driverText = "";
    [ObservableProperty] private string _driverCommand = "";
    [ObservableProperty] private string _installLabel = "Install driver";
    [ObservableProperty] private bool _canInstallDriver;
    [ObservableProperty] private bool _canRemoveDriver;
    [ObservableProperty] private bool _driverBusy;
    [ObservableProperty] private string? _message;

    public string Version => version;
    public string Built => built;

    public void Update()
    {
        _loading = true;
        try
        {
            var presence = main.Presence;
            var pen = main.Snapshot.Pen;
            var config = main.Config;
            TabletLine = presence.Mode == DriveMode.Absent
                ? "Not connected"
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"XPPen {main.Model.Name}  ·  USB {main.Model.VendorId:x4}:{main.Model.ProductId:x4}");
            ModeLine = presence.Mode switch
            {
                DriveMode.Driver => "Xperience driver: the keys and dials are real controls",
                DriveMode.BuiltIn => "Built-in mode: the keys and dials type the tablet's fixed shortcuts",
                _ => "—",
            };
            DesktopLine = main.HasDesktop ? $"{main.DesktopName}  ·  {main.Snapshot.Screens.Count} monitors" : "No supported desktop found";
            PenLine = pen == null ? "—" : $"{pen.Name}  ·  {pen.Id}";
            string turn = pen == null || pen.Turn == TabletTurn.None ? "" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  ·  turned {(int)pen.Turn}°");
            MappingLine = pen == null ? "—" : pen.TargetKind switch
            {
                ScreenTargetKind.OneScreen => $"{main.Snapshot.Screen(pen.TargetScreen)?.Label ?? pen.TargetScreen}{turn}",
                ScreenTargetKind.AllScreens => $"All monitors{turn}",
                _ => $"The active monitor{turn}",
            };

            Hints = config.Hints;
            KeysDisabled = config.KeysDisabled;
            KeepRunning = config.KeepRunning;
            StartWithDesktop = main.Autostart.Enabled;
            MakersAppInstalled = main.Autostart.MakersAppInstalled;
            MakersAppStarts = main.Autostart.MakersAppStarts;
            ShowDriver(main.DriverStatus, presence.Mode);
        }
        finally
        {
            _loading = false;
        }
    }

    void ShowDriver(DriverStatus status, DriveMode mode)
    {
        DriverCommand = "";
        CanRemoveDriver = status.Installed && status.CanInstall && !DriverBusy;
        CanInstallDriver = status.CanInstall && status.LoaderInstalled && !DriverBusy;
        InstallLabel = status.Installed ? "Reinstall driver" : "Install driver";
        if (!status.CanInstall)
            (DriverTitle, DriverText) = ("Not part of this build", "This copy of Xperience does not carry the driver.");
        else if (!status.LoaderInstalled)
        {
            (DriverTitle, DriverText) = ("One package is missing",
                $"The driver is loaded by the system's loader for such drivers, the package {DriverSetup.LoaderPackage}. Install it with your package manager, then press the refresh button at the top. On Arch Linux:");
            DriverCommand = $"sudo pacman -S {DriverSetup.LoaderPackage}";
        }
        else if (status.Installed && mode == DriveMode.Driver)
            (DriverTitle, DriverText) = ("Installed and loaded", "The tablet's keys and dials are real controls and follow what is assigned on the tablet page.");
        else if (status.Installed)
            (DriverTitle, DriverText) = ("Installed, not loaded", "Unplug the tablet and plug it back in, or reinstall to load the driver now.");
        else
            (DriverTitle, DriverText) = ("Not installed",
                "The driver makes the eight keys and two dials real controls, so they can be assigned, and lets Xperience see them. Installing asks for administrator rights. " +
                "While it loads the pen is away for a moment; if it does not come back, unplug the tablet and plug it in again. Close the XPPen app first.");
    }

    partial void OnHintsChanged(bool value) => Change(c => c with { Hints = value });
    partial void OnKeysDisabledChanged(bool value) => Change(c => c with { KeysDisabled = value });
    partial void OnKeepRunningChanged(bool value) => Change(c => c with { KeepRunning = value });

    void Change(Func<XperienceConfig, XperienceConfig> change)
    {
        if (!_loading)
            _ = main.ChangeConfigAsync(change);
    }

    partial void OnMakersAppStartsChanged(bool value)
    {
        if (_loading)
            return;
        if (!main.Autostart.SetMakersAppStarts(value))
            main.Problem = "The XPPen app's autostart entry in this account was not written by Xperience, so it is left as it is.";
        Update();
    }

    partial void OnStartWithDesktopChanged(bool value)
    {
        if (_loading)
            return;
        if (!main.Autostart.Set(value))
            main.Problem = "The autostart entry could not be changed.";
        Update();
    }

    /// <summary>Where the record of the session is kept; empty when there is none.</summary>
    public string LogPath => main.LogPath;
    public bool HasLog => main.LogPath.Length > 0;

    /// <summary>Opens the folder the record is in with the desktop's file manager.</summary>
    [RelayCommand]
    void OpenLogFolder()
    {
        if (Path.GetDirectoryName(main.LogPath) is not { Length: > 0 } folder)
            return;
        try
        {
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            main.Problem = $"The folder could not be opened; it is {folder}.";
        }
    }

    /// <summary>The driver's options with the switch keys the settings ask for right now.</summary>
    DriverOptions WantedOptions() => main.DriverStatus.Options with
    {
        SwitchKeys = Assignments.SwitchKeys(main.Config, main.Snapshot.Pen?.Turn ?? TabletTurn.None),
    };

    /// <summary>
    /// Reloads the driver when the keys set to switch between pen and eraser are not the ones it was loaded
    /// with. Called after the settings or the tablet's rotation changed; does nothing when they agree, and
    /// nothing where the driver is not in place, since installing it later takes the keys along.
    /// </summary>
    public Task SyncDriverAsync()
    {
        var status = main.DriverStatus;
        var wanted = WantedOptions();
        if (main.Presence.Mode != DriveMode.Driver || !status.Installed || !status.CanInstall || !status.LoaderInstalled || wanted.SwitchKeys == status.Options.SwitchKeys)
            return Task.CompletedTask;
        return InstallDriverAsync(wanted);
    }

    [RelayCommand]
    Task InstallDriver() => main.Pending = InstallDriverAsync(WantedOptions());

    /// <summary>Installs or reloads the driver with the given options, then gives the pen its settings back.</summary>
    public async Task InstallDriverAsync(DriverOptions options)
    {
        if (DriverBusy)
            return;
        DriverBusy = true;
        try
        {
            var before = main.Snapshot.Pen;
            var driver = main.Driver;
            var (ok, problem) = await Task.Run(() => driver.InstallAsync(options));
            main.Log.Write("driver", $"install with upper button {DriverOptions.Word(options.Upper)}, lower button {DriverOptions.Word(options.Lower)}, tilt {(options.TiltOff ? "off" : "on")}, switch keys {(options.SwitchKeys.Length > 0 ? options.SwitchKeys : "none")}: {(ok ? "done" : "failed: " + problem)}");
            await AfterDriverChangeAsync(ok, problem, before);
        }
        finally
        {
            DriverBusy = false;
            Update();
        }
    }

    [RelayCommand]
    Task RemoveDriver() => main.Pending = RemoveDriverAsync();

    async Task RemoveDriverAsync()
    {
        if (DriverBusy || !await main.Dialogs.ConfirmAsync("Remove driver",
            "Take the Xperience driver out? The tablet goes back to its built-in mode: the pen keeps working, and the keys and dials type the shortcuts fixed in the tablet.", "Remove"))
            return;
        DriverBusy = true;
        try
        {
            var before = main.Snapshot.Pen;
            var driver = main.Driver;
            var (ok, problem) = await Task.Run(driver.RemoveAsync);
            await AfterDriverChangeAsync(ok, problem, before);
        }
        finally
        {
            DriverBusy = false;
            Update();
        }
    }

    async Task AfterDriverChangeAsync(bool ok, string problem, PenState? before)
    {
        if (!ok)
        {
            main.Problem = problem;
            await main.RefreshAsync();
        }
        else if (before != null)
            await main.CarryPenSettingsAsync(before);
        else
            await main.RefreshAsync();
    }

    [RelayCommand]
    public async Task Export()
    {
        string? path = await main.Dialogs.PickFileToSaveAsync("Export settings", "xperience-settings.json", "*.json", "Xperience settings");
        if (path == null)
            return;
        var backup = new Backup { Config = main.Config, Pen = main.Snapshot.Pen is { } pen ? PenBackup.Of(pen) : null };
        try
        {
            await File.WriteAllTextAsync(path, backup.ToJson());
            Message = $"Settings exported to {path}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            main.Problem = $"The settings could not be written to {path}: {e.Message}";
        }
    }

    [RelayCommand]
    public async Task Import()
    {
        string? path = await main.Dialogs.PickFileToOpenAsync("Import settings", "*.json", "Xperience settings");
        if (path == null)
            return;
        Backup? backup;
        try
        {
            backup = Backup.FromJson(await File.ReadAllTextAsync(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            main.Problem = $"{path} could not be read: {e.Message}";
            return;
        }
        if (backup == null)
        {
            main.Problem = $"{path} is not an Xperience settings file.";
            return;
        }
        await main.ChangeConfigAsync(_ => backup.Config);
        if (backup.Pen is { } pen && main.Snapshot.Pen is { } current)
        {
            var screens = main.Snapshot.Screens;
            await main.WriteAsync((desktop, _) => pen.ApplyAsync(desktop, current, screens));
        }
        Message = $"Settings imported from {path}";
    }

    /// <summary>
    /// Everything back to how it starts out, except which monitor the pen is on and which way up the tablet is:
    /// those are the two settings that make the pen usable at all on this desk.
    /// </summary>
    [RelayCommand]
    public async Task RestoreDefaults()
    {
        if (!await main.Dialogs.ConfirmAsync("Default settings",
            "Put the keys, dials, pen buttons, pressure, areas, calibration, profiles and floating menu back to their defaults? The monitor the pen is on and the tablet's rotation stay as they are.", "Restore"))
            return;
        bool keep = main.Config.KeepRunning;
        await main.Hub.RememberAsync(s => s with { DialSlots = [], AreaBeforePrecision = null });
        await main.ChangeConfigAsync(_ => XperienceConfig.Default with { KeepRunning = keep });
        main.EditingApp = "";
        if (main.Snapshot.Pen is { } pen)
        {
            var matrix = PenMatrix.ForTurn(pen.Matrix.QuarterPart());
            await main.WriteAsync(async (desktop, id) =>
            {
                await desktop.SetPressureAsync(id, PressureSettings.Default).ConfigureAwait(false);
                if (pen.MouseMode)
                    await desktop.SetMouseModeAsync(id, false).ConfigureAwait(false);
                if (pen.CanSetMouseSpeed && pen.MouseSpeed != 0)
                    await desktop.SetMouseSpeedAsync(id, 0).ConfigureAwait(false);
                if (pen.CanSetScreenArea)
                    await desktop.SetScreenAreaAsync(id, RectD.Full).ConfigureAwait(false);
                if (pen.CanSetMatrix)
                    await desktop.SetMatrixAsync(id, matrix).ConfigureAwait(false);
            });
        }
        Message = "Settings are back to their defaults.";
    }

    public void Tick()
    {
        var sample = main.LatestPen;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (!main.CanReadPen || !sample.InRange)
        {
            Position = Pressure = Tilt = "—";
            Tool = main.CanReadPen ? "Out of range" : "—";
            return;
        }
        Position = string.Create(culture, $"{sample.X}, {sample.Y}");
        Pressure = string.Create(culture, $"{sample.Pressure} of {main.Model.PressureMax}");
        Tilt = string.Create(culture, $"{sample.TiltX}°, {sample.TiltY}°");
        Tool = (sample.Eraser ? "Eraser" : "Pen") + (sample.Touching ? ", touching" : ", hovering");
    }
}
