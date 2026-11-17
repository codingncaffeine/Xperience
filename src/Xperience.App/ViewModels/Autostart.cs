namespace Xperience.App.ViewModels;

/// <summary>
/// Starting Xperience with the desktop, the way desktops provide for it: an entry in the account's autostart
/// folder. The entry's presence is the setting.
/// </summary>
public sealed class Autostart(string file, string program)
{
    /// <summary>The entry for this account and this build.</summary>
    public static Autostart ForThisAccount()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        return new Autostart(Path.Combine(config, "autostart", "xperience.desktop"), Environment.ProcessPath ?? "xperience");
    }

    public bool Enabled => File.Exists(file);

    /// <summary>Adds or removes the entry; false when the file could not be changed.</summary>
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
                Icon=input-tablet
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
