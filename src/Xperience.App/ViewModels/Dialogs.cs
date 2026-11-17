using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;

namespace Xperience.App.ViewModels;

/// <summary>
/// What the assignment dialog is asked to edit. A pen button can also stay a plain pen button; a floating-menu
/// button has no mouse actions and is always carried out by Xperience.
/// </summary>
public sealed record ActionRequest(string Title, PadAction Current, PadAction Default, bool ForPenButton = false, bool ForMenu = false);

/// <summary>What the area dialog is asked to edit: a rectangle on a surface, shown in the surface's own units.</summary>
public sealed record AreaRequest(string Title, string Hint, RectD Area, double UnitsWide, double UnitsHigh, string Unit, int Decimals,
    IReadOnlyList<RectD>? Parts = null, bool DarkSurface = false);

/// <summary>Where the calibration crosses go and what the pen is set to, so the result can be worked out.</summary>
public sealed record CalibrationRequest(ScreenInfo Screen, RectD ScreenArea, bool Flipped);

/// <summary>The questions the pages ask through windows of their own. Tests answer them without windows.</summary>
public interface IDialogs
{
    Task<PadAction?> EditActionAsync(ActionRequest request);
    Task<(IReadOnlyList<DialFunction> Slots, int Current)?> EditDialAsync(string title, IReadOnlyList<DialFunction> slots, int current, IReadOnlyList<DialFunction> defaults);
    Task<RectD?> EditAreaAsync(AreaRequest request);
    Task<bool> ConfirmAsync(string title, string text, string yes);
    Task<AppInfo?> PickAppAsync(IReadOnlyList<AppInfo> running);
    Task<string?> PickFileToOpenAsync(string title, string pattern, string kind);
    Task<string?> PickFileToSaveAsync(string title, string suggestedName, string pattern, string kind);

    /// <summary>Runs the tap-the-crosses screen; returns the measured and the wanted points, or null when it was left early.</summary>
    Task<CalibrationResult?> CalibrateAsync(CalibrationRequest request);
}

/// <summary>What tapping the crosses gave: where the pen said it was, and where each cross is, both as shares.</summary>
public sealed record CalibrationResult(IReadOnlyList<(double X, double Y)> Measured, IReadOnlyList<(double X, double Y)> Wanted);

/// <summary>One line of the "Other" list.</summary>
public sealed record OtherChoice(string Label, ActionKind Kind, SpecialAct Special = default)
{
    public override string ToString() => Label;
}

/// <summary>The assignment dialog: one of four kinds of action, with a name of the user's own if wanted.</summary>
public sealed partial class ActionDialogViewModel : ObservableObject
{
    readonly ActionRequest _request;
    bool _loading;

    [ObservableProperty] private bool _isShortcut;
    [ObservableProperty] private bool _isMouse;
    [ObservableProperty] private bool _isRun;
    [ObservableProperty] private bool _isOther;
    [ObservableProperty] private string _keys = "";
    [ObservableProperty] private bool _addSuper;
    [ObservableProperty] private bool _shift;
    [ObservableProperty] private bool _ctrl;
    [ObservableProperty] private bool _leftAlt;
    [ObservableProperty] private bool _rightAlt;
    [ObservableProperty] private bool _space;
    [ObservableProperty] private int _mouseIndex;
    [ObservableProperty] private string _app = "";
    [ObservableProperty] private OtherChoice? _other;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _note = "";

    public ActionDialogViewModel(ActionRequest request)
    {
        _request = request;
        var others = new List<OtherChoice>();
        if (request.ForPenButton)
            others.Add(new OtherChoice("Pen button, as each application uses it", ActionKind.Default));
        foreach (var act in (SpecialAct[])[SpecialAct.ShowXperience, SpecialAct.SwitchMonitor, SpecialAct.PrecisionMode, SpecialAct.BrushEraser,
            SpecialAct.SwitchDial1, SpecialAct.SwitchDial2, SpecialAct.FloatingMenu, SpecialAct.ShowDesktop, SpecialAct.ScreenKeyboard])
            others.Add(new OtherChoice(PadAction.SpecialName(act), ActionKind.Special, act));
        others.Add(new OtherChoice("Disable", ActionKind.Disabled));
        Others = others;
        Load(request.Current);
    }

