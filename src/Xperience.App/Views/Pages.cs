using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xperience.App.Controls;
using Xperience.App.ViewModels;

namespace Xperience.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class WorkAreaView : UserControl
{
    public WorkAreaView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class DisplayView : UserControl
{
    public DisplayView()
    {
        AvaloniaXamlLoader.Load(this);
        // The brightness is written once, when the slider is let go, not for every step on the way.
        var brightness = this.FindControl<Slider>("Brightness")!;
        brightness.AddHandler(PointerReleasedEvent, (_, _) => _ = (DataContext as DisplayViewModel)?.ApplyBrightnessAsync(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        brightness.AddHandler(KeyUpEvent, (_, _) => _ = (DataContext as DisplayViewModel)?.ApplyBrightnessAsync(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }
}

public sealed partial class FloatingMenuView : UserControl
{
    public FloatingMenuView() => AvaloniaXamlLoader.Load(this);

    /// <summary>A tile stands for the button with the same place in the list.</summary>
    void OnTile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.FindAncestorOfType<ContentPresenter>() is not { } container)
            return;
        int index = this.FindControl<ItemsControl>("Tiles")!.IndexFromContainer(container);
        if (index >= 0 && DataContext is FloatingMenuViewModel model)
            model.EditButtonCommand.Execute(index);
    }
}

/// <summary>The pen page takes the pen's live reading only while it is on screen.</summary>
public sealed partial class PenView : UserControl
{
    readonly DispatcherTimer _live;

    public PenView()
    {
        AvaloniaXamlLoader.Load(this);
        _live = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _live.Tick += (_, _) => (DataContext as PenViewModel)?.Tick();
        this.FindControl<CurveEditor>("Editor")!.Edited += (_, _) => _ = (DataContext as PenViewModel)?.ApplyAsync();
        this.FindControl<ScribbleArea>("Scribble")!.StrokeSeen += line => (DataContext as PenViewModel)?.Main.Log.Write("app", line);
        // The speed is written once, when the slider is let go, not for every step on the way.
        var speed = this.FindControl<Slider>("Speed")!;
        speed.AddHandler(PointerReleasedEvent, (_, _) => _ = (DataContext as PenViewModel)?.ApplySpeedAsync(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        speed.AddHandler(KeyUpEvent, (_, _) => _ = (DataContext as PenViewModel)?.ApplySpeedAsync(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        // Smoothing reloads the driver, so it too is applied once, when its slider is let go.
        var smoothing = this.FindControl<Slider>("Smoothing")!;
        smoothing.AddHandler(PointerReleasedEvent, (_, _) => _ = (DataContext as PenViewModel)?.ApplySmoothingAsync(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        smoothing.AddHandler(KeyUpEvent, (_, _) => _ = (DataContext as PenViewModel)?.ApplySmoothingAsync(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    void OnClear(object? sender, RoutedEventArgs e) => this.FindControl<ScribbleArea>("Scribble")!.Clear();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _live.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _live.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}

public sealed partial class SettingsView : UserControl
{
    readonly DispatcherTimer _live;

    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        _live = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _live.Tick += (_, _) => (DataContext as SettingsViewModel)?.Tick();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _live.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _live.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
