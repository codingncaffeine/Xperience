using System.Text.Json;
using System.Text.Json.Serialization;
using Xperience.Core.Actions;

namespace Xperience.Core.Settings;

/// <summary>What the tablet's controls do, for every application or for one.</summary>
public sealed record Profile
{
    public const int KeyCount = 8, DialCount = 2, DialSlots = 4;

    /// <summary>The name shown on the profile's tab.</summary>
    public string Name { get; init; } = "All applications";

    /// <summary>The application this profile is for, as the desktop names it; empty for the profile every other application gets.</summary>
    public string App { get; init; } = "";

    public IReadOnlyList<PadAction> Keys { get; init; } = DefaultKeys;

    /// <summary>Each dial's functions; a key steps through them.</summary>
    public IReadOnlyList<IReadOnlyList<DialFunction>> Dials { get; init; } = DefaultDials;

    public PadAction PenLower { get; init; } = PadAction.PenButton;
    public PadAction PenUpper { get; init; } = PadAction.PenButton;

    /// <summary>The maker's own assignments for this tablet, top to bottom with the keys on the left.</summary>
    public static IReadOnlyList<PadAction> DefaultKeys { get; } =
    [
        PadAction.Do(SpecialAct.ShowXperience), PadAction.Shortcut("B"), PadAction.Shortcut("Alt"), PadAction.Do(SpecialAct.SwitchDial1),
        PadAction.Do(SpecialAct.SwitchDial2), PadAction.Shortcut("Space"), PadAction.Shortcut("Ctrl+S"), PadAction.Shortcut("Ctrl+Z"),
    ];

    public static IReadOnlyList<IReadOnlyList<DialFunction>> DefaultDials { get; } =
    [
        [DialFunction.Of(DialKind.Zoom), DialFunction.Of(DialKind.Scroll), DialFunction.Of(DialKind.BrushSize), DialFunction.Of(DialKind.Rotate)],
        // The second dial starts on brush size and goes round the other way, as the maker has it.
        [DialFunction.Of(DialKind.BrushSize), DialFunction.Of(DialKind.Scroll), DialFunction.Of(DialKind.Zoom), DialFunction.Of(DialKind.Rotate)],
    ];

    public bool IsDefault => App.Length == 0;

    public PadAction Key(int index) => index >= 0 && index < Keys.Count ? Keys[index] : PadAction.Off;

    public DialFunction Dial(int dial, int slot) =>
        dial >= 0 && dial < Dials.Count && slot >= 0 && slot < Dials[dial].Count ? Dials[dial][slot] : DialFunction.Of(DialKind.Disabled);

    public Profile WithKey(int index, PadAction action) => this with { Keys = [.. Keys.Select((k, i) => i == index ? action : k)] };

    public Profile WithDial(int dial, IReadOnlyList<DialFunction> slots) => this with { Dials = [.. Dials.Select((d, i) => i == dial ? slots : d)] };

    /// <summary>The profile with every list at its full length, whatever the file it was read from held.</summary>
    public Profile Completed() => this with
    {
        Keys = [.. Enumerable.Range(0, KeyCount).Select(i => i < Keys.Count && Keys[i] != null ? Keys[i] : DefaultKeys[i])],
        Dials =
        [
            .. Enumerable.Range(0, DialCount).Select(d => (IReadOnlyList<DialFunction>)
            [
                .. Enumerable.Range(0, DialSlots).Select(s => d < Dials.Count && Dials[d] != null && s < Dials[d].Count && Dials[d][s] != null ? Dials[d][s] : DefaultDials[d][s]),
            ]),
        ],
        PenLower = PenLower ?? PadAction.PenButton,
        PenUpper = PenUpper ?? PadAction.PenButton,
    };
}

/// <summary>The small window of eight buttons that floats over other windows.</summary>
public sealed record FloatingMenuConfig
{
    public const int ButtonCount = 8;

    public bool Vertical { get; init; }

    public IReadOnlyList<PadAction> Buttons { get; init; } = DefaultButtons;

