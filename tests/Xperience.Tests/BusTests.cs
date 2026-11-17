using System.Text.RegularExpressions;
using Tmds.DBus.Protocol;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;
using Xperience.Core.Tablets;

namespace Xperience.Tests;

/// <summary>
/// A stand-in for KWin on the test run's own bus: it owns KWin's name, keeps one pen's properties, and loads
/// "scripts" by reading them for the address to call back, as the real one does by running them.
/// </summary>
sealed partial class FakeKWin : IPathMethodHandler, IDisposable
{
    readonly DBusConnection _connection;
    readonly Dictionary<string, object> _pen = new()
    {
        ["vendor"] = 0x28bdu, ["product"] = 0x097eu, ["tabletTool"] = true, ["tabletPad"] = false, ["name"] = "XPPen Artist 16 3rd",
        ["outputName"] = "DP-2", ["mapToWorkspace"] = false, ["outputArea"] = RectD.Full, ["leftHanded"] = true, ["tabletToolIsRelative"] = false,
        ["pressureCurve"] = "0,0;1,1;", ["pressureRangeMin"] = 0.0, ["pressureRangeMax"] = 1.0, ["supportsLeftHanded"] = true,
        ["supportsOutputArea"] = true, ["supportsPressureRange"] = true, ["calibrationMatrix"] = "1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1",
        ["supportsCalibrationMatrix"] = true, ["pointerAcceleration"] = 0.0, ["supportsPointerAcceleration"] = true,
    };
    readonly Dictionary<int, string> _scripts = [];
    int _nextScript = 3;

    FakeKWin(DBusConnection connection) => _connection = connection;

    public List<string> Loaded { get; } = [];
    public List<string> Unloaded { get; } = [];
    public bool ShowingDesktop { get; private set; }

    public object Pen(string property)
    {
        lock (_pen)
            return _pen[property];
    }

