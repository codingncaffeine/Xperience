using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>
/// The window's state. It holds nothing about the pen that it did not just read: every page shows the last
/// snapshot of the desktop, and every change is written to the desktop and then read back.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly ICompositor? _compositor;
    readonly Func<TabletPresence> _scan;
    readonly DispatcherTimer _watch;
    PenReader? _reader;
    string? _readerNode;
    bool _refreshing, _refreshAgain;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private string _statusText = "Looking for the tablet…";
    [ObservableProperty] private bool _statusGood;
    [ObservableProperty] private bool _statusWarn;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _problem;

    public MainViewModel(ICompositor? compositor, Func<TabletPresence> scan, string version, string built)
    {
        _compositor = compositor;
        _scan = scan;
        Version = version;
        Home = new HomeViewModel(this);
        WorkArea = new WorkAreaViewModel(this);
        Pen = new PenViewModel(this);
        Settings = new SettingsViewModel(this, version, built);
        _currentPage = Home;
        if (compositor != null)
            compositor.Changed += OnDesktopChanged;
        // The tablet and the maker's own driver come and go without telling anyone: look every few seconds.
        _watch = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => _ = WatchAsync());
    }

    public TabletModel Model => TabletModel.Artist16Third;
    public string Version { get; }
    public string DesktopName => _compositor?.Name ?? "";
    public bool HasDesktop => _compositor != null;

    public HomeViewModel Home { get; }
    public WorkAreaViewModel WorkArea { get; }
    public PenViewModel Pen { get; }
    public SettingsViewModel Settings { get; }

    public DesktopSnapshot Snapshot { get; private set; } = DesktopSnapshot.Empty;
    public TabletPresence Presence { get; private set; } = TabletPresence.None;

    /// <summary>The pen's newest report, or an empty one when it cannot be read.</summary>
    public PenSample LatestPen => _reader?.Latest ?? default;
    public bool CanReadPen => _reader is { Ended: false };

    public bool IsTabletPage => CurrentPage == Home || CurrentPage == WorkArea;
    public bool IsPenPage => CurrentPage == Pen;
    public bool IsSettingsPage => CurrentPage == Settings;

    partial void OnCurrentPageChanged(object value)
    {
        OnPropertyChanged(nameof(IsTabletPage));
        OnPropertyChanged(nameof(IsPenPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    [RelayCommand] void ShowHome() => CurrentPage = Home;
    [RelayCommand] void ShowWorkArea() => CurrentPage = WorkArea;
    [RelayCommand] void ShowPen() => CurrentPage = Pen;
    [RelayCommand] void ShowSettings() => CurrentPage = Settings;
    [RelayCommand] Task Refresh() => RefreshAsync();

    public void Open(string? page)
    {
        CurrentPage = page?.ToLowerInvariant() switch
        {
            "workarea" or "work-area" => WorkArea,
            "pen" => Pen,
            "settings" => Settings,
            _ => Home,
        };
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _watch.Start();
    }

    void OnDesktopChanged() => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    /// <summary>Reads the tablet and the desktop again, off the UI thread, and shows the result.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var compositor = _compositor;
                var scan = _scan;
                var model = Model;
                var (presence, snapshot, failure) = await Task.Run(async () =>
                {
                    var found = scan();
                    try
                    {
                        return (found, compositor != null ? await compositor.ReadAsync(model).ConfigureAwait(false) : DesktopSnapshot.Empty, (string?)null);
                    }
                    catch (Exception e) when (KWinCompositor.IsBusFault(e) || e is IOException or InvalidOperationException)
                    {
                        return (found, DesktopSnapshot.Empty, e.Message);
                    }
                });
                await UseReaderAsync(presence.PenNode);
                Presence = presence;
                Snapshot = snapshot;
                Problem = failure == null ? null : $"The desktop did not answer: {failure}";
                Show();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    async Task WatchAsync()
    {
        var scan = _scan;
        var presence = await Task.Run(scan);
        if (presence != Presence || (_reader?.Ended ?? false))
            await RefreshAsync();
    }

    async Task UseReaderAsync(string? node)
    {
        if (node == _readerNode && _reader is not { Ended: true })
            return;
        var old = _reader;
        _reader = null;
        _readerNode = node;
        _reader = await Task.Run(() =>
        {
            old?.Dispose();
            return PenReader.Open(node);
        });
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
        Home.Update();
        WorkArea.Update();
        Pen.Update();
        Settings.Update();
    }

    /// <summary>Writes one change to the desktop, then reads everything back so the window shows what took effect.</summary>
    public Task WriteAsync(Func<ICompositor, string, Task> change) => Pending = WriteCoreAsync(change);

    /// <summary>The write most recently started, finished or not.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

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

    public Task IdentifyScreensAsync() => _compositor is { } compositor ? Task.Run(() => compositor.IdentifyScreensAsync()) : Task.CompletedTask;

    public void Dispose()
    {
        _watch.Stop();
        if (_compositor != null)
            _compositor.Changed -= OnDesktopChanged;
        _reader?.Dispose();
    }
}
