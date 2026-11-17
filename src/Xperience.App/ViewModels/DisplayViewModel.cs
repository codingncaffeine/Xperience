using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Xperience.App.ViewModels;

/// <summary>
/// The tablet's own screen. Its brightness is the desktop's setting for that monitor, read from the desktop and
/// written there; everything else about the picture is in the desktop's display settings, one button away.
/// </summary>
public sealed partial class DisplayViewModel(MainViewModel main) : ObservableObject
{
    bool _loading;
    string _screen = "";

    [ObservableProperty] private bool _hasScreen;
    [ObservableProperty] private string _screenLabel = "";
    [ObservableProperty] private bool _canSetBrightness;
    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private string _note = "";

    public MainViewModel Main => main;

    public string BrightnessText => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{Brightness:0}%");

    public void Update()
    {
        _loading = true;
        try
        {
            var own = main.Snapshot.OwnScreen(main.Model);
            _screen = own?.Name ?? "";
            HasScreen = own != null;
            ScreenLabel = own?.Label ?? "";
            CanSetBrightness = main.HasDesktop && own?.Brightness != null;
            if (own?.Brightness is { } share)
                Brightness = Math.Round(share * 100);
            Note = own == null
                ? (main.Snapshot.Screens.Count == 0 ? "" : "None of the monitors the desktop lists is the size of the tablet's screen, so Xperience cannot tell which one is the tablet. The desktop's display settings list them all.")
                : own.Brightness == null ? "The desktop does not report a brightness for the tablet's screen." : "";
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnBrightnessChanged(double value) => OnPropertyChanged(nameof(BrightnessText));

    /// <summary>Writes the brightness as it stands. Called when the slider is let go, not for every step on the way.</summary>
    public Task ApplyBrightnessAsync()
    {
        if (_loading || !CanSetBrightness || _screen.Length == 0)
            return Task.CompletedTask;
        string screen = _screen;
        double share = Math.Clamp(Brightness, 0, 100) / 100;
        return main.WriteAsync((desktop, _) => desktop.SetScreenBrightnessAsync(screen, share));
    }

    [RelayCommand]
    Task DesktopSettings() => main.Desktop is { } desktop ? Task.Run(() => desktop.OpenDisplaySettingsAsync()) : Task.CompletedTask;
}
