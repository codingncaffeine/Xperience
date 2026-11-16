using Avalonia;
using Xperience.Core.Desktop;
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
                Usage: xperience [--status]

                  --status         Print what was found (the tablet, the desktop, the pen's settings) and exit.
                  --check-writes   Write each pen setting back with the value it already has, to see that
                                   this desktop accepts it. Changes nothing.
                  --version   Print the version and exit.
                """);
            return 0;
        }

        // Everything that waits on the bus or the disk happens here, before the UI thread exists.
        string? demo = Environment.GetEnvironmentVariable("XPERIENCE_DEMO");
        ICompositor? compositor;
        Func<TabletPresence> scan;
        if (!string.IsNullOrEmpty(demo))
        {
            // A desktop in memory, for screenshots and for looking at the window without a tablet.
            var presence = demo.ToLowerInvariant() switch
            {
                "absent" => TabletPresence.None,
                "driver" => new TabletPresence(DriveMode.Driver, null, "/dev/null", null, false),
                "vendor" => new TabletPresence(DriveMode.BuiltIn, null, null, "/dev/null", true),
                _ => new TabletPresence(DriveMode.BuiltIn, null, null, "/dev/null", false),
            };
            var sample = DemoCompositor.Sample();
            if (presence.Mode == DriveMode.Absent)
                sample.SetPen(null);
            if (demo.Equals("fresh", StringComparison.OrdinalIgnoreCase))
            {
                // The pen as a desktop has it before anything is set.
                sample.SetTargetAsync("", ScreenTargetKind.ActiveScreen, "").GetAwaiter().GetResult();
                sample.SetFlippedAsync("", false).GetAwaiter().GetResult();
            }
            compositor = sample;
            scan = () => presence;
        }
        else
        {
            compositor = KWinCompositor.ConnectAsync().GetAwaiter().GetResult();
            scan = static () => TabletScanner.Scan(TabletModel.Artist16Third);
        }

        if (args.Contains("--status"))
            return PrintStatus(compositor, scan);
        if (args.Contains("--check-writes"))
            return CheckWrites(compositor);

        App.Start = new StartupState(compositor, scan, version, BuildTime(),
            Environment.GetEnvironmentVariable("XPERIENCE_OPEN"), Environment.GetEnvironmentVariable("XPERIENCE_THEME"));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static int PrintStatus(ICompositor? compositor, Func<TabletPresence> scan)
    {
        var presence = scan();
        Console.WriteLine($"Tablet:   {presence.Mode}" + (presence.VendorAppRunning ? "  (the XPPen app is running)" : ""));
        Console.WriteLine($"  pen node      {presence.PenNode ?? "-"}");
        Console.WriteLine($"  pad node      {presence.PadNode ?? "-"}");
        Console.WriteLine($"  keyboard node {presence.KeyboardNode ?? "-"}");
        if (compositor == null)
        {
            Console.WriteLine("Desktop:  none that Xperience can talk to");
            return 1;
        }
        var snapshot = compositor.ReadAsync(TabletModel.Artist16Third).GetAwaiter().GetResult();
        Console.WriteLine($"Desktop:  {compositor.Name}");
        foreach (var screen in snapshot.Screens)
            Console.WriteLine($"  {screen.Label}  at {screen.Bounds.X:0},{screen.Bounds.Y:0}  scale {screen.Scale:0.##}  turned {(int)screen.Rotation}°");
        if (snapshot.Pen is not { } pen)
        {
            Console.WriteLine("Pen:      not listed by the desktop");
            return 0;
        }
        Console.WriteLine($"Pen:      {pen.Name} ({pen.Id})");
        Console.WriteLine($"  target        {pen.TargetKind} {pen.TargetScreen}".TrimEnd());
        Console.WriteLine($"  screen area   {pen.ScreenArea.X:0.###} {pen.ScreenArea.Y:0.###} {pen.ScreenArea.Width:0.###} {pen.ScreenArea.Height:0.###}");
        Console.WriteLine($"  turned 180°   {pen.Flipped}");
        Console.WriteLine($"  mouse mode    {pen.MouseMode}");
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
        Try("pressure", () => compositor.SetPressureAsync(before.Id, before.Pressure));
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

    /// <summary>When this build was made, shown in Settings so it is always clear which build is running.</summary>
    static string BuildTime()
    {
        try { return File.GetLastWriteTime(typeof(Program).Assembly.Location).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
