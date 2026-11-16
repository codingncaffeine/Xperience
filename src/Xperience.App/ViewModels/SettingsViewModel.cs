using CommunityToolkit.Mvvm.ComponentModel;
using Xperience.Core.Desktop;
using Xperience.Core.Tablets;

namespace Xperience.App.ViewModels;

/// <summary>What this build is, what it found, and what the pen is reporting.</summary>
public sealed partial class SettingsViewModel(MainViewModel main, string version, string built) : ObservableObject
{
    [ObservableProperty] private string _tabletLine = "";
    [ObservableProperty] private string _modeLine = "";
    [ObservableProperty] private string _desktopLine = "";
    [ObservableProperty] private string _penLine = "";
    [ObservableProperty] private string _mappingLine = "";
    [ObservableProperty] private string _position = "—";
    [ObservableProperty] private string _pressure = "—";
    [ObservableProperty] private string _tilt = "—";
    [ObservableProperty] private string _tool = "—";

    public string Version => version;
    public string Built => built;

    public void Update()
    {
        var presence = main.Presence;
        var pen = main.Snapshot.Pen;
        TabletLine = presence.Mode == DriveMode.Absent
            ? "Not connected"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"XPPen {main.Model.Name}  ·  USB {main.Model.VendorId:x4}:{main.Model.ProductId:x4}");
        ModeLine = presence.Mode switch
        {
            DriveMode.Driver => "Xperience driver: the keys and dials are real controls",
            DriveMode.BuiltIn => "Built-in mode: the keys and dials type the tablet's fixed shortcuts",
            _ => "—",
        };
        DesktopLine = main.HasDesktop ? $"{main.DesktopName}  ·  {main.Snapshot.Screens.Count} monitors" : "No supported desktop found";
        PenLine = pen == null ? "—" : $"{pen.Name}  ·  {pen.Id}";
        MappingLine = pen == null ? "—" : pen.TargetKind switch
        {
            ScreenTargetKind.OneScreen => $"{main.Snapshot.Screen(pen.TargetScreen)?.Label ?? pen.TargetScreen}{(pen.Flipped ? "  ·  turned 180°" : "")}",
            ScreenTargetKind.AllScreens => $"All monitors{(pen.Flipped ? "  ·  turned 180°" : "")}",
            _ => $"The active monitor{(pen.Flipped ? "  ·  turned 180°" : "")}",
        };
    }

    public void Tick()
    {
        var sample = main.LatestPen;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (!main.CanReadPen || !sample.InRange)
        {
            Position = Pressure = Tilt = "—";
            Tool = main.CanReadPen ? "Out of range" : "—";
            return;
        }
        Position = string.Create(culture, $"{sample.X}, {sample.Y}");
        Pressure = string.Create(culture, $"{sample.Pressure} of {main.Model.PressureMax}");
        Tilt = string.Create(culture, $"{sample.TiltX}°, {sample.TiltY}°");
        Tool = (sample.Eraser ? "Eraser" : "Pen") + (sample.Touching ? ", touching" : ", hovering");
    }
}
