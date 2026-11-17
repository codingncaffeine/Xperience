using System.Buffers.Binary;
using Avalonia.Platform;
using Xperience.App.ViewModels;

namespace Xperience.Tests;

/// <summary>The program's picture: built into the program, and pointed at by the entry that starts it with the desktop.</summary>
public sealed class ArtTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "xperience-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public Task The_icon_is_built_in_square_and_see_through_outside_its_frame() => HeadlessApp.OnUi(() =>
    {
        using var picture = AssetLoader.Open(new Uri("avares://xperience/Assets/icon.png"));
        byte[] head = new byte[26];
        picture.ReadExactly(head);
        // A PNG says its size and kind right after its signature: width, height, bit depth, color type.
        Assert.Equal("89504E470D0A1A0A", Convert.ToHexString(head, 0, 8));
        Assert.Equal((256u, 256u), (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20))));
        Assert.Equal(6, head[25]);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_autostart_entry_points_at_the_picture_beside_the_program_when_there_is_one()
    {
        string program = Path.Combine(_folder, "app", "xperience");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        string entry = Path.Combine(_folder, "autostart", "xperience.desktop");
        var autostart = new Autostart(entry, program, Path.Combine(_folder, "system", "xppentablet.desktop"));

        Assert.True(autostart.Set(true));
        Assert.Contains("Icon=input-tablet\n", File.ReadAllText(entry));

        string picture = Path.Combine(_folder, "app", "xperience.png");
        File.WriteAllBytes(picture, [1]);
        Assert.True(autostart.Set(true));
        string text = File.ReadAllText(entry);
        Assert.Contains($"Icon={picture}\n", text);
        Assert.Contains($"Exec=\"{program}\" --background\n", text);
    }

    [Fact]
    public void The_makers_app_is_kept_from_starting_for_this_account_without_touching_what_it_installed()
    {
        string system = Path.Combine(_folder, "system", "xppentablet.desktop");
        string entry = Path.Combine(_folder, "autostart", "xperience.desktop");
        string mine = Path.Combine(_folder, "autostart", "xppentablet.desktop");
        var autostart = new Autostart(entry, "/opt/xperience/xperience", system);

        // Not installed: nothing starts, and there is nothing to switch.
        Assert.False(autostart.MakersAppInstalled);
        Assert.False(autostart.MakersAppStarts);

        Directory.CreateDirectory(Path.GetDirectoryName(system)!);
        const string theirs = "[Desktop Entry]\nName=xppentablet\nExec=/usr/lib/pentablet/PenTablet.sh /mini\nType=Application\n";
        File.WriteAllText(system, theirs);
        Assert.True(autostart.MakersAppInstalled);
        Assert.True(autostart.MakersAppStarts);

        Assert.True(autostart.SetMakersAppStarts(false));
        Assert.False(autostart.MakersAppStarts);
        Assert.Contains("Hidden=true\n", File.ReadAllText(mine));
        Assert.Equal(theirs, File.ReadAllText(system));

        Assert.True(autostart.SetMakersAppStarts(true));
        Assert.True(autostart.MakersAppStarts);
        Assert.False(File.Exists(mine));

        // An entry the person made themselves is theirs: it counts, and it is not removed.
        File.WriteAllText(mine, "[Desktop Entry]\nHidden = true\n");
        Assert.False(autostart.MakersAppStarts);
        Assert.False(autostart.SetMakersAppStarts(true));
        Assert.True(File.Exists(mine));
    }
}