    public static IReadOnlyList<PadAction> DefaultButtons { get; } =
    [
        PadAction.Shortcut("Ctrl+Shift+Z"), PadAction.Shortcut("Ctrl+Z"), PadAction.Shortcut("Ctrl+Shift+N"), PadAction.Shortcut("Ctrl+E"),
        PadAction.Shortcut("]"), PadAction.Shortcut("["), PadAction.Shortcut("Ctrl+S"), PadAction.Do(SpecialAct.ShowXperience),
    ];

    public FloatingMenuConfig Completed() => this with
    {
        Buttons = [.. Enumerable.Range(0, ButtonCount).Select(i => i < Buttons.Count && Buttons[i] != null ? Buttons[i] : DefaultButtons[i])],
    };
}

/// <summary>
/// What the pen's calibration is made of. The desktop holds the result as one matrix; these are the parts it
/// was built from, kept so that changing one part does not lose the others.
/// </summary>
public sealed record PenGeometry
{
    /// <summary>The part of the pen's surface in use, as shares of it with the tablet the right way up.</summary>
    public RectD ActiveArea { get; init; } = RectD.Full;

    /// <summary>The correction found by tapping the calibration crosses: a, b, c, d, e, f of x' = ax + by + c, y' = dx + ey + f.</summary>
    public IReadOnlyList<double> Correction { get; init; } = [1, 0, 0, 0, 1, 0];

    public static readonly PenGeometry Default = new();
}

/// <summary>Everything Xperience keeps itself. The pen's mapping, turn and pressure are not here: the desktop keeps those.</summary>
public sealed record XperienceConfig
{
    public int Version { get; init; } = 1;

    /// <summary>The first profile is the one for every application; the rest are each for one application.</summary>
    public IReadOnlyList<Profile> Profiles { get; init; } = [new Profile()];

    public FloatingMenuConfig FloatingMenu { get; init; } = new();

    /// <summary>Show what a key does on screen when it is pressed.</summary>
    public bool Hints { get; init; } = true;

    /// <summary>Every key and dial on the tablet does nothing.</summary>
    public bool KeysDisabled { get; init; }

    public PenGeometry Geometry { get; init; } = PenGeometry.Default;

    /// <summary>The pressure response to put back when pressure is turned on again, in the desktop's own notation.</summary>
    public string PressureWhenOn { get; init; } = "";

    /// <summary>Closing the window leaves Xperience running, so the keys that need it keep working.</summary>
    public bool KeepRunning { get; init; } = true;

    public static readonly XperienceConfig Default = new();

    public Profile DefaultProfile => Profiles[0];

    /// <summary>The profile for an application, or the one for every application when it has none of its own.</summary>
    public Profile ProfileFor(string app) =>
        app.Length == 0 ? DefaultProfile : Profiles.Skip(1).FirstOrDefault(p => p.App.Equals(app, StringComparison.OrdinalIgnoreCase)) ?? DefaultProfile;

    /// <summary>The profile that is exactly for an application ("" for the one every application gets), or null.</summary>
    public Profile? Find(string app) => Profiles.FirstOrDefault(p => p.App.Equals(app, StringComparison.OrdinalIgnoreCase));

    /// <summary>The settings with one profile changed, found by its application; unchanged when there is no such profile.</summary>
    public XperienceConfig Change(string app, Func<Profile, Profile> change) =>
        this with { Profiles = [.. Profiles.Select(p => p.App.Equals(app, StringComparison.OrdinalIgnoreCase) ? change(p) with { App = p.App } : p)] };

