using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Xperience.App.ViewModels;
using Xperience.App.Views;
using Xperience.App.Views.Dialogs;
using Xperience.Core.Desktop;
using Xperience.Core.Input;
using Xperience.Core.Service;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.App;

/// <summary>What was prepared before the UI thread started.</summary>
public sealed record StartupState(ICompositor? Compositor, Func<TabletPresence> Scan, string Version, string Built, string? Page, string? Theme,
    ConfigStore Store, IInjector Injector, DriverSetup Driver, Autostart Autostart, bool Background, bool Demo, string? InstancePath)
{
    /// <summary>The record of the session; nobody's when it could not be opened.</summary>
    public Xperience.Core.Diagnostics.ILog Log { get; init; } = Xperience.Core.Diagnostics.NoLog.Instance;

    /// <summary>Where that record is kept; empty when there is none.</summary>
    public string LogPath { get; init; } = "";
}

public sealed class App : Application, IHubUi
{
    internal static StartupState? Start { get; set; }

    MainViewModel? _main;
    MainWindow? _window;
    FloatingMenuWindow? _menu;
    InstanceLink? _link;
    ControlHub? _hub;
    WindowIcon? _icon;
    bool _quitting;

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
            // The program outlives its window: the keys that need it keep working with the window closed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _hub = new ControlHub(start.Compositor, start.Store, start.Injector, TabletModel.Artist16Third) { Ui = this, Log = start.Log };
            var dialogs = new WindowDialogs(() => _window, () => _main?.PenReader);
            _main = new MainViewModel(start.Compositor, start.Scan, start.Version, start.Built, new AppServices(_hub, dialogs, start.Driver, start.Autostart) { Log = start.Log, LogPath = start.LogPath });
            _main.Open(OpenPage(start.Page));
            _main.FloatingMenu.ShowRequested += ToggleMenu;
            _hub.Changed += () => Dispatcher.UIThread.Post(() => _menu?.Fill());
            _icon = MakeIcon();
            if (!start.Demo)
            {
                if (start.InstancePath is { } path)
                    _link = InstanceLink.Listen(path, InstanceLink.ThisBuild, word => Dispatcher.UIThread.Post(() => OnWord(word)), () => Dispatcher.UIThread.Post(Quit));
                AddTrayIcon();
            }
            desktop.ShutdownRequested += (_, _) => End();
            if (!start.Background)
                ShowMain();
            _ = StartAsync(start.Page);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static string? OpenPage(string? page) => page != null && page.StartsWith("dialog:", StringComparison.OrdinalIgnoreCase) ? DialogPage(page) : page;

    /// <summary>The page a dialog belongs to, for the screenshots that open a page with one of its dialogs.</summary>
    static string DialogPage(string page) => page.ToLowerInvariant() switch
    {
        "dialog:area" or "dialog:tablet-area" => "workarea",
        "dialog:pen" => "pen",
        "dialog:menu" or "menu-window" => "floatingmenu",
        _ => "home",
    };

    async Task StartAsync(string? page)
    {
        if (_main is not { } main)
            return;
        await main.StartAsync();
        // Screenshots ask for a page with one of its dialogs open.
        switch (page?.ToLowerInvariant())
        {
            case "dialog:dial": _ = main.Home.EditDial(0); break;
            case "dialog:area": _ = main.WorkArea.CustomScreenArea(); break;
            case "dialog:tablet-area": _ = main.WorkArea.CustomActiveArea(); break;
            case "dialog:pen": _ = main.Pen.EditLower(); break;
            case "dialog:menu": _ = main.FloatingMenu.EditButton(0); break;
            case "menu-window": ToggleMenu(); break;
            case { } other when other.StartsWith("dialog:key", StringComparison.Ordinal) &&
                int.TryParse(other["dialog:key".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int key) && key is >= 1 and <= 8:
                _ = main.Home.EditKey(key - 1);
                break;
        }
    }

    void OnWord(string word)
    {
        if (word == "show")
            ShowMain();
    }

    // ---- what the hub asks for; it calls from its own thread ----

    void IHubUi.ShowWindow() => Dispatcher.UIThread.Post(ShowMain);

    void IHubUi.ToggleWindow() => Dispatcher.UIThread.Post(() =>
    {
        // In front already: the key that shows the window puts it away again. The program keeps running.
        if (_window is { IsVisible: true, IsActive: true, WindowState: not WindowState.Minimized })
            _window.Hide();
        else
            ShowMain();
    });
    void IHubUi.ToggleFloatingMenu() => Dispatcher.UIThread.Post(ToggleMenu);

    void ShowMain()
    {
        if (_main == null)
            return;
        if (_window == null)
        {
            _window = new MainWindow { DataContext = _main, Icon = _icon };
            _window.Closing += (_, e) =>
            {
                // Closing the window leaves the program running when the settings say so.
                if (_quitting || Start is { Demo: true } || !_main.Config.KeepRunning)
                    return;
                e.Cancel = true;
                _window.Hide();
            };
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!_quitting)
                    Quit();
            };
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void ToggleMenu()
    {
        if (_main == null)
            return;
        if (_menu is { IsVisible: true })
        {
            _menu.Hide();
            return;
        }
        _menu ??= new FloatingMenuWindow(_main);
        _ = _main.Hub.PrepareTypingAsync();
        _menu.ShowQuietly();
    }

    void AddTrayIcon()
    {
        var show = new NativeMenuItem("Show Xperience");
        show.Click += (_, _) => ShowMain();
        var menu = new NativeMenuItem("Floating menu");
        menu.Click += (_, _) => ToggleMenu();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        var tray = new TrayIcon { Icon = _icon, ToolTipText = "Xperience", Menu = [show, menu, new NativeMenuItemSeparator(), quit] };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, [tray]);
    }

    /// <summary>The program's mark, as the window, the task switcher and the tray show it.</summary>
    static WindowIcon? MakeIcon()
    {
        try
        {
            using var picture = Avalonia.Platform.AssetLoader.Open(new Uri("avares://xperience/Assets/icon.png"));
            return new WindowIcon(picture);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    void Quit()
    {
        _quitting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    void End()
    {
        _quitting = true;
        _link?.Dispose();
        _link = null;
        _main?.Dispose();
        _hub?.Dispose();
        if (Start?.Compositor is { } compositor)
        {
            // The desktop is told to stop reporting to a program that is about to be gone.
            try
            {
                compositor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The desktop went first; there is nobody left to tell.
            }
        }
    }
}
