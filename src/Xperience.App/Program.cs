using Avalonia;
using Xperience.App.ViewModels;
using Xperience.Core.Desktop;
using Xperience.Core.Input;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"Xperience {version}");
            return 0;
        }
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine("""
                Usage: xperience [--background | --status | --check-writes | --check-apps]

                  --background     Start without showing the window: the tray icon, the keys' special actions,
                                   application profiles and the floating menu are there, the window opens on request.
                  --status         Print what was found (the tablet, the driver, the desktop, the pen's settings,
                                   the bindings) and exit.
                  --check-writes   Write each pen setting back with the value it already has, to see that
                                   this desktop accepts it. Changes nothing.
                  --check-apps     Ask the desktop which applications have a window open and which one is
                                   active, the way application profiles do. Changes nothing.
                  --version        Print the version and exit.
                """);
            return 0;
        }

        // Everything that waits on the bus or the disk happens here, before the UI thread exists.
        string? demo = Environment.GetEnvironmentVariable("XPERIENCE_DEMO");
        bool tool = args.Contains("--status") || args.Contains("--check-writes") || args.Contains("--check-apps");
        ICompositor? compositor;
        Func<TabletPresence> scan;
        ConfigStore store;
        IInjector injector;
        DriverSetup driver;
        Autostart autostart;
        string? instancePath = null;
        if (!string.IsNullOrEmpty(demo))
        {
            // A desktop in memory, for screenshots and for looking at the window without a tablet. Nothing of
            // the real account is read or written: the settings live in a folder of the demo's own.
            string mode = demo.ToLowerInvariant();
            var presence = mode switch
            {
                "absent" => TabletPresence.None,
                "driver" => new TabletPresence(DriveMode.Driver, null, null, null, false),
                "vendor" => new TabletPresence(DriveMode.BuiltIn, null, null, "/dev/null", true),
                _ => new TabletPresence(DriveMode.BuiltIn, null, null, "/dev/null", false),
            };
            var sample = DemoCompositor.Sample();
            if (presence.Mode == DriveMode.Absent)
                sample.SetPen(null);
            if (presence.Mode == DriveMode.Driver)
                sample.SetPad("XPPen Artist 16 3rd Pad");
            if (mode == "fresh")
            {
                // The pen as a desktop has it before anything is set.
                sample.SetTargetAsync("", ScreenTargetKind.ActiveScreen, "").GetAwaiter().GetResult();
                sample.SetFlippedAsync("", false).GetAwaiter().GetResult();
            }
            string home = Environment.GetEnvironmentVariable("XPERIENCE_DEMO_HOME") is { Length: > 0 } given
                ? given : Path.Combine(Path.GetTempPath(), "xperience-demo-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            compositor = sample;
            scan = () => presence;
            store = new ConfigStore(Path.Combine(home, "config"), Path.Combine(home, "state"));
            injector = new NoInjector();
            driver = DemoDriver(Path.Combine(home, "system"), presence.Mode == DriveMode.Driver);
            autostart = new Autostart(Path.Combine(home, "autostart", "xperience.desktop"), "xperience", Path.Combine(home, "system", "etc/xdg/autostart/xppentablet.desktop"));
        }
        else
        {
            instancePath = InstanceLink.DefaultPath;
            // A second start hands over to the copy already running and leaves. A running copy of another build
            // ends instead, and this one takes its place once it is gone.
            if (!tool && instancePath != null)
            {
                var handover = InstanceLink.Send(instancePath, args.Contains("--background") ? "hello" : "show", InstanceLink.ThisBuild);
                if (handover == Handover.Taken)
                    return 0;
                if (handover == Handover.Replacing && !InstanceLink.WaitUntilFree(instancePath, TimeSpan.FromSeconds(8)))
                {
                    Console.Error.WriteLine("Another build of Xperience is running and did not end. Quit it from its tray icon, then start again.");
                    return 1;
                }
            }
            compositor = KWinCompositor.ConnectAsync().GetAwaiter().GetResult();
            scan = static () => TabletScanner.Scan(TabletModel.Artist16Third);
            store = ConfigStore.ForThisAccount();
            injector = new UinputInjector();
            driver = DriverSetup.ForThisBuild();
            autostart = Autostart.ForThisAccount();
        }

        if (args.Contains("--status"))
            return PrintStatus(compositor, scan, driver, store);
        if (args.Contains("--check-writes"))
            return CheckWrites(compositor);
        if (args.Contains("--check-apps"))
            return CheckApps(compositor);

        // The record of the session sits beside the program's state. It is opened only now, so the short
        // commands above leave no trace in it.
        var log = Xperience.Core.Diagnostics.SessionLog.Open(store.StateFolder);
        if (log != null)
        {
            log.Write("start", $"Xperience {version}, built {BuildTime()}, build {InstanceLink.ThisBuild[..8]}{(string.IsNullOrEmpty(demo) ? "" : ", demo")}"
                + $"{(args.Length > 0 ? ", started with " + string.Join(' ', args) : "")}; "
                + $"session {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"} on {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"}, "
                + $"kernel {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                log.Write("crash", e.ExceptionObject.ToString() ?? "an exception nothing caught");
                log.Settle();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                log.Write("start", "Xperience ended");
                log.Dispose();
            };
        }

        App.Start = new StartupState(compositor, scan, version, BuildTime(),
            Environment.GetEnvironmentVariable("XPERIENCE_OPEN"), Environment.GetEnvironmentVariable("XPERIENCE_THEME"),
            store, injector, driver, autostart, args.Contains("--background"), !string.IsNullOrEmpty(demo), instancePath)
        {
            Log = (Xperience.Core.Diagnostics.ILog?)log ?? Xperience.Core.Diagnostics.NoLog.Instance,
            LogPath = log?.Path ?? "",
        };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>A make-believe system folder, so the demo shows the driver's card without touching the real one.</summary>
    static DriverSetup DemoDriver(string root, bool installed)
    {
        string carried = Path.Combine(root, "build", DriverSetup.ObjectName), helper = Path.Combine(root, "build", "xperience-driver-helper");
        Directory.CreateDirectory(Path.Combine(root, "build"));
        Directory.CreateDirectory(Path.Combine(root, "usr/bin"));
        File.WriteAllText(carried, "");
        File.WriteAllText(helper, "");
        File.WriteAllText(Path.Combine(root, "usr/bin/udev-hid-bpf"), "");
        if (installed)
        {
            Directory.CreateDirectory(Path.Combine(root, "etc/udev/rules.d"));
            Directory.CreateDirectory(Path.Combine(root, "etc/udev-hid-bpf"));
            File.WriteAllText(Path.Combine(root, "etc/udev/rules.d/70-xperience.rules"), "");
            File.WriteAllText(Path.Combine(root, "etc/udev-hid-bpf", DriverSetup.ObjectName), "");
        }
        return new DriverSetup(root, carried, helper, _ => Task.FromResult((0, "")));
    }

    static int PrintStatus(ICompositor? compositor, Func<TabletPresence> scan, DriverSetup driver, ConfigStore store)
    {
        var presence = scan();
        Console.WriteLine($"Tablet:   {presence.Mode}" + (presence.VendorAppRunning ? "  (the XPPen app is running)" : ""));
        Console.WriteLine($"  pen node      {presence.PenNode ?? "-"}");
        Console.WriteLine($"  pad node      {presence.PadNode ?? "-"}");
        Console.WriteLine($"  keyboard node {presence.KeyboardNode ?? "-"}");
        // The pen's own node, when this account may read it: the range it reports is asked of the kernel.
        using (var reader = Xperience.Core.Pen.PenReader.Open(presence.PenNode))
            Console.WriteLine(reader == null ? "  pen readable  no" : $"  pen readable  yes, range {reader.MaxX} x {reader.MaxY}");
        var status = driver.Read();
        Console.WriteLine($"Driver:   {(status.Installed ? "installed" : "not installed")}, loader {(status.LoaderInstalled ? "present" : "missing")}, " +
            $"this build {(status.CanInstall ? "carries it" : "does not carry it")}");
        Console.WriteLine($"  upper button  {DriverOptions.Word(status.Options.Upper)}");
        Console.WriteLine($"  lower button  {DriverOptions.Word(status.Options.Lower)}");
        Console.WriteLine($"  switch keys   {(status.Options.SwitchKeys.Length > 0 ? status.Options.SwitchKeys : "none")}");
        Console.WriteLine($"  tilt          {(status.Options.TiltOff ? "off" : "on")}");
        var config = store.Load();
        Console.WriteLine($"Settings: {store.ConfigPath}");
        Console.WriteLine($"  profiles      {string.Join(", ", config.Profiles.Select(p => p.Name))}");
        Console.WriteLine($"  keys          {string.Join(" | ", config.DefaultProfile.Keys.Select(k => k.Label))}");
        if (compositor == null)
        {
            Console.WriteLine("Desktop:  none that Xperience can talk to");
            return 1;
        }
        var snapshot = compositor.ReadAsync(TabletModel.Artist16Third).GetAwaiter().GetResult();
        Console.WriteLine($"Desktop:  {compositor.Name}");
        foreach (var screen in snapshot.Screens)
            Console.WriteLine($"  {screen.Label}  at {screen.Bounds.X:0},{screen.Bounds.Y:0}  scale {screen.Scale:0.##}  turned {(int)screen.Rotation}°");
        if (snapshot.PadName != null)
            Console.WriteLine($"Keys:     {snapshot.PadName}");
        var rebinds = compositor.ReadRebindsAsync().GetAwaiter().GetResult();
        foreach (var rebind in rebinds.Where(r => r.Device == snapshot.PadName || r.Device == snapshot.Pen?.Name))
            Console.WriteLine($"  binding       {rebind.Group} {rebind.Key} = {rebind.Value}");
        if (snapshot.Pen is not { } pen)
        {
            Console.WriteLine("Pen:      not listed by the desktop");
            return 0;
        }
        Console.WriteLine($"Pen:      {pen.Name} ({pen.Id})");
        Console.WriteLine($"  target        {pen.TargetKind} {pen.TargetScreen}".TrimEnd());
        Console.WriteLine($"  screen area   {pen.ScreenArea.X:0.###} {pen.ScreenArea.Y:0.###} {pen.ScreenArea.Width:0.###} {pen.ScreenArea.Height:0.###}");
        Console.WriteLine($"  turned        {(int)pen.Turn}°  (flip {pen.Flipped})");
        Console.WriteLine($"  calibration   {pen.Matrix.A:0.####} {pen.Matrix.B:0.####} {pen.Matrix.C:0.####} / {pen.Matrix.D:0.####} {pen.Matrix.E:0.####} {pen.Matrix.F:0.####}");
        Console.WriteLine($"  mouse mode    {pen.MouseMode}  speed {pen.MouseSpeed:0.##}");
        Console.WriteLine($"  pressure      {pen.Pressure.Curve.ToDesktopString()}  range {pen.Pressure.RangeMin:0.###}-{pen.Pressure.RangeMax:0.###}");
        return 0;
    }

    /// <summary>Writes the pen's settings back as they are and reads them again: nothing changes, and every
    /// setting this program can write is shown to be accepted by the desktop.</summary>
    static int CheckWrites(ICompositor? compositor)
    {
        if (compositor == null)
        {
            Console.WriteLine("No desktop that Xperience can talk to.");
            return 1;
        }
        var model = TabletModel.Artist16Third;
        var before = compositor.ReadAsync(model).GetAwaiter().GetResult().Pen;
        if (before == null)
        {
            Console.WriteLine("The desktop does not list the pen.");
            return 1;
        }
        int failures = 0;
        void Try(string what, Func<Task> write)
        {
            try
            {
                write().GetAwaiter().GetResult();
                Console.WriteLine($"  accepted  {what}");
            }
            catch (Exception e) when (KWinCompositor.IsBusFault(e))
            {
                failures++;
                Console.WriteLine($"  REFUSED   {what}: {e.Message}");
            }
        }
        Try("target screen", () => compositor.SetTargetAsync(before.Id, before.TargetKind, before.TargetScreen));
        Try("screen area", () => compositor.SetScreenAreaAsync(before.Id, before.ScreenArea));
        Try("turned 180°", () => compositor.SetFlippedAsync(before.Id, before.Flipped));
        Try("mouse mode", () => compositor.SetMouseModeAsync(before.Id, before.MouseMode));
        Try("mouse speed", () => compositor.SetMouseSpeedAsync(before.Id, before.MouseSpeed));
        Try("pressure", () => compositor.SetPressureAsync(before.Id, before.Pressure));
        Try("calibration", () => compositor.SetMatrixAsync(before.Id, before.Matrix));
        PenState? after = null;
        try
        {
            after = compositor.ReadAsync(model).GetAwaiter().GetResult().Pen;
        }
        catch (Exception e) when (KWinCompositor.IsBusFault(e))
        {
            Console.WriteLine($"The settings could not be read back: {e.Message}");
        }
        bool same = after == before;
        Console.WriteLine(same ? "Settings are exactly as they were." : $"SETTINGS CHANGED\n  before {before}\n  after  {after}");
        return failures == 0 && same ? 0 : 1;
    }

    /// <summary>Asks the desktop what application profiles need from it: the open applications and the active one.</summary>
    static int CheckApps(ICompositor? compositor)
    {
        if (compositor == null)
        {
            Console.WriteLine("No desktop that Xperience can talk to.");
            return 1;
        }
        var apps = compositor.RunningAppsAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Applications with a window open: {apps.Count}");
        foreach (var app in apps)
            Console.WriteLine($"  {app.Id}");
        string active = "";
        using var told = new ManualResetEventSlim();
        compositor.ActiveAppChanged += app =>
        {
            active = app;
            told.Set();
        };
        bool watching = compositor.WatchActiveAppAsync(true).GetAwaiter().GetResult();
        bool heard = watching && told.Wait(TimeSpan.FromSeconds(3));
        compositor.WatchActiveAppAsync(false).GetAwaiter().GetResult();
        Console.WriteLine(heard ? $"Active application: {(active.Length > 0 ? active : "(none)")}" : "The desktop did not report the active application.");
        return apps.Count > 0 && heard ? 0 : 1;
    }

    /// <summary>When this build was made, shown in Settings so it is always clear which build is running.</summary>
    static string BuildTime()
    {
        try { return File.GetLastWriteTime(typeof(Program).Assembly.Location).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    /// <summary>
    /// The name the window carries for the desktop. A taskbar finds a program's entry and icon by it, so the
    /// installed desktop entry names the same class (StartupWMClass); set here so it cannot drift with the
    /// program file's name.
    /// </summary>
    public const string WindowClass = "xperience";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().With(new X11PlatformOptions { WmClass = WindowClass }).WithInterFont();
}
