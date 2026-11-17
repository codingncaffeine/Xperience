using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Xperience.Core.Actions;

namespace Xperience.App.Controls;

/// <summary>Names the keys Avalonia reports the way shortcuts are written.</summary>
public static class KeyNames
{
    public static string? From(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F12 => "F" + (key - Key.F1 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        _ => null,
    };

    public static KeyMods Modifier(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => KeyMods.Ctrl,
        Key.LeftShift or Key.RightShift => KeyMods.Shift,
        Key.LeftAlt or Key.RightAlt => KeyMods.Alt,
        Key.LWin or Key.RWin => KeyMods.Super,
        _ => KeyMods.None,
    };
}

/// <summary>
/// A box that takes shortcuts by having them pressed. Click it, press Ctrl+Z, and it holds Ctrl+Z; a modifier
/// pressed and let go alone is taken as that modifier. It holds up to <see cref="Max"/> shortcuts in order.
/// </summary>
public sealed class ShortcutBox : Border
{
    public static readonly StyledProperty<string> KeysProperty =
        AvaloniaProperty.Register<ShortcutBox, string>(nameof(Keys), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> MaxProperty = AvaloniaProperty.Register<ShortcutBox, int>(nameof(Max), 1);
    public static readonly StyledProperty<bool> AddSuperProperty = AvaloniaProperty.Register<ShortcutBox, bool>(nameof(AddSuper));
    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<ShortcutBox, string>(nameof(Placeholder), "Click here, then press the shortcut");

    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    KeyMods _held;
    bool _used;

    public ShortcutBox()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 10);
        MinHeight = 44;
        Child = _text;
        ActualThemeVariantChanged += (_, _) => Show();
        LostFocus += (_, _) =>
        {
            _held = KeyMods.None;
            _used = false;
        };
    }

    public string Keys { get => GetValue(KeysProperty); set => SetValue(KeysProperty, value); }
    public int Max { get => GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public bool AddSuper { get => GetValue(AddSuperProperty); set => SetValue(AddSuperProperty, value); }
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    public IReadOnlyList<KeyCombo> Combos => KeyCombo.ParseAll(Keys);

    /// <summary>Takes one shortcut as if it had been pressed.</summary>
    public void Take(KeyCombo combo)
    {
        if (combo.IsEmpty)
            return;
        var combos = Combos.ToList();
        if (Max <= 1)
            combos = [combo];
        else if (combos.Count < Max)
            combos.Add(combo);
        Keys = KeyCombo.Write(combos);
    }

    public void Undo() => Keys = KeyCombo.Write(Combos.SkipLast(1));

    public void Clear() => Keys = "";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KeysProperty || change.Property == PlaceholderProperty || change.Property == IsFocusedProperty)
            Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show();
    }

    void Show()
    {
        var combos = Combos;
        bool empty = combos.Count == 0;
        _text.Text = empty ? (IsFocused ? "Press the shortcut…" : Placeholder) : string.Join("   ›   ", combos);
        _text.Foreground = Palette.Brush(this, empty ? "XpTextFaint" : "XpText");
        _text.FontWeight = empty ? FontWeight.Normal : FontWeight.Medium;
        Background = Palette.Brush(this, "XpBg");
        BorderBrush = Palette.Brush(this, IsFocused ? "XpAccent" : "XpLine");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var modifier = KeyNames.Modifier(e.Key);
        if (modifier != KeyMods.None)
        {
            if (_held == KeyMods.None)
                _used = false;
            _held |= modifier;
            return;
        }
        if (KeyNames.From(e.Key) is not { } name)
            return;
        var mods = KeyMods.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mods |= KeyMods.Ctrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mods |= KeyMods.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mods |= KeyMods.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || AddSuper) mods |= KeyMods.Super;
        _used = true;
        Take(new KeyCombo(mods, name));
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        var modifier = KeyNames.Modifier(e.Key);
        if (modifier == KeyMods.None)
            return;
        // Modifiers pressed and let go with no key between are a shortcut of their own, such as Alt.
        if (!_used && _held != KeyMods.None)
        {
            Take(new KeyCombo(_held | (AddSuper ? KeyMods.Super : KeyMods.None), ""));
            _used = true;
        }
        _held &= ~modifier;
    }

}
