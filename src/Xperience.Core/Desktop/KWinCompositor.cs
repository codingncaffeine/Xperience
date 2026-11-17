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

    const string CallbackPath = "/io/github/codingncaffeine/Xperience";
    const string CallbackInterface = "io.github.codingncaffeine.Xperience";
    const string WatchScript = "xperience-active-app", ListScript = "xperience-window-list";

    readonly DBusConnection _connection;
    readonly Lock _gate = new();
    DBusConnection? _callbacks;
    bool _watching;
    TaskCompletionSource<string>? _windowList;

    KWinCompositor(DBusConnection connection) => _connection = connection;

    public string Name => "KDE Plasma";

    public event Action? Changed;
    public event Action<string>? ActiveAppChanged;

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
        string? pad = null;
        foreach (string id in await DeviceNamesAsync().ConfigureAwait(false))
        {
            string path = $"{ManagerPath}/{id}";
            try
            {
                if (await GetAsync(path, "vendor", static (ref Reader r) => r.ReadUInt32()).ConfigureAwait(false) != tablet.VendorId ||
                    await GetAsync(path, "product", static (ref Reader r) => r.ReadUInt32()).ConfigureAwait(false) != tablet.ProductId)
                    continue;
                if (pen == null && await GetAsync(path, "tabletTool", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false))
                    pen = await ReadPenAsync(id, path).ConfigureAwait(false);
                else if (pad == null && await GetAsync(path, "tabletPad", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false))
                    pad = await GetAsync(path, "name", static (ref Reader r) => r.ReadString()).ConfigureAwait(false);
            }
            catch (Exception e) when (IsBusFault(e))
            {
                // The device left between the list and the read.
            }
        }
        return new DesktopSnapshot(screens, pen) { PadName = pad };
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
            CanSetPressureRange: await GetAsync(path, "supportsPressureRange", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false))
        {
            Matrix = PenMatrix.Parse(await GetAsync(path, "calibrationMatrix", static (ref Reader r) => r.ReadString()).ConfigureAwait(false)),
            CanSetMatrix = await GetAsync(path, "supportsCalibrationMatrix", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false),
            MouseSpeed = await GetAsync(path, "pointerAcceleration", static (ref Reader r) => r.ReadDouble()).ConfigureAwait(false),
            CanSetMouseSpeed = await GetAsync(path, "supportsPointerAcceleration", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false),
        };
    }

    public Task SetMatrixAsync(string penId, PenMatrix matrix, CancellationToken cancel = default) =>
        SetAsync($"{ManagerPath}/{penId}", "calibrationMatrix", (ref MessageWriter w) => w.WriteVariantString(matrix.ToDesktopString()));

    public Task SetMouseSpeedAsync(string penId, double speed, CancellationToken cancel = default) =>
        SetAsync($"{ManagerPath}/{penId}", "pointerAcceleration", (ref MessageWriter w) => w.WriteVariantDouble(Math.Clamp(speed, -1, 1)));

    /// <summary>The file the desktop keeps its input settings in.</summary>
    static string InputSettingsFile
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
            return Path.Combine(config, "kcminputrc");
        }
    }

    public Task<IReadOnlyList<Rebind>> ReadRebindsAsync(CancellationToken cancel = default) => ReadRebindsAsync(InputSettingsFile, cancel);

    internal static async Task<IReadOnlyList<Rebind>> ReadRebindsAsync(string file, CancellationToken cancel)
    {
        try
        {
            return File.Exists(file) ? RebindFile.Parse(await File.ReadAllTextAsync(file, cancel).ConfigureAwait(false)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public Task WriteRebindsAsync(IReadOnlyList<Rebind> changes, CancellationToken cancel = default) => WriteRebindsWithToolAsync(changes, null, notify: true, cancel);

    /// <summary>
    /// Writes through the desktop's own settings tool, which merges with what is in the file and, on the last
    /// change, tells the running desktop to take the bindings up.
    /// </summary>
    /// <param name="changes">The bindings to set or, with an empty value, remove.</param>
    /// <param name="configHome">The settings folder to write in instead of the account's own; tests use one of theirs.</param>
    /// <param name="notify">Whether the running desktop is told.</param>
    /// <param name="cancel">Stops the work.</param>
    internal static async Task WriteRebindsWithToolAsync(IReadOnlyList<Rebind> changes, string? configHome, bool notify, CancellationToken cancel)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            List<string> arguments = ["--file", "kcminputrc", "--group", "ButtonRebinds", "--group", change.Group, "--group", change.Device, "--key", change.Key];
            if (notify && i == changes.Count - 1)
                arguments.Add("--notify");
            if (change.Value.Length == 0)
                arguments.Add("--delete");
            else
            {
                // The marker keeps a value that starts with a dash from being read as an option.
                arguments.Add("--");
                arguments.Add(change.Value);
            }
            var (code, _, error) = await RunFullAsync("kwriteconfig6", arguments, cancel, configHome).ConfigureAwait(false);
            if (code != 0)
                throw new IOException($"kwriteconfig6 could not save the binding for {change.Id}: {error.Trim()}");
        }
    }

    public async Task ShowHintAsync(string text, CancellationToken cancel = default)
    {
        try
        {
            await _connection.CallMethodAsync(Call("org.kde.plasmashell", "/org/kde/osdService", "org.kde.osdService", "showText", "ss", (ref MessageWriter w) =>
            {
                w.WriteString("input-tablet");
                w.WriteString(text);
            })).ConfigureAwait(false);
        }
        catch (Exception e) when (IsBusFault(e))
        {
            // No desktop shell to show it; a hint is never worth an error.
        }
    }

    public Task OpenDisplaySettingsAsync(CancellationToken cancel = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("kcmshell6") { ArgumentList = { "kcm_kscreen" }, UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No such settings program on this desktop; there is nothing to open.
        }
        return Task.CompletedTask;
    }

    public async Task ToggleShowDesktopAsync(CancellationToken cancel = default)
    {
        bool showing = await GetAsync("/KWin", "org.kde.KWin", "showingDesktop", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false);
        await _connection.CallMethodAsync(Call(Service, "/KWin", "org.kde.KWin", "showDesktop", "b", (ref MessageWriter w) => w.WriteBool(!showing))).ConfigureAwait(false);
    }

    public async Task ToggleScreenKeyboardAsync(CancellationToken cancel = default)
    {
        const string keyboard = "org.kde.kwin.VirtualKeyboard";
        bool active = await GetAsync("/VirtualKeyboard", keyboard, "active", static (ref Reader r) => r.ReadBool()).ConfigureAwait(false);
        await SetAsync("/VirtualKeyboard", keyboard, "active", (ref MessageWriter w) => w.WriteVariantBool(!active)).ConfigureAwait(false);
        if (!active)
            await _connection.CallMethodAsync(Call("/VirtualKeyboard", keyboard, "forceActivate")).ConfigureAwait(false);
    }

    /// <summary>Takes the calls the desktop's scripts make back to this program.</summary>
    sealed class Callback(KWinCompositor owner) : IPathMethodHandler
    {
        public string Path => CallbackPath;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.InterfaceAsString != CallbackInterface)
                return default;
            var reader = request.GetBodyReader();
            switch (request.MemberAsString)
            {
                case "ActiveApp":
                    owner.ActiveAppChanged?.Invoke(reader.ReadString());
                    break;
                case "Windows":
                    string list = reader.ReadString();
                    lock (owner._gate)
                        owner._windowList?.TrySetResult(list);
                    break;
                default:
                    return default;
            }
            if (!context.NoReplyExpected)
            {
                using var writer = context.CreateReplyWriter(null);
                context.Reply(writer.CreateMessage());
            }
            return default;
        }
    }

    /// <summary>
    /// The connection the desktop's scripts call back on. It is a second one, made the plain way: the library
    /// takes incoming calls only on a connection that does not reconnect by itself, because a reconnected one
    /// has a new name on the bus that a loaded script would not know.
    /// </summary>
    async Task<DBusConnection> CallbacksAsync()
    {
        if (_callbacks is { } ready)
            return ready;
        var connection = new DBusConnection(DBusAddress.Session ?? throw new IOException("This session has no bus."));
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            connection.AddMethodHandler(new Callback(this));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        lock (_gate)
        {
            if (_callbacks == null)
                return _callbacks = connection;
        }
        connection.Dispose();
        return _callbacks;
    }

    /// <summary>A script's opening lines: how to call this program back.</summary>
    static string ScriptHead(DBusConnection callbacks) => $$"""
        const reply = (method, text) => callDBus("{{callbacks.UniqueName}}", "{{CallbackPath}}", "{{CallbackInterface}}", method, text);

        """;

    /// <summary>Lets go of the callback connection after a failure, so that the next request makes a fresh one.</summary>
    void DropCallbacks()
    {
        DBusConnection? old;
        lock (_gate)
        {
            old = _callbacks;
            _callbacks = null;
        }
        old?.Dispose();
    }

    async Task<bool> LoadScriptAsync(string name, string body)
    {
        string folder = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : Path.GetTempPath(), "xperience");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name + ".js");
        await File.WriteAllTextAsync(file, ScriptHead(await CallbacksAsync().ConfigureAwait(false)) + body).ConfigureAwait(false);
        await UnloadScriptAsync(name).ConfigureAwait(false);

        var load = Call(Service, "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss", (ref MessageWriter w) =>
        {
            w.WriteString(file);
            w.WriteString(name);
        });
        int id = await _connection.CallMethodAsync(load, static (Message m, object? _) => m.GetBodyReader().ReadInt32(), null).ConfigureAwait(false);
        if (id < 0)
            return false;
        await _connection.CallMethodAsync(Call(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/Scripting/Script{id}"), "org.kde.kwin.Script", "run")).ConfigureAwait(false);
        return true;
    }

    async Task UnloadScriptAsync(string name)
    {
        await _connection.CallMethodAsync(Call(Service, "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s", (ref MessageWriter w) => w.WriteString(name))).ConfigureAwait(false);
    }

    public async Task<bool> WatchActiveAppAsync(bool on, CancellationToken cancel = default)
    {
        try
        {
            if (!on)
            {
                if (_watching)
                    await UnloadScriptAsync(WatchScript).ConfigureAwait(false);
                _watching = false;
                return true;
            }
            _watching = await LoadScriptAsync(WatchScript, """
                const tell = (w) => reply("ActiveApp", w ? String(w.resourceClass) : "");
                workspace.windowActivated.connect(tell);
                tell(workspace.activeWindow);
                """).ConfigureAwait(false);
            return _watching;
        }
        catch (Exception e) when (IsBusFault(e) || e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DropCallbacks();
            return false;
        }
    }

    public async Task<IReadOnlyList<AppInfo>> RunningAppsAsync(CancellationToken cancel = default)
    {
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _windowList = answer;
        try
        {
            if (!await LoadScriptAsync(ListScript, """
                const lines = [];
                for (const w of workspace.windowList()) {
                    if (w.normalWindow && w.resourceClass)
                        lines.push(String(w.resourceClass) + "\t" + String(w.caption));
                }
                reply("Windows", lines.join("\n"));
                """).ConfigureAwait(false))
                return [];
            string list = await answer.Task.WaitAsync(TimeSpan.FromSeconds(3), cancel).ConfigureAwait(false);
            await UnloadScriptAsync(ListScript).ConfigureAwait(false);
            return ParseWindowList(list);
        }
        catch (Exception e) when (IsBusFault(e) || e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            DropCallbacks();
            return [];
        }
        finally
        {
            lock (_gate)
                _windowList = null;
        }
    }

    /// <summary>One application per line, its name and a window title with a tab between; one entry per application.</summary>
    internal static IReadOnlyList<AppInfo> ParseWindowList(string list) =>
    [
        .. list.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts[0].Length > 0)
            .Select(parts => new AppInfo(parts[0], parts.Length > 1 ? parts[1] : ""))
            .DistinctBy(app => app.Id.ToLowerInvariant())
            .OrderBy(app => app.Id, StringComparer.OrdinalIgnoreCase),
    ];

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

    /// <summary>A method call with arguments, built in one go because the writer cannot wait.</summary>
    MessageBuffer Call(string destination, string path, string @interface, string member, string signature, VariantWriter body)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: destination, path: path, @interface: @interface, member: member, signature: signature);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
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

    Task<T> GetAsync<T>(string path, string property, ValueReader<T> read) => GetAsync(path, DeviceInterface, property, read);

    Task<T> GetAsync<T>(string path, string @interface, string property, ValueReader<T> read)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: path, @interface: Properties, member: "Get", signature: "ss");
        writer.WriteString(@interface);
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

    Task SetAsync(string path, string property, VariantWriter writeVariant) => SetAsync(path, DeviceInterface, property, writeVariant);

    Task SetAsync(string path, string @interface, string property, VariantWriter writeVariant)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Service, path: path, @interface: Properties, member: "Set", signature: "ssv");
            writer.WriteString(@interface);
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
        var (code, output, _) = await RunFullAsync(program, arguments, cancel).ConfigureAwait(false);
        return code == 0 ? output : "";
    }

    /// <summary>Runs a short tool to its end. The exit code is -1 when it could not be started or took too long.</summary>
    internal static async Task<(int Code, string Output, string Error)> RunFullAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancel, string? configHome = null)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (configHome != null)
                start.Environment["XDG_CONFIG_HOME"] = configHome;
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null)
                return (-1, "", "could not be started");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            string output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return (process.ExitCode, output, await error.ConfigureAwait(false));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or OperationCanceledException or InvalidOperationException)
        {
            return (-1, "", e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A script left loaded would keep calling a program that is gone.
        try
        {
            if (_watching)
                await UnloadScriptAsync(WatchScript).ConfigureAwait(false);
        }
        catch (Exception e) when (IsBusFault(e))
        {
            // The desktop went away first.
        }
        DropCallbacks();
        _connection.Dispose();
    }
}
