using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Xperience.Core.Tablets;

namespace Xperience.App.Controls;

/// <summary>
/// The tablet with what each key and dial does written beside it. A key or dial, or its label, can be clicked
/// to change what it does.
/// </summary>
public sealed class TabletArt : Control
{
    public static readonly StyledProperty<TabletModel?> ModelProperty = AvaloniaProperty.Register<TabletArt, TabletModel?>(nameof(Model));
    public static readonly StyledProperty<bool> TurnedProperty = AvaloniaProperty.Register<TabletArt, bool>(nameof(Turned));
    public static readonly StyledProperty<IReadOnlyList<string>?> KeyLabelsProperty = AvaloniaProperty.Register<TabletArt, IReadOnlyList<string>?>(nameof(KeyLabels));
    public static readonly StyledProperty<IReadOnlyList<string>?> DialLabelsProperty = AvaloniaProperty.Register<TabletArt, IReadOnlyList<string>?>(nameof(DialLabels));
    public static readonly StyledProperty<ICommand?> KeyCommandProperty = AvaloniaProperty.Register<TabletArt, ICommand?>(nameof(KeyCommand));
    public static readonly StyledProperty<ICommand?> DialCommandProperty = AvaloniaProperty.Register<TabletArt, ICommand?>(nameof(DialCommand));
    public static readonly StyledProperty<bool> DimmedProperty = AvaloniaProperty.Register<TabletArt, bool>(nameof(Dimmed));

    /// <summary>One label where it is drawn: its text laid out, where it goes, and the line that leads to its control.</summary>
    sealed record Placed(FormattedText Text, Point At, Point LeaderFrom, Point LeaderTo, Rect Hit);

    /// <summary>Everything worked out for one size of the control: the tablet's parts and each label's place.</summary>
    sealed record Plan(TabletPainter.Layout Layout, IReadOnlyList<Placed> Keys, IReadOnlyList<Placed> Dials);

    Plan? _plan;
    int _hoverKey = -1, _hoverDial = -1;

    static TabletArt() => AffectsRender<TabletArt>(ModelProperty, TurnedProperty, KeyLabelsProperty, DialLabelsProperty, DimmedProperty);

    public TabletArt() => ActualThemeVariantChanged += (_, _) =>
    {
        // The labels carry their colors, so a new theme needs them laid out again.
        _plan = null;
        InvalidateVisual();
    };

    public TabletModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool Turned { get => GetValue(TurnedProperty); set => SetValue(TurnedProperty, value); }
    public IReadOnlyList<string>? KeyLabels { get => GetValue(KeyLabelsProperty); set => SetValue(KeyLabelsProperty, value); }
    public IReadOnlyList<string>? DialLabels { get => GetValue(DialLabelsProperty); set => SetValue(DialLabelsProperty, value); }

    /// <summary>Run with the key's number, from 0, when a key or its label is clicked.</summary>
    public ICommand? KeyCommand { get => GetValue(KeyCommandProperty); set => SetValue(KeyCommandProperty, value); }

    /// <summary>Run with the dial's number, from 0, when a dial or its label is clicked.</summary>
    public ICommand? DialCommand { get => GetValue(DialCommandProperty); set => SetValue(DialCommandProperty, value); }

    /// <summary>Draws the labels faint, for when the keys and dials are switched off.</summary>
    public bool Dimmed { get => GetValue(DimmedProperty); set => SetValue(DimmedProperty, value); }