    public string Title => _request.Title;
    public bool ShowMouse => !_request.ForMenu;
    public IReadOnlyList<OtherChoice> Others { get; }
    public IReadOnlyList<string> MouseActions { get; } = [.. Enum.GetValues<MouseAct>().Select(PadAction.MouseName)];

    public void Load(PadAction action)
    {
        _loading = true;
        try
        {
            IsShortcut = action.Kind == ActionKind.Shortcut;
            IsMouse = action.Kind == ActionKind.Mouse && ShowMouse;
            IsRun = action.Kind == ActionKind.RunApp;
            IsOther = !IsShortcut && !IsMouse && !IsRun;
            Keys = action.Kind == ActionKind.Shortcut ? action.Keys : "";
            AddSuper = false;
            Shift = action.MouseMods.HasFlag(MouseMods.Shift);
            Ctrl = action.MouseMods.HasFlag(MouseMods.Ctrl);
            LeftAlt = action.MouseMods.HasFlag(MouseMods.LeftAlt);
            RightAlt = action.MouseMods.HasFlag(MouseMods.RightAlt);
            Space = action.MouseMods.HasFlag(MouseMods.Space);
            MouseIndex = (int)action.Mouse;
            App = action.App;
            Other = Others.FirstOrDefault(o => o.Kind == action.Kind && (o.Kind != ActionKind.Special || o.Special == action.Special))
                ?? Others.First(o => o.Kind == ActionKind.Disabled);
            Name = action.Name;
        }
        finally
        {
            _loading = false;
        }
        Explain();
    }

    /// <summary>The action as the dialog stands.</summary>
    public PadAction Build()
    {
        PadAction action;
        if (IsShortcut)
            action = KeyCombo.ParseAll(Keys).Count > 0 ? PadAction.Shortcut(Keys) : PadAction.Off;
        else if (IsMouse)
            action = PadAction.Click((MouseAct)Math.Clamp(MouseIndex, 0, MouseActions.Count - 1),
                (Shift ? MouseMods.Shift : 0) | (Ctrl ? MouseMods.Ctrl : 0) | (LeftAlt ? MouseMods.LeftAlt : 0) | (RightAlt ? MouseMods.RightAlt : 0) | (Space ? MouseMods.Space : 0));
        else if (IsRun)
            action = App.Trim().Length > 0 ? PadAction.Run(App.Trim()) : PadAction.Off;
        else
            action = Other switch
            {
                { Kind: ActionKind.Default } => PadAction.PenButton,
                { Kind: ActionKind.Special } special => PadAction.Do(special.Special),
                _ => PadAction.Off,
            };
        return action with { Name = Name.Trim() };
    }

    [RelayCommand]
    void UseDefault() => Load(_request.Default);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName != nameof(Note))
            Explain();
    }

    void Explain()
    {
        var action = Build();
        Note = _request.ForMenu ? ""
            : action.Kind == ActionKind.Default ? "The button stays a plain pen button, and each application decides what it does."
            : action.Kind == ActionKind.Disabled ? "The control does nothing."
            : Assignments.NeedsXperience(action) ? "Xperience carries this out itself, so it has to be running."
            : "The desktop carries this out by itself, whether or not Xperience is running.";
    }
}

/// <summary>One of a dial's four functions in the dial dialog.</summary>
public sealed partial class DialSlotViewModel : ObservableObject
{
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private string _counterclockwise = "";
    [ObservableProperty] private string _clockwise = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _inUse;

    public DialSlotViewModel(int number, DialFunction function, bool inUse)
    {
        Number = number;
        Load(function);
        InUse = inUse;
    }

    public int Number { get; }
    public string Heading => string.Create(CultureInfo.InvariantCulture, $"Function {Number}");
    public bool IsCustom => (DialKind)KindIndex == DialKind.Custom;

    partial void OnKindIndexChanged(int value) => OnPropertyChanged(nameof(IsCustom));

    public void Load(DialFunction function)
    {
        KindIndex = (int)function.Kind;
        Counterclockwise = function.Counterclockwise;
        Clockwise = function.Clockwise;
        Name = function.Name;
    }

