using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xperience.Core.Desktop;
using Xperience.Core.Settings;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>One tab of the profile bar.</summary>
public sealed record ProfileTab(string Name, string App, bool Selected, bool Active);

/// <summary>The tablet at a glance: its picture, what each key and dial does, and the profiles that say so.</summary>
public sealed partial class HomeViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _turned;
    [ObservableProperty] private bool _hasTablet;
    [ObservableProperty] private string _modeTitle = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _showMode;
    [ObservableProperty] private IReadOnlyList<string> _keyLabels = [];
    [ObservableProperty] private IReadOnlyList<string> _dialLabels = [];
    [ObservableProperty] private IReadOnlyList<ProfileTab> _profiles = [];
    [ObservableProperty] private bool _canRemoveProfile;
    [ObservableProperty] private bool _keysDisabled;
    [ObservableProperty] private bool _labelsShown = true;

    public MainViewModel Main => main;
    public TabletModel Model => main.Model;

    public void Update()
    {
        var config = main.Config;
        var profile = main.EditingProfile;
        var slots = main.Hub.SlotsOf(profile);
        Turned = main.Snapshot.Pen?.Flipped ?? false;
        HasTablet = main.Presence.Mode != DriveMode.Absent;
        KeysDisabled = config.KeysDisabled;
        LabelsShown = !main.Hub.State.LabelsHidden;
        // The picture has the keys where they are on the tablet; the profile has them in the order they run as held.
        var turn = main.Snapshot.Pen?.Turn ?? TabletTurn.None;
        KeyLabels = [.. Enumerable.Range(0, Profile.KeyCount).Select(own => profile.Key(HeldOrder.Key(own, turn)).Label)];
        DialLabels = [.. Enumerable.Range(0, Profile.DialCount).Select(own => HeldOrder.Dial(own, turn)).Select(d => profile.Dial(d, slots[d]).Label)];
        string active = main.Hub.ActiveProfile.App;
        Profiles = [.. config.Profiles.Select(p => new ProfileTab(p.Name, p.App, p.App.Equals(profile.App, StringComparison.OrdinalIgnoreCase),
            config.Profiles.Count > 1 && p.App.Equals(active, StringComparison.OrdinalIgnoreCase)))];
        CanRemoveProfile = !profile.IsDefault;

        ShowMode = main.Presence.Mode != DriveMode.Driver || config.KeysDisabled;
        (ModeTitle, ModeText) = main.Presence.Mode switch
        {
            DriveMode.Absent => ("No tablet found", "Plug the tablet in and it shows up here."),
            DriveMode.BuiltIn => ("The tablet is running in its built-in mode",
                "Its keys and dials type the shortcuts fixed in the tablet. What you assign here takes effect once the Xperience driver is installed, which Settings can do."),
            _ when config.KeysDisabled => ("The keys and dials are switched off", "Settings has the switch to turn them back on."),
            _ => ("", ""),
        };
    }

    [RelayCommand]
    void SelectProfile(string app) => main.EditingApp = app;

    /// <summary>Puts the labels on the tablet's picture away, or brings them back. It is remembered.</summary>
    [RelayCommand]
    public async Task ToggleLabels()
    {
        bool hide = LabelsShown;
        await main.Hub.RememberAsync(s => s with { LabelsHidden = hide });
        Update();
    }

    /// <summary>Opens the assignment dialog for a key; keys count from 0, by the tablet's own numbers, as the picture has them.</summary>
    [RelayCommand]
    public async Task EditKey(int own)
    {
        int index = HeldOrder.Key(own, main.Snapshot.Pen?.Turn ?? TabletTurn.None);
        var profile = main.EditingProfile;
        var result = await main.Dialogs.EditActionAsync(new ActionRequest(
            string.Create(CultureInfo.InvariantCulture, $"Key {index + 1}"), profile.Key(index), Profile.DefaultKeys[index]));
        if (result != null)
            await main.ChangeConfigAsync(c => c.Change(profile.App, p => p.WithKey(index, result)));
    }

    /// <summary>Opens the dial dialog for a dial; dials count from 0, by the tablet's own numbers, as the picture has them.</summary>
    [RelayCommand]
    public async Task EditDial(int own)
    {
        int index = HeldOrder.Dial(own, main.Snapshot.Pen?.Turn ?? TabletTurn.None);
        var profile = main.EditingProfile;
        int[] slots = [.. main.Hub.SlotsOf(profile)];
        var result = await main.Dialogs.EditDialAsync(string.Create(CultureInfo.InvariantCulture, $"Dial {index + 1}"),
            index < profile.Dials.Count ? profile.Dials[index] : Profile.DefaultDials[index], slots[index], Profile.DefaultDials[index]);
        if (result is not var (functions, current))
            return;
        slots[index] = current;
        await main.Hub.RememberAsync(s => s with { DialSlots = new Dictionary<string, int[]>(s.DialSlots) { [profile.App] = slots } });
        await main.ChangeConfigAsync(c => c.Change(profile.App, p => p.WithDial(index, functions)));
    }

    [RelayCommand]
    public async Task AddProfile()
    {
        var desktop = main.Desktop;
        var running = desktop == null ? [] : await Task.Run(() => desktop.RunningAppsAsync());
        var taken = main.Config.Profiles.Select(p => p.App.ToLowerInvariant()).ToHashSet();
        var picked = await main.Dialogs.PickAppAsync([.. running.Where(a => !taken.Contains(a.Id.ToLowerInvariant()))]);
        if (picked == null || picked.Id.Length == 0)
            return;
        if (main.Config.Find(picked.Id) == null)
        {
            // A new profile starts as a copy of the one every application gets.
            var start = main.Config.DefaultProfile with { App = picked.Id, Name = PrettyName(picked.Id) };
            await main.ChangeConfigAsync(c => c with { Profiles = [.. c.Profiles, start] });
        }
        main.EditingApp = main.Config.Find(picked.Id)?.App ?? "";
    }

    [RelayCommand]
    public async Task RemoveProfile()
    {
        var profile = main.EditingProfile;
        if (profile.IsDefault || !await main.Dialogs.ConfirmAsync("Remove profile", $"Remove the profile for {profile.Name}? Its assignments are lost.", "Remove"))
            return;
        main.EditingApp = "";
        await main.ChangeConfigAsync(c => c with { Profiles = [.. c.Profiles.Where(p => !p.App.Equals(profile.App, StringComparison.OrdinalIgnoreCase))] });
    }

    [RelayCommand]
    public async Task ResetKeys()
    {
        var profile = main.EditingProfile;
        if (!await main.Dialogs.ConfirmAsync("Default keys", "Put every key and dial of this profile back to its default?", "Restore"))
            return;
        await main.Hub.RememberAsync(s => s with { DialSlots = new Dictionary<string, int[]>(s.DialSlots) { [profile.App] = [0, 0] } });
        await main.ChangeConfigAsync(c => c.Change(profile.App, p => p with { Keys = Profile.DefaultKeys, Dials = Profile.DefaultDials }));
    }

    /// <summary>A readable name for a profile from the desktop's name for the application: org.kde.krita becomes Krita.</summary>
    internal static string PrettyName(string app)
    {
        string last = app.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? app;
        return last.Length == 0 ? app : char.ToUpperInvariant(last[0]) + last[1..];
    }
}
