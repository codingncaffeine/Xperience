using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xperience.App.Controls;
using Xperience.App.ViewModels;
using Xperience.Core;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;

namespace Xperience.App.Views.Dialogs;

/// <summary>
/// The veil for picking an area by its upper left and lower right corners. It covers one monitor. For a screen
/// area the corners are clicked on the veil, which lets the screen show through. For the tablet's active area
/// they are tapped with the pen and read from the pen itself; there the veil only keeps the taps from landing
/// on whatever else is on that monitor.
/// </summary>
public sealed class CornerWindow : Window
{
    readonly CornerRequest _request;
    readonly PenReader? _pen;
    readonly DispatcherTimer? _timer;
    readonly Veil _veil;
    TapCollector _taps = new(2, flipped: false);
    (double X, double Y)? _first;
    bool _settingFirst;

    public CornerWindow(CornerRequest request, PenReader? pen)
    {
        _request = request;
        _pen = pen;
        Title = request.WithPen ? "Set the active area" : "Set the screen area";
        WindowDecorations = WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        Topmost = true;
        ShowInTaskbar = false;
        if (!request.WithPen)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
        }
        Content = _veil = new Veil
        {
            WithPen = request.WithPen,
            Screen = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"monitor {request.Screen.Number}"),
            SurfaceAspect = SurfaceAspect(request),
            SeesThrough = () => ActualTransparencyLevel != WindowTransparencyLevel.None,
        };
        if (request.WithPen && pen != null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(12) };
            _timer.Tick += (_, _) => Tick();
            Opened += (_, _) => _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }
    }

    /// <summary>The area that was picked; null while picking and when the veil was left.</summary>
    public RectD? Picked { get; private set; }

    /// <summary>The shape of what the corners are picked on, as the person looks at it.</summary>
    static double SurfaceAspect(CornerRequest request)
    {
        if (!request.WithPen)
            return request.Screen.Bounds.Height > 0 ? request.Screen.Bounds.Width / request.Screen.Bounds.Height : 16.0 / 9;
        double aspect = request.Screen.PenAspect > 0 ? request.Screen.PenAspect : 16.0 / 9;
        return request.QuarterPart == TabletTurn.None ? aspect : 1 / aspect;
    }

    /// <summary>Puts the veil on the monitor of that name, when the window system knows its monitors by name.</summary>
    public void PlaceOn(Window owner, string screenName)
    {
        if (!Placement.OnScreen(this, owner, screenName))
            _veil.Warning = "Make sure this covers the monitor the pen is mapped to before going on.";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close(null);
    }

    (double X, double Y) Share(Point at) => Bounds.Width > 0 && Bounds.Height > 0 ? (at.X / Bounds.Width, at.Y / Bounds.Height) : (0, 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_request.WithPen)
            return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            Close(null);
            return;
        }
        var at = Share(point.Position);
        if (_first is { } first)
        {
            Finish(first, at);
            return;
        }
        _first = at;
        _settingFirst = true;
        _veil.Note = "";
        _veil.First = at;
        _veil.Pointer = at;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_request.WithPen)
            _veil.Pointer = Share(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_request.WithPen || !_settingFirst || _first is not { } first)
            return;
        _settingFirst = false;
        // Pressing on one corner and letting go on the other picks the area in one drag.
        var at = Share(e.GetPosition(this));
        if (AreaMath.FromCorners(first, at) != null)
            Finish(first, at);
    }

    void Tick()
    {
        if (_pen != null)
            TakePenReport(_pen.Latest.Touching, _pen.Position);
    }

    /// <summary>Takes one report of the pen: whether it touches, and where it is as shares of the tablet's surface as built.</summary>
    internal void TakePenReport(bool touching, (double X, double Y)? at)
    {
        if (!_request.WithPen || !_taps.Add(touching, at))
            return;
        var tap = AreaMath.TapOnSurface(_taps.Measured[^1], _request.Flipped, _request.QuarterPart);
        if (_first is not { } first)
        {
            _first = tap;
            _veil.Note = "";
            _veil.First = tap;
            return;
        }
        _taps = new TapCollector(2, flipped: false);
        Finish(first, tap);
    }

    void Finish((double X, double Y) first, (double X, double Y) second)
    {
        if (AreaMath.FromCorners(first, second) is not { } area)
        {
            _first = null;
            _settingFirst = false;
            _veil.First = null;
            _veil.Note = "That area is too small. Start again with its upper left corner.";
            return;
        }
        Picked = area;
        Close(area);
    }

    /// <summary>Draws the veil: what to do next, the corner already set, and the area taking shape.</summary>
    sealed class Veil : Control
    {
        (double X, double Y)? _first;
        (double X, double Y) _pointer;
        string _note = "", _warning = "";

        public Veil() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

        public bool WithPen { get; init; }
        public string Screen { get; init; } = "";
        public double SurfaceAspect { get; init; } = 16.0 / 9;
        public Func<bool> SeesThrough { get; init; } = () => true;

        public (double X, double Y)? First { get => _first; set { _first = value; InvalidateVisual(); } }
        public (double X, double Y) Pointer { get => _pointer; set { _pointer = value; InvalidateVisual(); } }
        public string Note { get => _note; set { _note = value; InvalidateVisual(); } }
        public string Warning { get => _warning; set { _warning = value; InvalidateVisual(); } }

        public override void Render(DrawingContext context)
        {
            var all = new Rect(Bounds.Size);
            if (WithPen)
                RenderForPen(context, all);
            else
                RenderForScreen(context, all);
        }

        void RenderForScreen(DrawingContext context, Rect all)
        {
            bool through = SeesThrough();
            var veil = through ? new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)) : Palette.Brush(this, "XpBg");
            if (First is { } first)
            {
                var picked = new Rect(new Point(Math.Min(first.X, Pointer.X) * all.Width, Math.Min(first.Y, Pointer.Y) * all.Height),
                    new Point(Math.Max(first.X, Pointer.X) * all.Width, Math.Max(first.Y, Pointer.Y) * all.Height));
                // The veil is drawn around the area so the area itself shows the screen as it is.
                context.DrawRectangle(veil, null, new Rect(0, 0, all.Width, picked.Y));
                context.DrawRectangle(veil, null, new Rect(0, picked.Bottom, all.Width, Math.Max(0, all.Height - picked.Bottom)));
                context.DrawRectangle(veil, null, new Rect(0, picked.Y, picked.X, picked.Height));
                context.DrawRectangle(veil, null, new Rect(picked.Right, picked.Y, Math.Max(0, all.Width - picked.Right), picked.Height));
                context.DrawRectangle(through ? null : Palette.Brush(this, "XpAccentSoft"), Palette.Pen(this, "XpAccent", 2), picked);
            }
            else
                context.DrawRectangle(veil, null, all);

            Explain(context, all,
                First == null ? "Click the upper left corner of the area" : "Click the lower right corner",
                $"This is {Screen}. Esc or a right click leaves without changing anything."
                    + (through ? "" : " The screen cannot show through here; the frame and the numbers work everywhere."));
        }

        void RenderForPen(DrawingContext context, Rect all)
        {
            context.DrawRectangle(Palette.Brush(this, "XpBg"), null, all);
            var surface = Palette.Fit(new Rect(all.Width * 0.3, all.Height * 0.42, all.Width * 0.4, all.Height * 0.4), SurfaceAspect);
            context.DrawRectangle(Palette.Brush(this, "XpTabletScreen"), Palette.Pen(this, "XpLine", 1), surface, 6, 6);
            if (First is { } first)
                context.DrawEllipse(Palette.Brush(this, "XpAccent"), null, new Point(surface.X + first.X * surface.Width, surface.Y + first.Y * surface.Height), 6, 6);
            Explain(context, all,
                First == null ? "Tap the upper left corner of the area on the tablet" : "Tap the lower right corner",
                "Tap with the pen on the tablet itself; the picture below shows where the first tap was. Esc leaves without changing anything.");
        }

        void Explain(DrawingContext context, Rect all, string heading, string detail)
        {
            var title = Palette.Text(this, heading, 22, "XpText", FontWeight.SemiBold);
            var help = Palette.Text(this, detail, 14, "XpTextMuted");
            var lines = new List<FormattedText> { title, help };
            if (Note.Length > 0)
                lines.Add(Palette.Text(this, Note, 14, "XpWarn", FontWeight.Medium));
            if (Warning.Length > 0)
                lines.Add(Palette.Text(this, Warning, 14, "XpWarn", FontWeight.Medium));
            double width = lines.Max(l => l.Width), height = lines.Sum(l => l.Height) + 10 * (lines.Count - 1);
            var panel = new Rect((all.Width - width) / 2 - 28, all.Height * 0.16, width + 56, height + 40);
            context.DrawRectangle(Palette.Brush(this, "XpCard"), Palette.Pen(this, "XpLine", 1), panel, 14, 14);
            double y = panel.Y + 20;
            foreach (var line in lines)
            {
                context.DrawText(line, new Point((all.Width - line.Width) / 2, y));
                y += line.Height + 10;
            }
        }
    }
}

/// <summary>Putting a full-screen window on one monitor.</summary>
static class Placement
{
    /// <summary>Moves the window to the monitor of that name; false when the window system does not know it by that name.</summary>
    public static bool OnScreen(Window window, Window owner, string screenName)
    {
        var screen = owner.Screens.All.FirstOrDefault(s => s.DisplayName == screenName);
        if (screen == null)
            return false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = screen.Bounds.Position;
        return true;
    }
}
