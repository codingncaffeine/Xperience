using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Xperience.App.Controls;
using Xperience.App.ViewModels;

namespace Xperience.App.Views;

/// <summary>
/// The floating menu: eight buttons in a small window that stays above the others. It never takes the
/// keyboard, so a shortcut one of its buttons types goes to the application being drawn in.
/// </summary>
public sealed class FloatingMenuWindow : Window
{
    readonly MainViewModel _main;
    readonly UniformGrid _grid = new() { Margin = new Thickness(8, 0, 8, 8) };
    readonly ToggleButton _pin;
    bool _placed;

    public FloatingMenuWindow(MainViewModel main)
    {
        _main = main;
        Title = "Xperience floating menu";
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        _pin = HeaderToggle("IconPin", "Hold the menu in place");
        _pin.IsChecked = main.Hub.State.FloatingMenuPinned;
        _pin.IsCheckedChanged += (_, _) => _ = main.Hub.RememberAsync(s => s with { FloatingMenuPinned = _pin.IsChecked == true });
        var gear = HeaderButton("IconSettings", "Set up the floating menu");
        gear.Click += (_, _) =>
        {
            main.Open("floatingmenu");
            main.Hub.Ui?.ShowWindow();
        };
        var close = HeaderButton("IconClose", "Close the floating menu");
        close.Click += (_, _) => Hide();

        var header = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 8, 10, 6),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _pin, gear, close } },
        };
        header.PointerPressed += (_, e) =>
        {
            if (_pin.IsChecked != true && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        var frame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { header, _grid } },
        };
        frame[!Border.BackgroundProperty] = frame.GetResourceObservable("XpPanel").ToBinding();
        frame[!Border.BorderBrushProperty] = frame.GetResourceObservable("XpLine").ToBinding();
        Content = frame;

        PositionChanged += (_, e) =>
        {
            if (_placed && IsVisible)
                _ = main.Hub.RememberAsync(s => s with { FloatingMenuAt = [e.Point.X, e.Point.Y] });
        };
        Opened += (_, _) =>
        {
            if (main.Hub.State.FloatingMenuAt is [var x, var y])
                Position = new PixelPoint((int)x, (int)y);
            _placed = true;
        };
        Fill();
    }

    /// <summary>Builds the buttons from the settings as they are now.</summary>
    public void Fill()
    {
        var menu = _main.Config.FloatingMenu;
        _grid.Columns = menu.Vertical ? 2 : 4;
        _grid.Rows = menu.Vertical ? 4 : 2;
        _grid.Children.Clear();
        foreach (var action in menu.Buttons)
        {
            var button = new Button
            {
                Classes = { "tile" },
                Width = 104,
                Height = 58,
                Margin = new Thickness(3),
                Focusable = false,
                Content = new TextBlock { Text = action.Label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12.5, MaxLines = 2 },
            };
            button.Click += (_, _) => _ = _main.Hub.RunAsync(action);
            _grid.Children.Add(button);
        }
    }

    static Button HeaderButton(string icon, string tip)
    {
        var button = new Button { Classes = { "icon" }, Width = 28, Height = 28, CornerRadius = new CornerRadius(999), Focusable = false, Content = Glyph(icon) };
        ToolTip.SetTip(button, tip);
        return button;
    }

    static ToggleButton HeaderToggle(string icon, string tip)
    {
        var button = new ToggleButton { Classes = { "chip" }, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), Focusable = false, Content = Glyph(icon) };
        ToolTip.SetTip(button, tip);
        return button;
    }

    static Control Glyph(string key)
    {
        var icon = new PathIcon { Width = 13, Height = 13 };
        if (Application.Current!.TryFindResource(key, out object? data) && data is Geometry geometry)
            icon.Data = geometry;
        return icon;
    }

    /// <summary>Shows the menu without letting it take the keyboard.</summary>
    public void ShowQuietly()
    {
        Fill();
        NeverFocus();
        Show();
    }

    /// <summary>
    /// Tells the window manager that this window takes no keyboard input. Under X11 that is one hint on the
    /// window; other window systems have no such request for an ordinary window, and there the menu's
    /// shortcuts only reach the application if it gets the focus back by itself.
    /// </summary>
    void NeverFocus()
    {
        if (TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle)
            return;
        try
        {
            nint display = XOpenDisplay(0);
            if (display == 0)
                return;
            var hints = new XWMHints { Flags = InputHint, Input = 0 };
            XSetWMHints(display, handle.Handle, ref hints);
            XFlush(display);
            XCloseDisplay(display);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No X library to ask; the menu still works, it just takes the focus when clicked.
        }
    }

    const nint InputHint = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct XWMHints
    {
        public nint Flags;
        public int Input;
        public int InitialState;
        public nint IconPixmap;
        public nint IconWindow;
        public int IconX, IconY;
        public nint IconMask;
        public nint WindowGroup;
    }

    [DllImport("libX11.so.6")] static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] static extern int XSetWMHints(nint display, nint window, ref XWMHints hints);
    [DllImport("libX11.so.6")] static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] static extern int XCloseDisplay(nint display);
}
