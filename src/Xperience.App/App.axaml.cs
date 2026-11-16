using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Xperience.App.ViewModels;
using Xperience.App.Views;
using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.App;

/// <summary>What was prepared before the UI thread started.</summary>
public sealed record StartupState(ICompositor? Compositor, Func<TabletPresence> Scan, string Version, string Built, string? Page, string? Theme);

public sealed class App : Application
{
    internal static StartupState? Start { get; set; }

    MainViewModel? _main;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Start is { } start)
        {
            RequestedThemeVariant = start.Theme?.ToLowerInvariant() switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            _main = new MainViewModel(start.Compositor, start.Scan, start.Version, start.Built);
            _main.Open(start.Page);
            desktop.MainWindow = new MainWindow { DataContext = _main };
            desktop.ShutdownRequested += (_, _) => _main.Dispose();
            _ = _main.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
