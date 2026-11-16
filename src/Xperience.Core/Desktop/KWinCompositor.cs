using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace Xperience.Core.Desktop;

/// <summary>
/// KDE Plasma on Wayland. The pen's settings are KWin's own, reached over the session bus, so they hold whether
/// or not this program is running. The screen layout comes from <c>kscreen-doctor</c>.
/// </summary>
public sealed class KWinCompositor : ICompositor
{
    const string Service = "org.kde.KWin";
    const string ManagerPath = "/org/kde/KWin/InputDevice";
    const string ManagerInterface = "org.kde.KWin.InputDeviceManager";
    const string DeviceInterface = "org.kde.KWin.InputDevice";
    const string Properties = "org.freedesktop.DBus.Properties";

    readonly DBusConnection _connection;

    KWinCompositor(DBusConnection connection) => _connection = connection;

    public string Name => "KDE Plasma";

    public event Action? Changed;

    /// <summary>Connects to KWin; null when this session has no bus or no KWin on it.</summary>
    public static async Task<KWinCompositor?> ConnectAsync()
    {
        if (DBusAddress.Session is not { } address)
            return null;
        // A bus that drops the connection gets a new one on the next call, so one bad moment does not leave the
        // window deaf until it is restarted.
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = true });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var compositor = new KWinCompositor(connection);
            await compositor.DeviceNamesAsync().ConfigureAwait(false);
            foreach (string member in (string[])["deviceAdded", "deviceRemoved"])
            {
                await connection.AddMatchAsync(
                    new MatchRule { Type = MessageType.Signal, Interface = ManagerInterface, Member = member },
                    static (Message m, object? _) => m.GetBodyReader().ReadString(),
                    (Notification<string> n) =>
                    {
                        if (n.HasValue)
                            compositor.Changed?.Invoke();
                    },
                    emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);
            }
            return compositor;
        }
        catch (Exception e) when (IsBusFault(e) || e is IOException or InvalidOperationException)
        {
            connection.Dispose();
            return null;
        }
    }

    public async Task<DesktopSnapshot> ReadAsync(Tablets.TabletModel tablet, CancellationToken cancel = default)
    {
        var screens = KScreenLayout.Parse(await RunAsync("kscreen-doctor", ["-j"], cancel).ConfigureAwait(false));
        PenState? pen = null;
        foreach (string id in await DeviceNamesAsync().ConfigureAwait(false))
        {
            string path = $"{ManagerPath}/{id}";
            try
            {
                if (await GetAsync(path, "vendor", static (ref Reader r) => r.ReadUInt32()).ConfigureAwait(false) != tablet.VendorId ||
                    await GetAsync(path, "product", static (ref Reader r) => r.ReadUInt32()).ConfigureAwait(false) != tablet.ProductId ||
                    !await GetAsync(path, "tabletTool", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false))
                    continue;
                pen = await ReadPenAsync(id, path).ConfigureAwait(false);
                break;
            }
            catch (Exception e) when (IsBusFault(e))
            {
                // The device left between the list and the read.
            }
        }
        return new DesktopSnapshot(screens, pen);
    }

    async Task<PenState> ReadPenAsync(string id, string path)
    {
        string screen = await GetAsync(path, "outputName", static (ref Reader r) => r.ReadString()).ConfigureAwait(false);
        bool everywhere = await GetAsync(path, "mapToWorkspace", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false);
        return new PenState(
            Id: id,
            Name: await GetAsync(path, "name", static (ref Reader r) => r.ReadString()).ConfigureAwait(false),
            TargetKind: everywhere ? ScreenTargetKind.AllScreens : screen.Length > 0 ? ScreenTargetKind.OneScreen : ScreenTargetKind.ActiveScreen,
            TargetScreen: screen,
            ScreenArea: await GetAsync(path, "outputArea", ReadRect).ConfigureAwait(false),
            Flipped: await GetAsync(path, "leftHanded", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false),
            MouseMode: await GetAsync(path, "tabletToolIsRelative", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false),
            Pressure: new PressureSettings(
                Pen.PressureCurve.Parse(await GetAsync(path, "pressureCurve", static (ref Reader r) => r.ReadString()).ConfigureAwait(false)),
                await GetAsync(path, "pressureRangeMin", static (ref Reader r) => r.ReadDouble()).ConfigureAwait(false),
                await GetAsync(path, "pressureRangeMax", static (ref Reader r) => r.ReadDouble()).ConfigureAwait(false)),
            CanFlip: await GetAsync(path, "supportsLeftHanded", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false),
            CanSetScreenArea: await GetAsync(path, "supportsOutputArea", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false),
            CanSetPressureRange: await GetAsync(path, "supportsPressureRange", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false));
    }

    public async Task SetTargetAsync(string penId, ScreenTargetKind kind, string screenName, CancellationToken cancel = default)
    {
        string path = $"{ManagerPath}/{penId}";
        await SetAsync(path, "mapToWorkspace", (ref MessageWriter w) => w.WriteVariantBool(kind == ScreenTargetKind.AllScreens)).ConfigureAwait(false);
        string name = kind == ScreenTargetKind.OneScreen ? screenName : "";
        await SetAsync(path, "outputName", (ref MessageWriter w) => w.WriteVariantString(name)).ConfigureAwait(false);
    }

    public Task SetScreenAreaAsync(string penId, RectD area, CancellationToken cancel = default) =>
        SetAsync($"{ManagerPath}/{penId}", "outputArea", (ref MessageWriter w) =>
        {
            w.WriteSignature("(dddd)");
            w.WriteStructureStart();
            w.WriteDouble(area.X);
            w.WriteDouble(area.Y);
            w.WriteDouble(area.Width);
            w.WriteDouble(area.Height);
        });

    public Task SetFlippedAsync(string penId, bool flipped, CancellationToken cancel = default) =>
        SetAsync($"{ManagerPath}/{penId}", "leftHanded", (ref MessageWriter w) => w.WriteVariantBool(flipped));

    public Task SetMouseModeAsync(string penId, bool on, CancellationToken cancel = default) =>
        SetAsync($"{ManagerPath}/{penId}", "tabletToolIsRelative", (ref MessageWriter w) => w.WriteVariantBool(on));

    public async Task SetPressureAsync(string penId, PressureSettings pressure, CancellationToken cancel = default)
    {
        string path = $"{ManagerPath}/{penId}";
        await SetAsync(path, "pressureCurve", (ref MessageWriter w) => w.WriteVariantString(pressure.Curve.ToDesktopString())).ConfigureAwait(false);
        await SetAsync(path, "pressureRangeMin", (ref MessageWriter w) => w.WriteVariantDouble(pressure.RangeMin)).ConfigureAwait(false);
        await SetAsync(path, "pressureRangeMax", (ref MessageWriter w) => w.WriteVariantDouble(pressure.RangeMax)).ConfigureAwait(false);
    }

    public async Task IdentifyScreensAsync(CancellationToken cancel = default)
    {
        try
        {
            await _connection.CallMethodAsync(Call("/org/kde/KWin/Effect/OutputLocator1", "org.kde.KWin.Effect.OutputLocator1", "show")).ConfigureAwait(false);
        }
        catch (Exception e) when (IsBusFault(e))
        {
            // The effect is not loaded on this desktop; there is nothing else to ask.
        }
    }

    MessageBuffer Call(string path, string @interface, string member)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: member);
        return writer.CreateMessage();
    }

    Task<string[]> DeviceNamesAsync()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ManagerPath, @interface: Properties, member: "Get", signature: "ss");
        writer.WriteString(ManagerInterface);
        writer.WriteString("devicesSysNames");
        return _connection.CallMethodAsync(writer.CreateMessage(), static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadSignature();
            return reader.ReadArrayOfString();
        }, null);
    }

    delegate T ValueReader<T>(ref Reader reader);

    Task<T> GetAsync<T>(string path, string property, ValueReader<T> read)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: path, @interface: Properties, member: "Get", signature: "ss");
        writer.WriteString(DeviceInterface);
        writer.WriteString(property);
        return _connection.CallMethodAsync(writer.CreateMessage(), static (Message m, object? state) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadSignature();
            return ((ValueReader<T>)state!)(ref reader);
        }, read);
    }

    /// <summary>Writes one variant. The writer is a struct that moves as it writes, so it comes by reference.</summary>
    delegate void VariantWriter(ref MessageWriter writer);

    Task SetAsync(string path, string property, VariantWriter writeVariant)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Service, path: path, @interface: Properties, member: "Set", signature: "ssv");
            writer.WriteString(DeviceInterface);
            writer.WriteString(property);
            writeVariant(ref writer);
            return _connection.CallMethodAsync(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>An error reply, a lost connection or a reply of the wrong shape: the bus could not do what was asked.</summary>
    public static bool IsBusFault(Exception e) => e is DBusErrorReplyException or DBusConnectionException or DBusMessageException;

    static RectD ReadRect(ref Reader reader)
    {
        reader.AlignStruct();
        return new RectD(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }

    /// <summary>Runs a short desktop tool and returns what it printed; empty when it is missing or fails.</summary>
    static async Task<string> RunAsync(string program, string[] arguments, CancellationToken cancel)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null)
                return "";
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            _ = process.StandardError.ReadToEndAsync(limit.Token);
            string output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : "";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or OperationCanceledException or InvalidOperationException)
        {
            return "";
        }
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
