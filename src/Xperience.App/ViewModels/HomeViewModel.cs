using CommunityToolkit.Mvvm.ComponentModel;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>The tablet at a glance: its picture, and what each key and dial does.</summary>
public sealed partial class HomeViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _turned;
    [ObservableProperty] private bool _hasTablet;
    [ObservableProperty] private string _modeTitle = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _showMode;

    public MainViewModel Main => main;
    public TabletModel Model => main.Model;
    public IReadOnlyList<string> KeyLabels => DefaultAssignments.Keys;
    public IReadOnlyList<string> DialLabels => DefaultAssignments.Dials;

    public void Update()
    {
        Turned = main.Snapshot.Pen?.Flipped ?? false;
        HasTablet = main.Presence.Mode != DriveMode.Absent;
        ShowMode = main.Presence.Mode != DriveMode.Driver;
        (ModeTitle, ModeText) = main.Presence.Mode switch
        {
            DriveMode.Absent => ("No tablet found", "Plug the tablet in and it shows up here."),
            DriveMode.BuiltIn => ("The tablet is running in its built-in mode",
                "Its keys and dials type the shortcuts fixed in the tablet. The assignments shown here take effect once the Xperience driver is loaded."),
            _ => ("", ""),
        };
    }
}