    /// <summary>Where each key and dial can be clicked: the control, its label and the line between them.</summary>
    public IReadOnlyList<Rect> KeyHits => [.. (Planned()?.Keys ?? []).Select(p => p.Hit)];
    public IReadOnlyList<Rect> DialHits => [.. (Planned()?.Dials ?? []).Select(p => p.Hit)];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == ModelProperty || change.Property == TurnedProperty ||
            change.Property == KeyLabelsProperty || change.Property == DialLabelsProperty || change.Property == DimmedProperty)
            _plan = null;
    }

    /// <summary>The plan for the control as it is now. It is worked out when first needed, by drawing or by the pointer.</summary>
    Plan? Planned()
    {
        if (_plan != null)
            return _plan;
        if (Model is not { } model)
            return null;
        var body = Palette.Fit(new Rect(Bounds.Size).Deflate(2), model.WidthMm / model.HeightMm);
        if (body.Width < 40)
            return null;
        var layout = TabletPainter.Measure(model, body, Turned);
        double size = Math.Clamp(body.Height * 0.042, 11, 16);
        return _plan = new Plan(layout,
            Place(layout, layout.Keys, KeyLabels, size, Dimmed ? "XpOnTabletFaint" : "XpOnTablet"),
            Place(layout, layout.Dials, DialLabels, size, Dimmed ? "XpOnTabletFaint" : "XpOnTabletAccent"));
    }

    List<Placed> Place(TabletPainter.Layout layout, IReadOnlyList<Rect> spots, IReadOnlyList<string>? labels, double size, string color)
    {
        var placed = new List<Placed>();
        double gap = layout.Surface.Width * 0.022;
        for (int i = 0; labels != null && i < spots.Count && i < labels.Count; i++)
        {
            var text = Palette.Text(this, labels[i], size, color, FontWeight.Medium);
            double y = spots[i].Center.Y;
            Point at, from, to;
            if (layout.KeysOnRight)
            {
                (from, to) = (new Point(layout.Surface.Right - gap * 0.55, y), new Point(spots[i].X - 3, y));
                at = new Point(layout.Surface.Right - gap * 1.4 - text.Width, y - text.Height / 2);
            }
            else
            {
                (from, to) = (new Point(spots[i].Right + 3, y), new Point(layout.Surface.X + gap * 0.55, y));
                at = new Point(layout.Surface.X + gap * 1.4, y - text.Height / 2);
            }
            var hit = spots[i].Union(new Rect(at, new Size(text.Width, text.Height))).Inflate(new Thickness(4, Math.Max(3, (spots[i].Height - text.Height) / 4)));
            placed.Add(new Placed(text, at, from, to, hit));
        }
        return placed;
    }

    public override void Render(DrawingContext context)
    {
        if (Planned() is not { } plan)
            return;
        TabletPainter.Draw(context, this, plan.Layout, _hoverKey, _hoverDial);
        Draw(context, plan.Keys, _hoverKey);
        Draw(context, plan.Dials, _hoverDial);
    }

    void Draw(DrawingContext context, IReadOnlyList<Placed> labels, int hover)
    {
        var leader = Palette.Pen(this, "XpOnTabletFaint", 1);
        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            context.DrawLine(leader, label.LeaderFrom, label.LeaderTo);
            context.DrawText(label.Text, label.At);
            // The one under the pointer is underlined, to say it can be clicked.
            if (i == hover)
                context.DrawLine(Palette.Pen(this, "XpOnTabletAccent", 1.5), new Point(label.At.X, label.At.Y + label.Text.Height + 1),
                    new Point(label.At.X + label.Text.Width, label.At.Y + label.Text.Height + 1));
        }
    }

    (int Key, int Dial) Find(Point at)
    {
        if (Planned() is not { } plan)
            return (-1, -1);
        int Index(IReadOnlyList<Placed> labels)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                if (labels[i].Hit.Contains(at))
                    return i;
            }
            return -1;
        }
        return (Index(plan.Keys), Index(plan.Dials));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (key, dial) = Find(e.GetPosition(this));
        if (key >= 0)
            dial = -1;
        if (key == _hoverKey && dial == _hoverDial)
            return;
        (_hoverKey, _hoverDial) = (key, dial);
        Cursor = key >= 0 || dial >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverKey < 0 && _hoverDial < 0)
            return;
        (_hoverKey, _hoverDial) = (-1, -1);
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var (key, dial) = Find(e.GetPosition(this));
        if (key >= 0 && KeyCommand is { } keyCommand && keyCommand.CanExecute(key))
            keyCommand.Execute(key);
        else if (dial >= 0 && DialCommand is { } dialCommand && dialCommand.CanExecute(dial))
            dialCommand.Execute(dial);
        else
            return;
        e.Handled = true;
    }
}