    public static async Task<FakeKWin> StartAsync()
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        var fake = new FakeKWin(connection);
        connection.AddMethodHandler(fake);
        // Refused, with an error, when the name is taken: this must never run where a real KWin is.
        await connection.RequestNameAsync("org.kde.KWin", RequestNameOptions.None);
        return fake;
    }

    public string Path => "/";
    public bool HandlesChildPaths => true;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        string path = request.PathAsString ?? "", member = request.MemberAsString ?? "";
        var reader = request.GetBodyReader();
        if (request.InterfaceAsString == "org.freedesktop.DBus.Properties" && member == "Get")
        {
            string owner = reader.ReadString(), property = reader.ReadString();
            object? value = path switch
            {
                "/org/kde/KWin/InputDevice" when property == "devicesSysNames" => new[] { "event3", "event9" },
                "/org/kde/KWin/InputDevice/event9" => PenOrNull(property),
                // A keyboard, to be passed over.
                "/org/kde/KWin/InputDevice/event3" => property switch { "vendor" => 1u, "product" => 1u, _ => false },
                "/KWin" when property == "showingDesktop" => ShowingDesktop,
                "/VirtualKeyboard" when property == "active" => false,
                _ => null,
            };
            if (value == null)
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"{owner}.{property} at {path}");
                return default;
            }
            // The writer is a struct that moves as it writes, so it goes to the helper by reference.
            var writer = context.CreateReplyWriter("v");
            try
            {
                WriteVariant(ref writer, value);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
            return default;
        }
        if (request.InterfaceAsString == "org.freedesktop.DBus.Properties" && member == "Set" && path == "/org/kde/KWin/InputDevice/event9")
        {
            reader.ReadString();
            string property = reader.ReadString();
            string signature = reader.ReadSignatureAsString();
            object value = signature switch
            {
                "s" => reader.ReadString(),
                "b" => reader.ReadBool(),
                "d" => reader.ReadDouble(),
                "(dddd)" => ReadRect(ref reader),
                _ => throw new InvalidOperationException($"a {signature} was written to {property}"),
            };
            lock (_pen)
            {
                Assert.True(_pen.ContainsKey(property), $"no such pen property: {property}");
                Assert.True(_pen[property].GetType() == value.GetType(), $"{property} was written as {value.GetType().Name}");
                _pen[property] = value;
            }
            ReplyEmpty(context);
            return default;
        }
        switch (path, member)
        {
            case ("/Scripting", "loadScript"):
                string file = reader.ReadString(), name = reader.ReadString();
                Loaded.Add(name);
                int id = _nextScript++;
                _scripts[id] = File.ReadAllText(file);
                using (var writer = context.CreateReplyWriter("i"))
                {
                    writer.WriteInt32(id);
                    context.Reply(writer.CreateMessage());
                }
                break;
            case ("/Scripting", "unloadScript"):
                Unloaded.Add(reader.ReadString());
                using (var writer = context.CreateReplyWriter("b"))
                {
                    writer.WriteBool(true);
                    context.Reply(writer.CreateMessage());
                }
                break;
            case (_, "run") when path.StartsWith("/Scripting/Script", StringComparison.Ordinal):
                Run(_scripts[int.Parse(path["/Scripting/Script".Length..], System.Globalization.CultureInfo.InvariantCulture)]);
                ReplyEmpty(context);
                break;
            case ("/KWin", "showDesktop"):
                ShowingDesktop = reader.ReadBool();
                ReplyEmpty(context);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return default;
    }

    object? PenOrNull(string property)
    {
        lock (_pen)
            return _pen.GetValueOrDefault(property);
    }

    static RectD ReadRect(ref Reader reader)
    {
        reader.AlignStruct();
        return new RectD(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }

    static void WriteVariant(ref MessageWriter writer, object value)
    {
        switch (value)
        {
            case string text: writer.WriteVariantString(text); break;
            case bool flag: writer.WriteVariantBool(flag); break;
            case uint number: writer.WriteVariantUInt32(number); break;
            case double number: writer.WriteVariantDouble(number); break;
            case string[] list:
                writer.WriteSignature("as");
                writer.WriteArray(list);
                break;
            case RectD rect:
                writer.WriteSignature("(dddd)");
                writer.WriteStructureStart();
                writer.WriteDouble(rect.X);
                writer.WriteDouble(rect.Y);
                writer.WriteDouble(rect.Width);
                writer.WriteDouble(rect.Height);
                break;
            default: throw new InvalidOperationException(value.GetType().Name);
        }
    }

    static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    [GeneratedRegex("""callDBus\("([^"]+)", "([^"]+)", "([^"]+)", method, text\)""")]
    private static partial Regex CallBack();

    /// <summary>Does what the real KWin does by running the script: calls the program back at the address in it.</summary>
    void Run(string script)
    {
        var address = CallBack().Match(script);
        Assert.True(address.Success, "the script carries no address to call back");
        bool list = script.Contains("workspace.windowList()", StringComparison.Ordinal);
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: address.Groups[1].Value, path: address.Groups[2].Value, @interface: address.Groups[3].Value,
            member: list ? "Windows" : "ActiveApp", signature: "s");
        writer.WriteString(list ? "krita\tDrawing — Krita\nfirefox\tStart page\nkrita\tSecond window" : "krita");
        _ = _connection.CallMethodAsync(writer.CreateMessage());
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// The KDE backend on a real bus, against the stand-in. This is where the shape of every message is checked:
/// a write of the wrong type, a reply read wrongly or a connection that cannot take calls shows up here.
/// </summary>
public class KWinOnTheBusTests
{
    static void NeedsTheTestBus()
    {
        if (Environment.GetEnvironmentVariable("XPERIENCE_TEST_BUS") != "1")
            Assert.Skip("needs the test run's own session bus: run the tests through tools/test.sh");
    }

    [Fact]
    public async Task The_pen_is_read_written_and_read_back_and_the_desktop_calls_back()
    {
        NeedsTheTestBus();
        using var kwin = await FakeKWin.StartAsync();
        await using var desktop = await KWinCompositor.ConnectAsync();
        Assert.NotNull(desktop);
        var cancel = TestContext.Current.CancellationToken;
        var model = TabletModel.Artist16Third;

        var pen = (await desktop.ReadAsync(model, cancel)).Pen!;
        Assert.Equal(("event9", "XPPen Artist 16 3rd", ScreenTargetKind.OneScreen, "DP-2", true, RectD.Full),
            (pen.Id, pen.Name, pen.TargetKind, pen.TargetScreen, pen.Flipped, pen.ScreenArea));
        Assert.Equal((PenMatrix.Identity, true, 0.0, true), (pen.Matrix, pen.CanSetMatrix, pen.MouseSpeed, pen.CanSetMouseSpeed));
        Assert.Null((await desktop.ReadAsync(model, cancel)).PadName);

        // Every setting this program writes, each with its own type on the wire.
        await desktop.SetTargetAsync(pen.Id, ScreenTargetKind.AllScreens, "", cancel);
        await desktop.SetScreenAreaAsync(pen.Id, new RectD(0.25, 0.125, 0.5, 0.75), cancel);
        await desktop.SetFlippedAsync(pen.Id, false, cancel);
        await desktop.SetMouseModeAsync(pen.Id, true, cancel);
        await desktop.SetMouseSpeedAsync(pen.Id, 0.5, cancel);
        await desktop.SetPressureAsync(pen.Id, new PressureSettings(PressureCurve.Through(0.6, 0.3), 0.125, 0.875), cancel);
        await desktop.SetMatrixAsync(pen.Id, PenMatrix.QuarterTurn, cancel);

        Assert.Equal((true, "", new RectD(0.25, 0.125, 0.5, 0.75), false, true, 0.5),
            (kwin.Pen("mapToWorkspace"), kwin.Pen("outputName"), kwin.Pen("outputArea"), kwin.Pen("leftHanded"), kwin.Pen("tabletToolIsRelative"), kwin.Pen("pointerAcceleration")));
        Assert.Equal(("0,-1,1,0,1,0,0,0,0,0,1,0,0,0,0,1", 0.125, 0.875), (kwin.Pen("calibrationMatrix"), kwin.Pen("pressureRangeMin"), kwin.Pen("pressureRangeMax")));

        var after = (await desktop.ReadAsync(model, cancel)).Pen!;
        Assert.Equal((ScreenTargetKind.AllScreens, new RectD(0.25, 0.125, 0.5, 0.75), false, true, 0.5, TabletTurn.Quarter),
            (after.TargetKind, after.ScreenArea, after.Flipped, after.MouseMode, after.MouseSpeed, after.Turn));
        Assert.True(after.Pressure.Curve.IsCloseTo(PressureCurve.Through(0.6, 0.3)));

        // The open applications come back through a script that calls this program on the bus.
        var apps = await desktop.RunningAppsAsync(cancel);
        Assert.Equal([("firefox", "Start page"), ("krita", "Drawing — Krita")], apps.Select(a => (a.Id, a.Title)));
        Assert.Equal(["xperience-window-list"], kwin.Loaded);

        // So does the active application, for as long as it is watched.
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.ActiveAppChanged += app => heard.TrySetResult(app);
        Assert.True(await desktop.WatchActiveAppAsync(true, cancel));
        Assert.Equal("krita", await heard.Task.WaitAsync(TimeSpan.FromSeconds(3), cancel));
        Assert.True(await desktop.WatchActiveAppAsync(false, cancel));
        Assert.Equal("xperience-active-app", kwin.Unloaded[^1]);

        await desktop.ToggleShowDesktopAsync(cancel);
        Assert.True(kwin.ShowingDesktop);
    }
}
