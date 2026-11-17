using System.Diagnostics;

namespace Xperience.Core.Tablets;

/// <summary>What a pen button is to the driver itself, before the desktop sees anything of it.</summary>
public enum PenButtonRole
{
    /// <summary>A button, handed on for the desktop and Xperience to bind.</summary>
    Button,

    /// <summary>The pen is its eraser while the button is held.</summary>
    Eraser,

    /// <summary>Each press switches between pen and eraser, and it stays switched.</summary>
    Toggle,
}

/// <summary>The choices the driver takes when it is loaded.</summary>
/// <param name="Upper">What the pen's upper button is. Out of the box it erases while held.</param>
/// <param name="Lower">What the pen's lower button is. Out of the box it is a button.</param>
/// <param name="TiltOff">The pen reports no tilt.</param>
public sealed record DriverOptions(PenButtonRole Upper, PenButtonRole Lower, bool TiltOff)
{
    public static readonly DriverOptions Default = new(PenButtonRole.Eraser, PenButtonRole.Button, false);

    /// <summary>The tablet's keys that switch between pen and eraser: its own numbers, 1 to 8, as digits; empty for none.</summary>
    public string SwitchKeys { get; init; } = "";

    /// <summary>Digits 1 to 8, each once, in rising order: what the driver and its helper take for the switch keys.</summary>
    public static string Keys(string? text) => string.Concat((text ?? "").Where(c => c is >= '1' and <= '8').Distinct().Order());

    /// <summary>The word the driver knows a role by.</summary>
    public static string Word(PenButtonRole role) => role switch
    {
        PenButtonRole.Eraser => "eraser",
        PenButtonRole.Toggle => "toggle",
        _ => "button",
    };

    /// <summary>The role a word stands for; a word the driver does not know is what the button is out of the box.</summary>
    public static PenButtonRole Role(string? word, PenButtonRole outOfTheBox) => word switch
    {
        "button" => PenButtonRole.Button,
        "eraser" => PenButtonRole.Eraser,
        "toggle" => PenButtonRole.Toggle,
        _ => outOfTheBox,
    };
}

/// <summary>What is installed of the driver on this machine.</summary>
/// <param name="LoaderInstalled">The system's loader for such drivers, udev-hid-bpf, is installed.</param>
/// <param name="Installed">The driver and its rules are in place.</param>
/// <param name="CanInstall">This build carries the driver and the helper that installs it.</param>
/// <param name="Options">The choices the installed driver is loaded with.</param>
public sealed record DriverStatus(bool LoaderInstalled, bool Installed, bool CanInstall, DriverOptions Options);

/// <summary>
/// Installs, updates and removes the tablet driver. The work needs administrator rights, so it is done by a
/// small helper started through the system's own prompt for them.
/// </summary>
public sealed class DriverSetup
{
    public const string ObjectName = "0010-XPPen__Artist16_3rd.bpf.o";
    public const string LoaderPackage = "udev-hid-bpf";

    readonly string _root, _objectPath, _helperPath;
    readonly Func<IReadOnlyList<string>, Task<(int Code, string Output)>> _elevate;

    /// <param name="root">The folder the system lives in: "/" outside tests.</param>
    /// <param name="objectPath">The driver object this build carries.</param>
    /// <param name="helperPath">The helper that installs it.</param>
    /// <param name="elevate">Runs a command line as administrator; the default asks through pkexec.</param>
    public DriverSetup(string root, string objectPath, string helperPath, Func<IReadOnlyList<string>, Task<(int Code, string Output)>>? elevate = null)
    {
        _root = root;
        _objectPath = objectPath;
        _helperPath = helperPath;
        _elevate = elevate ?? RunElevatedAsync;
    }

    /// <summary>The setup for a build laid out like ours: the helper beside the program, the driver object one folder up.</summary>
    public static DriverSetup ForThisBuild()
    {
        string folder = AppContext.BaseDirectory;
        string beside = Path.Combine(folder, ObjectName);
        return new DriverSetup("/", File.Exists(beside) ? beside : Path.GetFullPath(Path.Combine(folder, "..", ObjectName)), Path.Combine(folder, "xperience-driver-helper"));
    }

