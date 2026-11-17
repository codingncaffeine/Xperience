using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>One entry of the screen list: a screen, all of them, or whichever is active.</summary>
public sealed record TargetChoice(ScreenTargetKind Kind, string ScreenName, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the rotation list.</summary>
public sealed record TurnChoice(TabletTurn Turn, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Where the pen lands: which screen, which part of it, which part of the tablet, and which way up the tablet is.</summary>
public sealed partial class WorkAreaViewModel(MainViewModel main) : ObservableObject
{
    static readonly TurnChoice[] AllTurns =
    [
        new(TabletTurn.None, "0°  ·  keys on the left"),
        new(TabletTurn.Quarter, "90°  ·  keys at the top"),
        new(TabletTurn.Half, "180°  ·  keys on the right"),
        new(TabletTurn.ThreeQuarter, "270°  ·  keys at the bottom"),
    ];

    bool _loading;

    [ObservableProperty] private TargetChoice? _selectedTarget;
    [ObservableProperty] private TurnChoice? _selectedTurn;
    [ObservableProperty] private IReadOnlyList<TurnChoice> _turns = [];
    [ObservableProperty] private bool _hasPen;
    [ObservableProperty] private bool _canFlip;
    [ObservableProperty] private bool _canSetArea;
    [ObservableProperty] private bool _canSetGeometry;
    [ObservableProperty] private bool _canCalibrate;
    [ObservableProperty] private bool _isWholeScreen;
    [ObservableProperty] private bool _isSameShape;
    [ObservableProperty] private bool _isCustomScreen;
    [ObservableProperty] private bool _isFullArea;
    [ObservableProperty] private bool _isProportional;
    [ObservableProperty] private bool _isCustomArea;
    [ObservableProperty] private bool _isCalibrated;
    [ObservableProperty] private string _shapeNote = "";
    [ObservableProperty] private string _geometryNote = "";
    [ObservableProperty] private IReadOnlyList<ScreenInfo> _screens = [];
    [ObservableProperty] private ScreenTargetKind _targetKind;
    [ObservableProperty] private string _targetScreen = "";
    [ObservableProperty] private RectD _screenArea = RectD.Full;
    [ObservableProperty] private RectD _activeArea = RectD.Full;
    [ObservableProperty] private bool _turned;

    public MainViewModel Main => main;
    public TabletModel Model => main.Model;
    public ObservableCollection<TargetChoice> Targets { get; } = [];

    /// <summary>The turn the pen is at, for tests and pages that want the number.</summary>
    public TabletTurn Turn => SelectedTurn?.Turn ?? TabletTurn.None;

    /// <summary>The shape of the pen's whole surface with the tablet as it is turned.</summary>
    double SurfaceAspect(TabletTurn turn) =>
        turn is TabletTurn.Quarter or TabletTurn.ThreeQuarter ? 1 / Model.ActiveAreaMm.Aspect : Model.ActiveAreaMm.Aspect;

    /// <summary>The shape of the part of the surface in use.</summary>
    double PenAspect() => SurfaceAspect(Turn) * (ActiveArea.Height > 0 ? ActiveArea.Width / ActiveArea.Height : 1);

    /// <summary>The shape of what the pen is mapped to, as the pen meets it, or 0 when that changes with the active window.</summary>
    double TargetAspect()
    {
        var snapshot = main.Snapshot;
        return snapshot.Pen?.TargetKind switch
        {
            ScreenTargetKind.OneScreen => snapshot.Screen(snapshot.Pen.TargetScreen)?.PenAspect ?? 0,
            ScreenTargetKind.AllScreens => RectD.Around(snapshot.Screens.Select(s => s.Bounds)).Aspect,
            _ => 0,
        };
    }

    public void Update()
    {
        _loading = true;
        try
        {
            var snapshot = main.Snapshot;
            var pen = snapshot.Pen;
            var geometry = main.Config.Geometry;
            Screens = snapshot.Screens;
            HasPen = pen != null;
            CanFlip = pen?.CanFlip ?? false;
            CanSetGeometry = pen is { CanSetMatrix: true };

            var wanted = new List<TargetChoice>();
            foreach (var screen in snapshot.Screens)
                wanted.Add(new TargetChoice(ScreenTargetKind.OneScreen, screen.Name, screen.Label));
            if (snapshot.Screens.Count > 1)
                wanted.Add(new TargetChoice(ScreenTargetKind.AllScreens, "", "All monitors"));
            wanted.Add(new TargetChoice(ScreenTargetKind.ActiveScreen, "", "Whichever monitor has the active window"));
            if (!wanted.SequenceEqual(Targets))
            {
                Targets.Clear();
                foreach (var choice in wanted)
                    Targets.Add(choice);
            }

            TargetKind = pen?.TargetKind ?? ScreenTargetKind.ActiveScreen;
            TargetScreen = pen?.TargetScreen ?? "";
            ScreenArea = pen?.ScreenArea ?? RectD.Full;
            SelectedTarget = pen == null ? null : Targets.FirstOrDefault(t => t.Kind == pen.TargetKind && t.ScreenName == (pen.TargetKind == ScreenTargetKind.OneScreen ? pen.TargetScreen : ""));

            // Quarter turns need the calibration transform; without it there is only the right way up and upside down.
            var turn = pen?.Turn ?? TabletTurn.None;
            IReadOnlyList<TurnChoice> turns = CanSetGeometry ? AllTurns : [.. AllTurns.Where(t => t.Turn is TabletTurn.None or TabletTurn.Half)];
            if (!turns.SequenceEqual(Turns))
                Turns = turns;
            SelectedTurn = Turns.FirstOrDefault(t => t.Turn == turn);
            Turned = turn == TabletTurn.Half;

            // The transform the desktop holds is checked against the parts it was built from.
            var correction = PenMatrix.FromList(geometry.Correction);
            bool ours = pen == null || pen.Matrix.IsCloseTo(PenMatrix.Compose(pen.Matrix.QuarterPart(), geometry.ActiveArea, correction));
            ActiveArea = ours ? geometry.ActiveArea : RectD.Full;
            IsCalibrated = ours && !correction.IsCloseTo(PenMatrix.Identity);
            GeometryNote = ours ? "" : "The desktop holds a pen calibration that Xperience did not set. Setting an area or calibrating here replaces it.";

            double aspect = TargetAspect();
            CanSetArea = pen is { CanSetScreenArea: true } && aspect > 0;
            IsWholeScreen = CanSetArea && ScreenArea.IsCloseTo(RectD.Full);
            IsSameShape = CanSetArea && !IsWholeScreen && ScreenArea.IsCloseTo(AreaMath.SameShape(PenAspect(), aspect), 0.004);
            IsCustomScreen = CanSetArea && !IsWholeScreen && !IsSameShape;
            IsFullArea = CanSetGeometry && ours && ActiveArea.IsCloseTo(RectD.Full);
            IsProportional = CanSetGeometry && ours && !IsFullArea && aspect > 0 && ActiveArea.IsCloseTo(AreaMath.Proportional(SurfaceAspect(turn), ScreenShape(aspect)), 0.004);
            IsCustomArea = CanSetGeometry && ours && !IsFullArea && !IsProportional;
            CanCalibrate = CanSetGeometry && pen?.TargetKind == ScreenTargetKind.OneScreen && main.CanReadPen;
            ShapeNote = Describe(aspect);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The shape of the screen area the pen is mapped to.</summary>
    double ScreenShape(double targetAspect) => targetAspect * (ScreenArea.Height > 0 ? ScreenArea.Width / ScreenArea.Height : 1);

    string Describe(double aspect)
    {
        if (main.Snapshot.Pen == null)
            return "";
        if (aspect <= 0)
            return "The pen follows the active window from monitor to monitor, so its shape on screen changes with the monitor.";
        double stretch = AreaMath.Stretch(PenAspect(), aspect, ScreenArea);
        if (Math.Abs(stretch - 1) < 0.02)
            return "The pen area and the screen area have the same shape: a circle you draw stays a circle.";
        return stretch > 1
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"The screen area is wider than the pen area: a circle you draw comes out {stretch:0.00}× wider than tall.")
            : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"The screen area is taller than the pen area: a circle you draw comes out {1 / stretch:0.00}× taller than wide.");
    }

    partial void OnSelectedTargetChanged(TargetChoice? value)
    {
        if (_loading || value == null)
            return;
        // A new screen starts with all of itself: an area cut for another screen's shape would be wrong here.
        _ = SetScreenAsync(async (desktop, pen) =>
        {
            await desktop.SetTargetAsync(pen, value.Kind, value.ScreenName).ConfigureAwait(false);
            await desktop.SetScreenAreaAsync(pen, RectD.Full).ConfigureAwait(false);
        });
    }

    /// <summary>A change to the screen side of the mapping made by hand ends precision mode's claim on the area.</summary>
    Task SetScreenAsync(Func<ICompositor, string, Task> change)
    {
        _ = main.Hub.ForgetPrecisionAsync();
        return main.WriteAsync(change);
    }

    partial void OnSelectedTurnChanged(TurnChoice? value)
    {
        if (_loading || value == null)
            return;
        var geometry = main.Config.Geometry;
        bool sideways = value.Turn is TabletTurn.Quarter or TabletTurn.ThreeQuarter;
        bool wasSideways = main.Snapshot.Pen?.Matrix.QuarterPart() != TabletTurn.None;
        // An area cut for the tablet lying flat does not fit it standing up, and the other way around.
        _ = WriteGeometryAsync(value.Turn, sideways == wasSideways ? geometry : geometry with { ActiveArea = RectD.Full });
    }

    /// <summary>Saves the parts of the pen's transform and writes the whole, with the half turn, to the desktop.</summary>
    Task WriteGeometryAsync(TabletTurn turn, PenGeometry geometry) => main.Pending = WriteGeometryCoreAsync(turn, geometry);

    async Task WriteGeometryCoreAsync(TabletTurn turn, PenGeometry geometry)
    {
        if (main.Snapshot.Pen is not { } pen)
            return;
        bool flip = turn == TabletTurn.Half;
        var quarter = turn is TabletTurn.Quarter or TabletTurn.ThreeQuarter ? turn : TabletTurn.None;
        var matrix = PenMatrix.Compose(quarter, geometry.ActiveArea, PenMatrix.FromList(geometry.Correction));
        var kept = main.Config.Geometry;
        if (kept.ActiveArea != geometry.ActiveArea || !kept.Correction.SequenceEqual(geometry.Correction))
            await main.Hub.UseAsync(main.Config with { Geometry = geometry });
        await main.WriteAsync(async (desktop, id) =>
        {
            if (pen.CanFlip && pen.Flipped != flip)
                await desktop.SetFlippedAsync(id, flip).ConfigureAwait(false);
            if (pen.CanSetMatrix && !pen.Matrix.IsCloseTo(matrix, 0.0002))
                await desktop.SetMatrixAsync(id, matrix).ConfigureAwait(false);
        });
    }

    [RelayCommand]
    Task WholeScreen() => SetScreenAsync((desktop, pen) => desktop.SetScreenAreaAsync(pen, RectD.Full));

    [RelayCommand]
    Task SameShape()
    {
        var area = AreaMath.SameShape(PenAspect(), TargetAspect());
        return SetScreenAsync((desktop, pen) => desktop.SetScreenAreaAsync(pen, area));
    }

    [RelayCommand]
    public async Task CustomScreenArea()
    {
        var snapshot = main.Snapshot;
        if (snapshot.Pen is not { } pen || !CanSetArea)
            return;
        AreaRequest request;
        var rotation = ScreenRotation.None;
        if (pen.TargetKind == ScreenTargetKind.OneScreen && snapshot.Screen(pen.TargetScreen) is { } screen)
        {
            rotation = screen.Rotation;
            request = new AreaRequest("Customize screen area", $"Drag the frame, or type its numbers, to pick the part of monitor {screen.Number} the pen reaches.",
                AreaMath.ToSeen(pen.ScreenArea, rotation), screen.Bounds.Width, screen.Bounds.Height, "pixels", 0);
        }
        else
        {
            var all = RectD.Around(snapshot.Screens.Select(s => s.Bounds));
            if (all.Width <= 0 || all.Height <= 0)
                return;
            request = new AreaRequest("Customize screen area", "Drag the frame, or type its numbers, to pick the part of the desktop the pen reaches.",
                pen.ScreenArea, all.Width, all.Height, "pixels", 0,
                [.. snapshot.Screens.Select(s => new RectD((s.Bounds.X - all.X) / all.Width, (s.Bounds.Y - all.Y) / all.Height, s.Bounds.Width / all.Width, s.Bounds.Height / all.Height))]);
        }
        if (await main.Dialogs.EditAreaAsync(request) is { } picked)
        {
            var area = AreaMath.Clamp(AreaMath.ToPanel(picked, rotation));
            await SetScreenAsync((desktop, id) => desktop.SetScreenAreaAsync(id, area));
        }
    }

    [RelayCommand]
    Task FullArea() => WriteGeometryAsync(Turn, main.Config.Geometry with { ActiveArea = RectD.Full });

    [RelayCommand]
    Task Proportion()
    {
        double aspect = TargetAspect();
        return aspect <= 0 ? Task.CompletedTask
            : WriteGeometryAsync(Turn, main.Config.Geometry with { ActiveArea = AreaMath.Proportional(SurfaceAspect(Turn), ScreenShape(aspect)) });
    }

    [RelayCommand]
    public async Task CustomActiveArea()
    {
        if (!CanSetGeometry)
            return;
        bool sideways = Turn is TabletTurn.Quarter or TabletTurn.ThreeQuarter;
        double wide = sideways ? Model.ActiveAreaMm.Height : Model.ActiveAreaMm.Width, high = sideways ? Model.ActiveAreaMm.Width : Model.ActiveAreaMm.Height;
        var picked = await main.Dialogs.EditAreaAsync(new AreaRequest("Customize active area",
            "Drag the frame, or type its numbers, to pick the part of the tablet's surface the pen works on. That part reaches the whole screen area.",
            ActiveArea, wide, high, "mm", 1, DarkSurface: true));
        if (picked is { } area)
            await WriteGeometryAsync(Turn, main.Config.Geometry with { ActiveArea = AreaMath.Clamp(area) });
    }

    [RelayCommand]
    public async Task Calibrate()
    {
        var snapshot = main.Snapshot;
        if (snapshot.Pen is not { TargetKind: ScreenTargetKind.OneScreen } pen || snapshot.Screen(pen.TargetScreen) is not { } screen)
        {
            main.Problem = "Calibration needs the pen mapped to one monitor. Pick that monitor under Screen first.";
            return;
        }
        if (!main.CanReadPen)
        {
            main.Problem = "Calibration reads the pen directly, and this account has no access to the pen's input device.";
            return;
        }
        var result = await main.Dialogs.CalibrateAsync(new CalibrationRequest(screen, pen.ScreenArea, pen.Flipped));
        if (result == null)
            return;
        var geometry = main.Config.Geometry;
        if (Calibration.Correction(result.Measured, result.Wanted, pen.Matrix.QuarterPart(), ActiveArea) is not { } correction || !Calibration.IsPlausible(correction))
        {
            main.Problem = "The taps did not line up with the crosses, so nothing was changed. Calibrate with the window on the monitor the pen is mapped to.";
            return;
        }
        await WriteGeometryAsync(Turn, geometry with { ActiveArea = ActiveArea, Correction = correction.ToList() });
    }

    [RelayCommand]
    Task ResetCalibration() => WriteGeometryAsync(Turn, main.Config.Geometry with { ActiveArea = ActiveArea, Correction = PenMatrix.Identity.ToList() });

    [RelayCommand]
    Task Identify() => main.IdentifyScreensAsync();

    [RelayCommand]
    Task DisplaySettings() => main.Desktop is { } desktop ? Task.Run(() => desktop.OpenDisplaySettingsAsync()) : Task.CompletedTask;
}
