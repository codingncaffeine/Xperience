using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Desktop;
using Xperience.Core.Input;
using Xperience.Core.Pen;
using Xperience.Core.Service;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>What the window's state is built on besides the desktop: the hub, the dialogs and the machine's own setup.</summary>
public sealed record AppServices(ControlHub Hub, IDialogs Dialogs, DriverSetup Driver, Autostart Autostart);

/// <summary>
/// The window's state. It holds nothing about the pen that it did not just read: every page shows the last
/// snapshot of the desktop, and every change is written to the desktop and then read back. What the keys,
/// dials and buttons do is Xperience's own to keep, and lives in the hub.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly ICompositor? _compositor;
    readonly Func<TabletPresence> _scan;
    readonly AppServices _services;
    readonly DispatcherTimer _watch;
    PenReader? _reader;
    PadReader? _pad;
    string? _readerNode, _padNode;
    TabletDevices? _synced;
    Task? _refresh;
    bool _refreshAgain;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private string _statusText = "Looking for the tablet…";
    [ObservableProperty] private bool _statusGood;
    [ObservableProperty] private bool _statusWarn;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _editingApp = "";

    public MainViewModel(ICompositor? compositor, Func<TabletPresence> scan, string version, string built, AppServices services)
    {
        _compositor = compositor;
        _scan = scan;
        _services = services;
        Version = version;
        Home = new HomeViewModel(this);
        WorkArea = new WorkAreaViewModel(this);
        Pen = new PenViewModel(this);
        Settings = new SettingsViewModel(this, version, built);
        FloatingMenu = new FloatingMenuViewModel(this);
        _currentPage = Home;
        if (compositor != null)
            compositor.Changed += OnDesktopChanged;
        Hub.Changed += OnHubChanged;
        Hub.Failed += OnHubFailed;
        Hub.PenPosition = () => _reader?.Position;
        // The tablet and the maker's own driver come and go without telling anyone: look every few seconds.
        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += (_, _) => _ = WatchAsync();
    }

    public TabletModel Model => TabletModel.Artist16Third;
    public string Version { get; }
    public string DesktopName => _compositor?.Name ?? "";
    public bool HasDesktop => _compositor != null;
    public ICompositor? Desktop => _compositor;

    public ControlHub Hub => _services.Hub;
    public IDialogs Dialogs => _services.Dialogs;
    public DriverSetup Driver => _services.Driver;
    public Autostart Autostart => _services.Autostart;

    public HomeViewModel Home { get; }
    public WorkAreaViewModel WorkArea { get; }
    public PenViewModel Pen { get; }
    public SettingsViewModel Settings { get; }
    public FloatingMenuViewModel FloatingMenu { get; }

    public DesktopSnapshot Snapshot { get; private set; } = DesktopSnapshot.Empty;
    public TabletPresence Presence { get; private set; } = TabletPresence.None;
    public DriverStatus DriverStatus { get; private set; } = new(false, false, false, DriverOptions.Default);

    /// <summary>The settings as they stand.</summary>
    public XperienceConfig Config => Hub.Config;

    /// <summary>The profile the pages are editing: the one picked on the tablet page.</summary>
    public Profile EditingProfile => Config.Find(EditingApp) ?? Config.DefaultProfile;

    /// <summary>The pen's newest report, or an empty one when it cannot be read.</summary>
    public PenSample LatestPen => _reader?.Latest ?? default;
    public bool CanReadPen => _reader is { Ended: false };
    public PenReader? PenReader => _reader;

    public bool IsTabletPage => CurrentPage == Home || CurrentPage == WorkArea || CurrentPage == FloatingMenu;
    public bool IsPenPage => CurrentPage == Pen;
    public bool IsSettingsPage => CurrentPage == Settings;

    partial void OnCurrentPageChanged(object value)
    {
        OnPropertyChanged(nameof(IsTabletPage));
        OnPropertyChanged(nameof(IsPenPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    partial void OnEditingAppChanged(string value) => Show();

    [RelayCommand] void ShowHome() => CurrentPage = Home;
    [RelayCommand] void ShowWorkArea() => CurrentPage = WorkArea;
    [RelayCommand] void ShowPen() => CurrentPage = Pen;
    [RelayCommand] void ShowSettings() => CurrentPage = Settings;
    [RelayCommand] void ShowFloatingMenu() => CurrentPage = FloatingMenu;
    [RelayCommand] Task Refresh() => RefreshAsync();

    public void Open(string? page)
    {
        CurrentPage = page?.ToLowerInvariant() switch
        {
            "workarea" or "work-area" => WorkArea,
            "pen" => Pen,
            "settings" => Settings,
            "floatingmenu" or "floating-menu" => FloatingMenu,
            _ => Home,
        };
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        await Hub.StartAsync();
        _synced = Snapshot.Devices;
        Show();
        _watch.Start();
    }

    void OnDesktopChanged() => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    void OnHubChanged() => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    void OnHubFailed(string message) => Dispatcher.UIThread.Post(() => Problem = message);

    /// <summary>
    /// Reads the tablet and the desktop again, off the UI thread, and shows the result. Asked for while a read
    /// is under way, it has that read followed by another, and is done when the last one is shown: whoever
    /// waits for it sees the window in step with what was just changed.
    /// </summary>
    public Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false } running)
        {
            _refreshAgain = true;
            return running;
        }
        return _refresh = RefreshCoreAsync();
    }

    async Task RefreshCoreAsync()
    {
        {
            do
            {
                _refreshAgain = false;
                var compositor = _compositor;
                var scan = _scan;
                var model = Model;
                var driver = Driver;
                var (presence, snapshot, status, failure) = await Task.Run(async () =>
                {
                    var found = scan();
                    var installed = driver.Read();
                    try
                    {
                        return (found, compositor != null ? await compositor.ReadAsync(model).ConfigureAwait(false) : DesktopSnapshot.Empty, installed, (string?)null);
                    }
                    catch (Exception e) when (KWinCompositor.IsBusFault(e) || e is IOException or InvalidOperationException)
                    {
                        return (found, DesktopSnapshot.Empty, installed, e.Message);
                    }
                });
                await UseReadersAsync(presence.PenNode, presence.PadNode);
                Presence = presence;
                Snapshot = snapshot;
                DriverStatus = status;
                if (failure != null)
                    Problem = $"The desktop did not answer: {failure}";
                // New device names mean the bindings have to be filed under them.
                if (_synced != null && snapshot.Devices != _synced)
                {
                    _synced = snapshot.Devices;
                    await Hub.SyncAsync();
                }
                Show();
            }
            while (_refreshAgain);
        }
    }

    async Task WatchAsync()
    {
        var scan = _scan;
        var presence = await Task.Run(scan);
        if (presence != Presence || (_reader?.Ended ?? false) || (_pad?.Ended ?? false))
            await RefreshAsync();
    }

    async Task UseReadersAsync(string? penNode, string? padNode)
    {
        if (penNode != _readerNode || _reader is { Ended: true })
        {
            var old = _reader;
            _reader = null;
            _readerNode = penNode;
            var hub = Hub;
            _reader = await Task.Run(() =>
            {
                old?.Dispose();
                var reader = PenReader.Open(penNode);
                if (reader != null)
                    reader.ButtonChanged += (upper, down) => _ = hub.PenButtonAsync(upper, down);
                return reader;
            });
        }
        if (padNode != _padNode || _pad is { Ended: true })
        {
            var old = _pad;
            _pad = null;
            _padNode = padNode;
            var hub = Hub;
            _pad = await Task.Run(() =>
            {
                old?.Dispose();
                return PadReader.Open(padNode, (key, down) => _ = hub.KeyAsync(key, down));
            });
        }
    }

    void Show()
    {
        bool present = Presence.Mode != DriveMode.Absent;
        StatusGood = present && Snapshot.Pen != null && !Presence.VendorAppRunning;
        StatusWarn = present && !StatusGood;
        StatusText = Presence.Mode switch
        {
            DriveMode.Absent => "Not connected",
            DriveMode.Driver => "Connected  ·  Xperience driver",
            _ => "Connected  ·  built-in mode",
        };
        Notice =
            Presence.VendorAppRunning ? "The XPPen app is running. It takes the pen over while it runs, so the settings here will not hold until it is closed."
            : !HasDesktop ? "This desktop's pen settings cannot be reached from here. Xperience works with KDE Plasma on Wayland for now."
            : present && Snapshot.Pen == null ? "The tablet is connected, but the desktop does not list its pen yet. Unplug it and plug it back in."
            : null;
        if (Config.Find(EditingApp) == null && EditingApp.Length > 0)
        {
            // The profile being edited was removed; showing starts over with the one for every application.
            EditingApp = "";
            return;
        }
        Home.Update();
        WorkArea.Update();
        Pen.Update();
        Settings.Update();
        FloatingMenu.Update();
    }

    /// <summary>Writes one change to the desktop, then reads everything back so the window shows what took effect.</summary>
    public Task WriteAsync(Func<ICompositor, string, Task> change) => Pending = WriteCoreAsync(change);

    /// <summary>The change most recently started, finished or not.</summary>
    public Task Pending { get; internal set; } = Task.CompletedTask;

    async Task WriteCoreAsync(Func<ICompositor, string, Task> change)
    {
        if (_compositor is not { } compositor || Snapshot.Pen is not { } pen)
            return;
        try
        {
            await Task.Run(() => change(compositor, pen.Id));
            Problem = null;
        }
        catch (Exception e) when (KWinCompositor.IsBusFault(e) || e is IOException or InvalidOperationException)
        {
            Problem = $"The desktop refused the change: {e.Message}";
        }
        await RefreshAsync();
    }

    /// <summary>Changes Xperience's own settings: saved, applied and shown.</summary>
    public Task ChangeConfigAsync(Func<XperienceConfig, XperienceConfig> change) => Pending = ChangeConfigCoreAsync(change);

    async Task ChangeConfigCoreAsync(Func<XperienceConfig, XperienceConfig> change)
    {
        Problem = null;
        await Hub.UseAsync(change(Hub.Config));
        await RefreshAsync();
    }

    public Task IdentifyScreensAsync() => _compositor is { } compositor ? Task.Run(() => compositor.IdentifyScreensAsync()) : Task.CompletedTask;

    /// <summary>
    /// Waits for the pen to come back after its driver changed, and gives the pen that comes back the settings
    /// the pen had before. The desktop files pen settings under the device's name, and a driver change renames it.
    /// </summary>
    public async Task CarryPenSettingsAsync(PenState before, int attempts = 40, int pauseMs = 250)
    {
        if (_compositor is not { } compositor)
            return;
        var backup = PenBackup.Of(before);
        var model = Model;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(pauseMs);
            DesktopSnapshot snapshot;
            try
            {
                snapshot = await Task.Run(() => compositor.ReadAsync(model));
            }
            catch (Exception e) when (KWinCompositor.IsBusFault(e) || e is IOException or InvalidOperationException)
            {
                continue;
            }
            if (snapshot.Pen is not { } pen)
                continue;
            // The same device under the same name kept its settings; give a rename a little time to show.
            if (pen.Name == before.Name && pen.Id == before.Id && attempt < attempts / 3)
                continue;
            try
            {
                await Task.Run(() => backup.ApplyAsync(compositor, pen, snapshot.Screens));
            }
            catch (Exception e) when (KWinCompositor.IsBusFault(e) || e is IOException or InvalidOperationException)
            {
                Problem = $"The pen's settings could not be carried over to {pen.Name}: {e.Message}";
            }
            break;
        }
        await RefreshAsync();
    }

    public void Dispose()
    {
        _watch.Stop();
        if (_compositor != null)
            _compositor.Changed -= OnDesktopChanged;
        Hub.Changed -= OnHubChanged;
        Hub.Failed -= OnHubFailed;
        _reader?.Dispose();
        _pad?.Dispose();
    }
}