    string RulesFile => Path.Combine(_root, "etc/udev/rules.d/70-xperience.rules");

    public DriverStatus Read()
    {
        bool loader = ((string[])["usr/bin", "usr/local/bin", "bin"]).Any(d => File.Exists(Path.Combine(_root, d, "udev-hid-bpf")));
        bool installed = File.Exists(RulesFile) &&
            ((string[])["etc/udev-hid-bpf", "usr/lib/firmware/hid/bpf", "usr/local/lib/firmware/hid/bpf"]).Any(d => File.Exists(Path.Combine(_root, d, ObjectName)));
        return new DriverStatus(loader, installed, File.Exists(_objectPath) && File.Exists(_helperPath), ReadOptions());
    }

    /// <summary>The options as the installed rules have them: the rules file is where they live.</summary>
    DriverOptions ReadOptions()
    {
        try
        {
            string rules = File.Exists(RulesFile) ? File.ReadAllText(RulesFile) : "";
            return new DriverOptions(
                DriverOptions.Role(Property(rules, "XPERIENCE_PEN_UPPER_BUTTON"), DriverOptions.Default.Upper),
                DriverOptions.Role(Property(rules, "XPERIENCE_PEN_LOWER_BUTTON"), DriverOptions.Default.Lower),
                Property(rules, "XPERIENCE_PEN_TILT") == "off")
            {
                SwitchKeys = DriverOptions.Keys(Property(rules, "XPERIENCE_PEN_SWITCH_KEYS")),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DriverOptions.Default;
        }
    }

    /// <summary>The value the rules give a property, or null when they do not set it.</summary>
    static string? Property(string rules, string name)
    {
        string lead = $"ENV{{{name}}}=\"";
        int at = rules.IndexOf(lead, StringComparison.Ordinal);
        if (at < 0)
            return null;
        int end = rules.IndexOf('"', at + lead.Length);
        return end < 0 ? null : rules[(at + lead.Length)..end];
    }

    /// <summary>Installs or updates the driver with these options and loads it. Asks for administrator rights.</summary>
    public async Task<(bool Ok, string Message)> InstallAsync(DriverOptions options)
    {
        var status = Read();
        if (!status.CanInstall)
            return (false, "This build does not carry the driver.");
        if (!status.LoaderInstalled)
            return (false, $"The system's driver loader is missing. Install the package {LoaderPackage} first.");
        return Outcome(await _elevate([_helperPath, "install", _objectPath, DriverOptions.Word(options.Upper), options.TiltOff ? "off" : "on", DriverOptions.Word(options.Lower),
            DriverOptions.Keys(options.SwitchKeys) is { Length: > 0 } keys ? keys : "none"]).ConfigureAwait(false));
    }

    /// <summary>Unloads the driver and takes its files away again. Asks for administrator rights.</summary>
    public async Task<(bool Ok, string Message)> RemoveAsync() =>
        File.Exists(_helperPath) ? Outcome(await _elevate([_helperPath, "remove"]).ConfigureAwait(false)) : (false, "This build does not carry the driver's helper.");

    static (bool, string) Outcome((int Code, string Output) result) => result.Code switch
    {
        0 => (true, ""),
        126 or 127 => (false, "Administrator rights were not given, so nothing was changed."),
        _ => (false, result.Output.Trim().Length > 0 ? result.Output.Trim() : $"The helper failed (exit code {result.Code})."),
    };

    static async Task<(int Code, string Output)> RunElevatedAsync(IReadOnlyList<string> command)
    {
        try
        {
            var start = new ProcessStartInfo("pkexec") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string part in command)
                start.ArgumentList.Add(part);
            using var process = Process.Start(start);
            if (process == null)
                return (-1, "pkexec could not be started.");
            var error = process.StandardError.ReadToEndAsync();
            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return (process.ExitCode, output + await error.ConfigureAwait(false));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, "The system's prompt for administrator rights (pkexec) is not available: " + e.Message);
        }
    }
}
