using Avalonia;
using Avalonia.Headless;
using Xperience.App.ViewModels;
using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Input;
using Xperience.Core.Service;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>The real application, without a display.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    public static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    /// <summary>
    /// Runs a test's body on the UI thread and waits for all of it. The body is handed over as one that
    /// returns a value: handed over as a plain task, only its first step would be waited for, and nothing
    /// asserted after its first wait would ever be seen.
    /// </summary>
    public static Task OnUi(Func<Task> body) => Session.Dispatch(async () =>
    {
        await body();
        return true;
    }, TestContext.Current.CancellationToken);
}

/// <summary>An injector that writes down what it is asked to type.</summary>
public sealed class RecordingInjector : IInjector
{
    public List<string> Typed { get; } = [];
    public bool Allowed { get; set; } = true;
    public int Prepared { get; private set; }

    public bool Prepare()
    {
        Prepared++;
        return Allowed;
    }

    public void Key(int code, bool down)
    {
        lock (Typed)
            Typed.Add((down ? "+" : "-") + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Wheel(int steps)
    {
        lock (Typed)
            Typed.Add("wheel " + steps.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose() { }
}

/// <summary>Answers the pages' questions the way a test set it up to, and writes down what was asked.</summary>
public sealed class FakeDialogs : IDialogs
{
    public List<string> Asked { get; } = [];
    public Func<ActionRequest, PadAction?> Action { get; set; } = _ => null;
    public Func<IReadOnlyList<DialFunction>, int, (IReadOnlyList<DialFunction>, int)?> Dial { get; set; } = (_, _) => null;
    public Func<AreaRequest, RectD?> Area { get; set; } = _ => null;
    public bool Confirm { get; set; } = true;
    public Func<IReadOnlyList<AppInfo>, AppInfo?> App { get; set; } = _ => null;
    public string? FileToOpen { get; set; }
    public string? FileToSave { get; set; }
    public Func<CalibrationRequest, CalibrationResult?> Calibration { get; set; } = _ => null;

    public Task<PadAction?> EditActionAsync(ActionRequest request)
    {
        Asked.Add("action " + request.Title);
        return Task.FromResult(Action(request));
    }

    public Task<(IReadOnlyList<DialFunction> Slots, int Current)?> EditDialAsync(string title, IReadOnlyList<DialFunction> slots, int current, IReadOnlyList<DialFunction> defaults)
    {
        Asked.Add("dial " + title);
        return Task.FromResult(Dial(slots, current));
    }

    public Task<RectD?> EditAreaAsync(AreaRequest request)
    {
        Asked.Add("area " + request.Title);
        LastArea = request;
        return Task.FromResult(Area(request));
    }

    public AreaRequest? LastArea { get; private set; }

    public Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        Asked.Add("confirm " + title);
        return Task.FromResult(Confirm);
    }

    public Task<AppInfo?> PickAppAsync(IReadOnlyList<AppInfo> running)
    {
        Asked.Add("app " + string.Join(",", running.Select(a => a.Id)));
        return Task.FromResult(App(running));
    }

    public Task<string?> PickFileToOpenAsync(string title, string pattern, string kind) => Task.FromResult(FileToOpen);
    public Task<string?> PickFileToSaveAsync(string title, string suggestedName, string pattern, string kind) => Task.FromResult(FileToSave);

    public Task<CalibrationResult?> CalibrateAsync(CalibrationRequest request)
    {
        Asked.Add("calibrate " + request.Screen.Name);
        return Task.FromResult(Calibration(request));
    }
}

/// <summary>
/// Everything a window test needs, in a folder of its own: a desktop held in memory, the hub with its settings
/// file, dialogs that answer by themselves, a make-believe system folder for the driver, and the window's state.
/// </summary>
public sealed class Bench : IDisposable
{
    public const string PadName = "XPPen Artist 16 3rd Pad";
    public static readonly TabletPresence BuiltIn = new(DriveMode.BuiltIn, null, null, "/dev/null", false);
    public static readonly TabletPresence Driven = new(DriveMode.Driver, null, null, null, false);

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));
    public DemoCompositor Desktop { get; } = DemoCompositor.Sample();
    public RecordingInjector Injector { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public List<string> Launched { get; } = [];
    public List<string> Elevated { get; } = [];
    public List<string> Shown { get; } = [];
    public TabletPresence Presence { get; set; }
    public ConfigStore Store { get; }
    public ControlHub Hub { get; }
    public DriverSetup Driver { get; }
    public Autostart Autostart { get; }
    public MainViewModel Main { get; }

    /// <summary>What the make-believe administrator helper does when it is run; by default it succeeds.</summary>
    public Func<IReadOnlyList<string>, (int, string)> Elevate { get; set; } = _ => (0, "");

    Bench(TabletPresence presence, XperienceConfig? config, bool upright)
    {
        Presence = presence;
        // The sample desktop has the tablet turned around, as its owner holds it. Tests about what a key is
        // set to ask for it upright, where the tablet's own key numbers are the places as held.
        if (upright)
            Desktop.SetPen(Desktop.ReadAsync(TabletModel.Artist16Third).GetAwaiter().GetResult().Pen! with { Flipped = false });
        Directory.CreateDirectory(Folder);
        Store = new ConfigStore(Path.Combine(Folder, "config"), Path.Combine(Folder, "state"));
        if (config != null)
            Store.Save(config);
        if (presence.Mode == DriveMode.Driver)
            Desktop.SetPad(PadName);
        Hub = new ControlHub(Desktop, Store, Injector, TabletModel.Artist16Third, app =>
        {
            Launched.Add(app);
            return !app.Contains("missing");
        }) { Ui = new Ui(Shown) };

        string system = Path.Combine(Folder, "system"), build = Path.Combine(Folder, "build");
        Directory.CreateDirectory(Path.Combine(system, "usr/bin"));
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(system, "usr/bin/udev-hid-bpf"), "");
        File.WriteAllText(Path.Combine(build, DriverSetup.ObjectName), "");
        File.WriteAllText(Path.Combine(build, "xperience-driver-helper"), "");
        Driver = new DriverSetup(system, Path.Combine(build, DriverSetup.ObjectName), Path.Combine(build, "xperience-driver-helper"), command =>
        {
            Elevated.Add(string.Join(' ', command.Skip(1).Select(Path.GetFileName)));
            return Task.FromResult(Elevate(command));
        });
        Autostart = new Autostart(Path.Combine(Folder, "autostart", "xperience.desktop"), "/opt/xperience/xperience", Path.Combine(Folder, "system", "etc/xdg/autostart/xppentablet.desktop"));
        Main = new MainViewModel(Desktop, () => Presence, "0.0.0", "", new AppServices(Hub, Dialogs, Driver, Autostart)
        {
            Firmware = new FirmwareReader(Path.Combine(Folder, "usb"), "/dev/bus/usb", (node, index) => UsbTexts(node, index)),
        });
    }

    public string SystemFolder => Path.Combine(Folder, "system");

    /// <summary>What the make-believe tablet answers when asked for one of its USB texts; by default it may not be asked.</summary>
    public Func<string, int, string?> UsbTexts { get; set; } = (_, _) => null;

    /// <summary>Plugs a make-believe tablet into the kernel's list of USB devices, as device 15 on bus 1.</summary>
    public void PlugUsb(int bus = 1, int number = 15)
    {
        string device = Path.Combine(Folder, "usb", "1-2");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "idVendor"), "28bd\n");
        File.WriteAllText(Path.Combine(device, "idProduct"), "097e\n");
        File.WriteAllText(Path.Combine(device, "busnum"), bus.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        File.WriteAllText(Path.Combine(device, "devnum"), number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
    }

    /// <summary>Opens the window's state as the program does at its start.</summary>
    public static async Task<Bench> OpenAsync(TabletPresence? presence = null, XperienceConfig? config = null, bool upright = false)
    {
        var bench = new Bench(presence ?? BuiltIn, config, upright);
        await bench.Main.StartAsync();
        return bench;
    }

    /// <summary>The desktop's pen as it is right now.</summary>
    public async Task<PenState> PenAsync() => (await Desktop.ReadAsync(Main.Model)).Pen!;

    sealed class Ui(List<string> shown) : IHubUi
    {
        public void ShowWindow() => shown.Add("window");
        public void ToggleWindow() => shown.Add("window");
        public void ToggleFloatingMenu() => shown.Add("menu");
    }

    public void Dispose()
    {
        Main.Dispose();
        Hub.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A folder that will not go is left for the system's cleanup of its temporary files.
        }
    }
}
