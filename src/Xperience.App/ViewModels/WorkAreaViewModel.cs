using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>One entry of the screen list: a screen, all of them, or whichever is active.</summary>
public sealed record TargetChoice(ScreenTargetKind Kind, string ScreenName, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Where the pen lands: which screen, which part of it, and which way up the tablet is.</summary>
public sealed partial class WorkAreaViewModel(MainViewModel main) : ObservableObject
{
    bool _loading;

    [ObservableProperty] private TargetChoice? _selectedTarget;
    [ObservableProperty] private int _rotationIndex;
    [ObservableProperty] private bool _hasPen;
    [ObservableProperty] private bool _canFlip;
    [ObservableProperty] private bool _canSetArea;
    [ObservableProperty] private bool _isWholeScreen;
    [ObservableProperty] private bool _isSameShape;
    [ObservableProperty] private string _shapeNote = "";
    [ObservableProperty] private IReadOnlyList<ScreenInfo> _screens = [];
    [ObservableProperty] private ScreenTargetKind _targetKind;
    [ObservableProperty] private string _targetScreen = "";
    [ObservableProperty] private RectD _screenArea = RectD.Full;
    [ObservableProperty] private bool _turned;

    public MainViewModel Main => main;
    public TabletModel Model => main.Model;
    public ObservableCollection<TargetChoice> Targets { get; } = [];
    public IReadOnlyList<string> Rotations { get; } = ["0°  ·  keys on the left", "180°  ·  keys on the right"];

    double SurfaceAspect => Model.ActiveAreaMm.Aspect;

    /// <summary>The shape of what the pen is mapped to, or 0 when that changes with the active window.</summary>
    double TargetAspect()
    {
        var snapshot = main.Snapshot;
        return snapshot.Pen?.TargetKind switch
        {
            ScreenTargetKind.OneScreen => snapshot.Screen(snapshot.Pen.TargetScreen)?.Bounds.Aspect ?? 0,
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
            Screens = snapshot.Screens;
            HasPen = pen != null;
            CanFlip = pen?.CanFlip ?? false;

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
            Turned = pen?.Flipped ?? false;
            SelectedTarget = pen == null ? null : Targets.FirstOrDefault(t => t.Kind == pen.TargetKind && t.ScreenName == (pen.TargetKind == ScreenTargetKind.OneScreen ? pen.TargetScreen : ""));
            RotationIndex = Turned ? 1 : 0;

            double aspect = TargetAspect();
            CanSetArea = pen is { CanSetScreenArea: true } && aspect > 0;
            IsWholeScreen = CanSetArea && ScreenArea.IsCloseTo(RectD.Full);
            IsSameShape = CanSetArea && !IsWholeScreen && ScreenArea.IsCloseTo(AreaMath.SameShape(SurfaceAspect, aspect), 0.004);
            ShapeNote = Describe(aspect);
        }
        finally
        {
            _loading = false;
        }
    }

    string Describe(double aspect)
    {
        if (main.Snapshot.Pen == null)
            return "";
        if (aspect <= 0)
            return "The pen follows the active window from monitor to monitor, so its shape on screen changes with the monitor.";
        double stretch = AreaMath.Stretch(SurfaceAspect, aspect, ScreenArea);
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
        _ = main.WriteAsync(async (desktop, pen) =>
        {
            await desktop.SetTargetAsync(pen, value.Kind, value.ScreenName).ConfigureAwait(false);
            await desktop.SetScreenAreaAsync(pen, RectD.Full).ConfigureAwait(false);
        });
    }

    partial void OnRotationIndexChanged(int value)
    {
        if (_loading || value < 0)
            return;
        _ = main.WriteAsync((desktop, pen) => desktop.SetFlippedAsync(pen, value == 1));
    }

    [RelayCommand]
    Task WholeScreen() => main.WriteAsync((desktop, pen) => desktop.SetScreenAreaAsync(pen, RectD.Full));

    [RelayCommand]
    Task SameShape()
    {
        var area = AreaMath.SameShape(SurfaceAspect, TargetAspect());
        return main.WriteAsync((desktop, pen) => desktop.SetScreenAreaAsync(pen, area));
    }

    [RelayCommand]
    Task Identify() => main.IdentifyScreensAsync();
}
