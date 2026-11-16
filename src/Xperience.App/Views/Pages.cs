using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

/// <summary>The pen page takes the pen's live reading only while it is on screen.</summary>
public sealed partial class PenView : UserControl
{
    readonly DispatcherTimer _live;

    public PenView()
    {
        AvaloniaXamlLoader.Load(this);
        _live = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => (DataContext as PenViewModel)?.Tick());
        this.FindControl<CurveEditor>("Editor")!.Edited += (_, _) => _ = (DataContext as PenViewModel)?.ApplyAsync();
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
        _live = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => (DataContext as SettingsViewModel)?.Tick());
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
