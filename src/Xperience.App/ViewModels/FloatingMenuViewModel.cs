using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Actions;
using Xperience.Core.Settings;

namespace Xperience.App.ViewModels;

/// <summary>The floating menu's setup: what its eight buttons do and which way it is laid out.</summary>
public sealed partial class FloatingMenuViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private IReadOnlyList<string> _labels = [];
    [ObservableProperty] private bool _vertical;

    public MainViewModel Main => main;

    /// <summary>Asked to show or hide the floating menu itself.</summary>
    public event Action? ShowRequested;

    public IReadOnlyList<PadAction> Buttons => main.Config.FloatingMenu.Buttons;

    public void Update()
    {
        var menu = main.Config.FloatingMenu;
        Labels = [.. menu.Buttons.Select(b => b.Label)];
        Vertical = menu.Vertical;
    }

    [RelayCommand]
    public async Task EditButton(int index)
    {
        var menu = main.Config.FloatingMenu;
        var result = await main.Dialogs.EditActionAsync(new ActionRequest(
            string.Create(CultureInfo.InvariantCulture, $"Floating menu button {index + 1}"), menu.Buttons[index], FloatingMenuConfig.DefaultButtons[index], ForMenu: true));
        if (result != null)
            await main.ChangeConfigAsync(c => c with { FloatingMenu = c.FloatingMenu with { Buttons = [.. c.FloatingMenu.Buttons.Select((b, i) => i == index ? result : b)] } });
    }

    [RelayCommand]
    Task SetVertical(string vertical) =>
        main.ChangeConfigAsync(c => c with { FloatingMenu = c.FloatingMenu with { Vertical = vertical == "true" } });

    [RelayCommand]
    void ShowMenu() => ShowRequested?.Invoke();
}
