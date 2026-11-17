using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xperience.App.Controls;
using Xperience.App.ViewModels;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;

namespace Xperience.App.Views.Dialogs;

/// <summary>
/// The tap-the-crosses screen. It covers the monitor the pen is mapped to and shows one cross at a time; each
/// tap is read from the pen itself, so what comes out is where the tablet says the pen was for each cross.
/// </summary>
public sealed class CalibrationWindow : Window
{
    readonly CalibrationRequest _request;
    readonly PenReader _pen;
    readonly DispatcherTimer _timer;
    readonly Crosses _crosses;
    readonly TapCollector _taps;

    public CalibrationWindow(CalibrationRequest request, PenReader pen)
    {
        _request = request;
        _pen = pen;
        _taps = new TapCollector(Calibration.Crosses.Count, request.Flipped);
        Title = "Calibrate the pen";
        WindowDecorations = WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        Topmost = true;
        ShowInTaskbar = false;
        Content = _crosses = new Crosses { Screen = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"monitor {request.Screen.Number}") };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(12) };
        _timer.Tick += (_, _) => Tick();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Puts the window on the monitor of that name, when the window system knows its monitors by name.</summary>
    public void PlaceOn(Window owner, string screenName)
    {
        var screen = owner.Screens.All.FirstOrDefault(s => s.DisplayName == screenName);
        if (screen == null)
        {
            _crosses.Warning = "Make sure this window is on the monitor the pen is mapped to before tapping.";
            return;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = screen.Bounds.Position;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close(null);
    }

    void Tick()
    {
        if (!_taps.Add(_pen.Latest.Touching, _pen.Position))
            return;
        if (!_taps.Done)
        {
            _crosses.Current = _taps.Measured.Count;
            return;
        }
        Close(new CalibrationResult(_taps.Measured,
            [.. Calibration.Crosses.Select(c => Calibration.Wanted(c, _request.Screen.Rotation, _request.ScreenArea))]));
    }

    /// <summary>Draws the crosses: the one to tap in the accent color, the ones done faint.</summary>
    sealed class Crosses : Control
    {
        int _current;
        string _warning = "";

        public Crosses() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

        public string Screen { get; init; } = "";

        public int Current
        {
            get => _current;
            set
            {
                _current = value;
                InvalidateVisual();
            }
        }

        public string Warning
        {
            get => _warning;
            set
            {
                _warning = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            var all = new Rect(Bounds.Size);
            context.DrawRectangle(Palette.Brush(this, "XpBg"), null, all);
            for (int i = 0; i < Calibration.Crosses.Count; i++)
            {
                var (x, y) = Calibration.Crosses[i];
                var at = new Point(all.Width * x, all.Height * y);
                bool now = i == Current;
                if (!now && i > Current)
                    continue;
                var pen = Palette.Pen(this, now ? "XpAccent" : "XpTextFaint", now ? 2 : 1);
                double arm = now ? 22 : 12;
                context.DrawLine(pen, at - new Vector(arm, 0), at + new Vector(arm, 0));
                context.DrawLine(pen, at - new Vector(0, arm), at + new Vector(0, arm));
                if (now)
                    context.DrawEllipse(null, Palette.Pen(this, "XpAccent", 1), at, arm * 0.6, arm * 0.6);
            }

            var title = Palette.Text(this, string.Create(System.Globalization.CultureInfo.CurrentCulture,
                $"Tap the center of the cross  ·  {Math.Min(Current + 1, Calibration.Crosses.Count)} of {Calibration.Crosses.Count}"), 22, "XpText", FontWeight.SemiBold);
            var help = Palette.Text(this, $"Hold the pen the way you draw. This is {Screen}. Esc leaves without changing anything.", 14, "XpTextMuted");
            double top = all.Height * 0.30;
            context.DrawText(title, new Point((all.Width - title.Width) / 2, top));
            context.DrawText(help, new Point((all.Width - help.Width) / 2, top + title.Height + 10));
            if (Warning.Length > 0)
            {
                var warning = Palette.Text(this, Warning, 14, "XpWarn", FontWeight.Medium);
                context.DrawText(warning, new Point((all.Width - warning.Width) / 2, top + title.Height + help.Height + 22));
            }
        }
    }
}
