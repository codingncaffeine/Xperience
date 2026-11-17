namespace Xperience.App.ViewModels;

/// <summary>
/// Starting Xperience with the desktop, the way desktops provide for it: an entry in the account's autostart
/// folder. The entry's presence is the setting.
/// </summary>
public sealed class Autostart(string file, string program, string makersSystemEntry)
{
    /// <summary>Where the maker's installer puts the entry that starts its own driver program for every account.</summary>
    public const string MakersSystemEntry = "/etc/xdg/autostart/xppentablet.desktop";

    const string MakersMark = "# Written by Xperience: keeps the XPPen app from starting with the desktop.";

    /// <summary>The entry of the same name in this account's folder, which wins over the system's.</summary>
    string MakersOverride => Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileName(makersSystemEntry));

    /// <summary>True when the maker's driver program is set up to start with the desktop on this machine.</summary>
    public bool MakersAppInstalled => File.Exists(makersSystemEntry);

    /// <summary>True when it will in fact start for this account: installed, and not switched off here.</summary>
    public bool MakersAppStarts => MakersAppInstalled && !SaysHidden(MakersOverride);

    /// <summary>
    /// Lets the maker's program start with the desktop or keeps it from starting, for this account only and
    /// without touching what its installer put in place: an entry of the same name that says Hidden is the
    /// desktops' own way of switching an autostart entry off. False when the entry could not be changed, or
    /// when the one in the way was not written by Xperience and so is not Xperience's to remove.
    /// </summary>
    public bool SetMakersAppStarts(bool starts)
    {
        try
        {
            string entry = MakersOverride;
            if (!starts)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
                File.WriteAllText(entry, $"{MakersMark}\n[Desktop Entry]\nType=Application\nName=xppentablet\nHidden=true\n");
                return true;
            }
            if (!File.Exists(entry))
                return true;
            if (!File.ReadAllText(entry).Contains(MakersMark, StringComparison.Ordinal))
                return false;
            File.Delete(entry);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool SaysHidden(string entry)
    {
        try
        {
            return File.Exists(entry) && File.ReadLines(entry).Any(l => l.Replace(" ", "").Equals("Hidden=true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The entry for this account and this build.</summary>
    public static Autostart ForThisAccount()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        return new Autostart(Path.Combine(config, "autostart", "xperience.desktop"), Environment.ProcessPath ?? "xperience", MakersSystemEntry);
    }

    public bool Enabled => File.Exists(file);

    /// <summary>Adds or removes the entry; false when the file could not be changed.</summary>
    /// <summary>The picture shipped beside the program when it is there; otherwise the desktop's own tablet icon.</summary>
    string Icon => Path.GetDirectoryName(program) is { Length: > 0 } folder && File.Exists(Path.Combine(folder, "xperience.png"))
        ? Path.Combine(folder, "xperience.png") : "input-tablet";

    public bool Set(bool on)
    {
        try
        {
            if (!on)
            {
                File.Delete(file);
                return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"""
                [Desktop Entry]
                Type=Application
                Name=Xperience
                Comment=Keeps the drawing tablet's keys, dials and profiles working
                Exec="{program.Replace("\\", "\\\\").Replace("\"", "\\\"")}" --background
                Icon={Icon}
                X-GNOME-Autostart-enabled=true

                """);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
