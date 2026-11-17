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
        var autostart = new Autostart(entry, program);

        Assert.True(autostart.Set(true));
        Assert.Contains("Icon=input-tablet\n", File.ReadAllText(entry));

        string picture = Path.Combine(_folder, "app", "xperience.png");
        File.WriteAllBytes(picture, [1]);
        Assert.True(autostart.Set(true));
        string text = File.ReadAllText(entry);
        Assert.Contains($"Icon={picture}\n", text);
        Assert.Contains($"Exec=\"{program}\" --background\n", text);
    }
}