    /// <summary>The settings with every list at its full length and a first profile that is for every application.</summary>
    public XperienceConfig Completed()
    {
        var profiles = (Profiles ?? []).Where(p => p != null).Select(p => p.Completed()).ToList();
        if (profiles.Count == 0)
            profiles.Add(new Profile());
        profiles[0] = profiles[0] with { App = "", Name = "All applications" };
        var geometry = Geometry ?? PenGeometry.Default;
        if (geometry.Correction is not { Count: 6 } || geometry.Correction.Any(v => !double.IsFinite(v)))
            geometry = geometry with { Correction = PenGeometry.Default.Correction };
        return this with
        {
            Profiles = [profiles[0], .. profiles.Skip(1).Where(p => p.App.Length > 0).DistinctBy(p => p.App.ToLowerInvariant())],
            FloatingMenu = (FloatingMenu ?? new()).Completed(),
            Geometry = geometry,
            PressureWhenOn = PressureWhenOn ?? "",
        };
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads settings; null when the text is not settings at all.</summary>
    public static XperienceConfig? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<XperienceConfig>(json, Json)?.Completed();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when both hold the same settings, whatever objects they are made of.</summary>
    public bool SameAs(XperienceConfig other) => ToJson() == other.ToJson();
}

/// <summary>What changes while Xperience runs and should survive a restart, but is not a setting.</summary>
public sealed record RunState
{
    /// <summary>Which function each dial is on, per profile, keyed by the profile's application ("" for all).</summary>
    public Dictionary<string, int[]> DialSlots { get; init; } = [];

    /// <summary>The screen area to put back when precision mode ends; null when it is off.</summary>
    public RectD? AreaBeforePrecision { get; init; }

    /// <summary>Where the floating menu was left, in screen pixels; null before it was ever moved.</summary>
    public double[]? FloatingMenuAt { get; init; }

    public bool FloatingMenuPinned { get; init; }

    /// <summary>True when the tablet's picture is shown bare, without what each key does.</summary>
    public bool LabelsHidden { get; init; }

    /// <summary>
    /// The desktop bindings Xperience wrote, by their place in the desktop's list. A binding that is not in here
    /// was made by the user elsewhere, and is never removed to make room for "no binding".
    /// </summary>
    public HashSet<string> OwnBindings { get; init; } = [];

    public int[] SlotsFor(string app) => DialSlots.TryGetValue(app, out int[]? slots) && slots is { Length: Profile.DialCount } ? slots : [0, 0];
}

/// <summary>Reads and writes the settings file and the state file. Writes replace the file in one step.</summary>
public sealed class ConfigStore
{
    static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly string _configPath, _statePath;

    public ConfigStore(string configFolder, string stateFolder)
    {
        _configPath = Path.Combine(configFolder, "config.json");
        _statePath = Path.Combine(stateFolder, "state.json");
    }

    /// <summary>The store in this account's own folders, which the usual environment variables can move.</summary>
    public static ConfigStore ForThisAccount()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } s ? s : Path.Combine(home, ".local", "state");
        return new ConfigStore(Path.Combine(config, "xperience"), Path.Combine(state, "xperience"));
    }

    public string ConfigPath => _configPath;

    /// <summary>The folder for what the program keeps between runs that is not a setting: its state and its record.</summary>
    public string StateFolder => Path.GetDirectoryName(_statePath) ?? "";

    /// <summary>The saved settings, or the defaults when there are none. A file that cannot be read is set aside, not overwritten.</summary>
    public XperienceConfig Load()
    {
        string? text = Read(_configPath);
        if (text == null)
            return XperienceConfig.Default.Completed();
        if (XperienceConfig.FromJson(text) is { } config)
            return config;
        try
        {
            File.Copy(_configPath, _configPath + ".unreadable", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The copy is a courtesy; the defaults are used either way.
        }
        return XperienceConfig.Default.Completed();
    }

    /// <summary>Saves the settings; false when the file could not be written.</summary>
    public bool Save(XperienceConfig config) => Write(_configPath, config.ToJson());

    public RunState LoadState()
    {
        try
        {
            return Read(_statePath) is { } text ? JsonSerializer.Deserialize<RunState>(text, StateJson) ?? new RunState() : new RunState();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new RunState();
        }
    }

    public bool SaveState(RunState state) => Write(_statePath, JsonSerializer.Serialize(state, StateJson));

    static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool Write(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".new";
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