    public DialFunction Build()
    {
        var kind = (DialKind)Math.Clamp(KindIndex, 0, (int)DialKind.Disabled);
        return kind == DialKind.Custom
            ? new DialFunction { Kind = kind, Counterclockwise = Counterclockwise, Clockwise = Clockwise, Name = Name.Trim() }
            : new DialFunction { Kind = kind, Name = Name.Trim() };
    }
}

/// <summary>The dial dialog: four functions, one of them in use; a key steps through them.</summary>
public sealed partial class DialDialogViewModel : ObservableObject
{
    readonly IReadOnlyList<DialFunction> _defaults;

    public DialDialogViewModel(string title, IReadOnlyList<DialFunction> slots, int current, IReadOnlyList<DialFunction> defaults)
    {
        Title = title;
        _defaults = defaults;
        for (int i = 0; i < slots.Count; i++)
            Slots.Add(new DialSlotViewModel(i + 1, slots[i], i == current));
    }

    public string Title { get; }
    public ObservableCollection<DialSlotViewModel> Slots { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["Zoom", "Scroll", "Brush size", "Rotate", "Custom shortcuts", "Disabled"];

    public (IReadOnlyList<DialFunction> Slots, int Current) Build()
    {
        var functions = Slots.Select(s => s.Build()).ToList();
        int current = Math.Max(0, Slots.ToList().FindIndex(s => s.InUse));
        return (functions, current);
    }

    [RelayCommand]
    void UseDefault()
    {
        for (int i = 0; i < Slots.Count && i < _defaults.Count; i++)
        {
            Slots[i].Load(_defaults[i]);
            Slots[i].InUse = i == 0;
        }
    }
}

/// <summary>The area dialog: the rectangle as a picture and as four numbers in the surface's units.</summary>
public sealed partial class AreaDialogViewModel : ObservableObject
{
    readonly AreaRequest _request;
    bool _syncing;

    [ObservableProperty] private RectD _area;
    [ObservableProperty] private decimal? _x;
    [ObservableProperty] private decimal? _y;
    [ObservableProperty] private decimal? _width;
    [ObservableProperty] private decimal? _height;

    public AreaDialogViewModel(AreaRequest request)
    {
        _request = request;
        Area = request.Area;
    }

    public string Title => _request.Title;
    public string Hint => _request.Hint;
    public string Unit => _request.Unit;
    public double SurfaceAspect => _request.UnitsHigh > 0 ? _request.UnitsWide / _request.UnitsHigh : 1;
    public IReadOnlyList<RectD>? Parts => _request.Parts;
    public bool DarkSurface => _request.DarkSurface;
    public decimal MaxWidth => (decimal)_request.UnitsWide;
    public decimal MaxHeight => (decimal)_request.UnitsHigh;
    public string Format => _request.Decimals > 0 ? "F" + _request.Decimals.ToString(CultureInfo.InvariantCulture) : "F0";
    public decimal Step => _request.Decimals > 0 ? 0.5m : 10m;

    partial void OnAreaChanged(RectD value)
    {
        if (!_syncing)
            ShowNumbers();
    }

    partial void OnXChanged(decimal? value) => TakeNumbers();
    partial void OnYChanged(decimal? value) => TakeNumbers();
    partial void OnWidthChanged(decimal? value) => TakeNumbers();
    partial void OnHeightChanged(decimal? value) => TakeNumbers();

    void ShowNumbers()
    {
        _syncing = true;
        try
        {
            X = Round(Area.X * _request.UnitsWide);
            Y = Round(Area.Y * _request.UnitsHigh);
            Width = Round(Area.Width * _request.UnitsWide);
            Height = Round(Area.Height * _request.UnitsHigh);
        }
        finally
        {
            _syncing = false;
        }
    }

    decimal Round(double value) => Math.Round((decimal)value, _request.Decimals);

    void TakeNumbers()
    {
        if (_syncing || X is not { } x || Y is not { } y || Width is not { } width || Height is not { } height || _request.UnitsWide <= 0 || _request.UnitsHigh <= 0)
            return;
        _syncing = true;
        try
        {
            Area = AreaMath.Clamp(new RectD((double)x / _request.UnitsWide, (double)y / _request.UnitsHigh, (double)width / _request.UnitsWide, (double)height / _request.UnitsHigh));
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    void UseWhole() => Area = RectD.Full;
}
