using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Xperience.App.Controls;
using Xperience.App.ViewModels;
using Xperience.Core;
using Xperience.Core.Actions;
using Xperience.Core.Desktop;
using Xperience.Core.Pen;

namespace Xperience.App.Views.Dialogs;

public sealed partial class ActionDialog : Window
{
    public ActionDialog() => AvaloniaXamlLoader.Load(this);

    ActionDialogViewModel Model => (ActionDialogViewModel)DataContext!;

    void OnOk(object? sender, RoutedEventArgs e) => Close(Model.Build());
    void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    void OnUndo(object? sender, RoutedEventArgs e) => this.FindControl<ShortcutBox>("KeysBox")!.Undo();
    void OnClearKeys(object? sender, RoutedEventArgs e) => this.FindControl<ShortcutBox>("KeysBox")!.Clear();

    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a program", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Model.App = path.Contains(' ') ? $"\"{path}\"" : path;
    }
}

public sealed partial class DialDialog : Window
{
    public DialDialog() => AvaloniaXamlLoader.Load(this);

    void OnOk(object? sender, RoutedEventArgs e) => Close(((DialDialogViewModel)DataContext!).Build());
    void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}

public sealed partial class AreaDialog : Window
{
    public AreaDialog() => AvaloniaXamlLoader.Load(this);

    void OnOk(object? sender, RoutedEventArgs e) => Close(((AreaDialogViewModel)DataContext!).Area);
    void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}

/// <summary>A small question: yes or no, or one of a list, with an optional line to type in.</summary>
public sealed partial class AskDialog : Window
{
    public AskDialog() => AvaloniaXamlLoader.Load(this);

    public AskDialog(string title, string text, string yes) : this()
    {
        Title = title;
        this.FindControl<TextBlock>("Heading")!.Text = title;
        this.FindControl<TextBlock>("Body")!.Text = text;
        this.FindControl<Button>("Yes")!.Content = yes;
    }

    /// <summary>Offers a list to pick from and a line for a name that is not on it.</summary>
    public void Offer(IReadOnlyList<AppInfo> apps, string watermark)
    {
        var list = this.FindControl<ListBox>("Choices")!;
        var entry = this.FindControl<TextBox>("Entry")!;
        list.ItemsSource = apps.Select(a => a.Title.Length > 0 ? $"{a.Id}   ·   {a.Title}" : a.Id).ToList();
        list.IsVisible = apps.Count > 0;
        entry.IsVisible = true;
        entry.PlaceholderText = watermark;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
                entry.Text = apps[list.SelectedIndex].Id;
        };
    }

    public string Typed => this.FindControl<TextBox>("Entry")!.Text?.Trim() ?? "";

    void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>Asks the pages' questions with real windows over the main window.</summary>
public sealed class WindowDialogs(Func<Window?> owner, Func<PenReader?> pen) : IDialogs
{
    async Task<T?> ShowAsync<T>(Window dialog)
    {
        if (owner() is not { IsVisible: true } parent)
            return default;
        return await dialog.ShowDialog<T?>(parent);
    }

    public Task<PadAction?> EditActionAsync(ActionRequest request) =>
        ShowAsync<PadAction>(new ActionDialog { DataContext = new ActionDialogViewModel(request) });

    public async Task<(IReadOnlyList<DialFunction> Slots, int Current)?> EditDialAsync(string title, IReadOnlyList<DialFunction> slots, int current, IReadOnlyList<DialFunction> defaults) =>
        await ShowAsync<(IReadOnlyList<DialFunction>, int)?>(new DialDialog { DataContext = new DialDialogViewModel(title, slots, current, defaults) });

    public async Task<RectD?> EditAreaAsync(AreaRequest request)
    {
        var dialog = new AreaDialog();
        dialog.DataContext = new AreaDialogViewModel(request, corners => PickCornersAsync(dialog, corners));
        return await ShowAsync<RectD?>(dialog);
    }

    async Task<RectD?> PickCornersAsync(Window parent, CornerRequest request)
    {
        var reader = request.WithPen ? pen() : null;
        if (request.WithPen && reader == null)
            return null;
        var veil = new CornerWindow(request, reader);
        veil.PlaceOn(parent, request.Screen.Name);
        return await veil.ShowDialog<RectD?>(parent);
    }

    public Task<bool> ConfirmAsync(string title, string text, string yes) => ShowAsync<bool>(new AskDialog(title, text, yes));

    public async Task<AppInfo?> PickAppAsync(IReadOnlyList<AppInfo> running)
    {
        var dialog = new AskDialog("Add an application",
            running.Count > 0 ? "Pick the application the new profile is for. Applications with a window open are listed; any other can be typed in by the name the desktop knows it by."
                : "Type the name the desktop knows the application by, such as krita.", "Add");
        dialog.Offer(running, "Application name, such as krita");
        if (!await ShowAsync<bool>(dialog) || dialog.Typed.Length == 0)
            return null;
        return running.FirstOrDefault(a => a.Id.Equals(dialog.Typed, StringComparison.OrdinalIgnoreCase)) ?? new AppInfo(dialog.Typed, "");
    }

    public async Task<string?> PickFileToOpenAsync(string title, string pattern, string kind)
    {
        if (owner() is not { } parent)
            return null;
        var files = await parent.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(kind) { Patterns = [pattern] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileToSaveAsync(string title, string suggestedName, string pattern, string kind)
    {
        if (owner() is not { } parent)
            return null;
        var file = await parent.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(kind) { Patterns = [pattern] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<CalibrationResult?> CalibrateAsync(CalibrationRequest request)
    {
        if (owner() is not { } parent || pen() is not { } reader)
            return null;
        var window = new CalibrationWindow(request, reader);
        window.PlaceOn(parent, request.Screen.Name);
        return await window.ShowDialog<CalibrationResult?>(parent);
    }
}
