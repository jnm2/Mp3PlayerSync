using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Tagging;

namespace Mp3PlayerSync.Tests;

public class MusicFolderTests
{
    private string drive = null!;

    [Before(Test)]
    public void SetUp()
    {
        drive = Path.Combine(Path.GetTempPath(), "Mp3PlayerSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(drive);
    }

    [After(Test)]
    public void TearDown() => Directory.Delete(drive, recursive: true);

    [Test]
    public void A_drive_without_a_music_folder_syncs_to_its_root()
    {
        Directory.CreateDirectory(Path.Combine(drive, "Podcasts"));

        MusicFolder.Choose(drive).Folder.ShouldBeEmpty();
    }

    [Test]
    [Arguments("MUSIC")]
    [Arguments("Music")]
    [Arguments("music")]
    public void A_card_with_nothing_from_the_app_uses_its_music_folder_with_its_own_casing(string name)
    {
        Directory.CreateDirectory(Path.Combine(drive, name));

        var choice = MusicFolder.Choose(drive);

        choice.Folder.ShouldBe(name);
        MusicFolder.PathFor(drive, choice).ShouldBe(Path.Combine(drive, name));
    }

    [Test]
    public void A_music_folder_further_down_is_not_used()
    {
        Directory.CreateDirectory(Path.Combine(drive, "Audio", "MUSIC"));

        MusicFolder.Choose(drive).Folder.ShouldBeEmpty();
    }

    [Test]
    public void Playlist_folders_at_the_root_keep_the_root_even_with_an_empty_music_folder()
    {
        Directory.CreateDirectory(Path.Combine(drive, "MUSIC"));
        Marker(Path.Combine(drive, "Road Trip"));

        MusicFolder.Choose(drive).ShouldBe(new MusicFolderChoice("", InMusic: 0, AtRoot: 1));
    }

    [Test]
    [Arguments("Queen - Under Pressure.m4a")]
    [Arguments(@"Queen\Hot Space (1982)\11. Under Pressure.m4a")]
    public void The_apps_downloads_at_the_root_keep_the_root(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(drive, "MUSIC"));
        Download(relativePath, isrc: "GBUM71029605");

        MusicFolder.Choose(drive).Folder.ShouldBeEmpty();
    }

    [Test]
    [Arguments(@"MUSIC\Road Trip\" + PlaylistFolderMarker.FileName)]
    [Arguments(@"MUSIC\Queen\Hot Space (1982)\11. Under Pressure.m4a")]
    public void The_apps_content_in_the_music_folder_keeps_the_music_folder(string relativePath)
    {
        if (relativePath.EndsWith(".m4a", StringComparison.Ordinal)) Download(relativePath, isrc: "GBUM71029605");
        else Marker(Path.Combine(drive, Path.GetDirectoryName(relativePath)!));

        MusicFolder.Choose(drive).Folder.ShouldBe("MUSIC");
    }

    [Test]
    public void The_users_own_files_at_the_root_dont_count_as_the_apps()
    {
        Directory.CreateDirectory(Path.Combine(drive, "MUSIC"));
        Download("Ripped from a CD.m4a", isrc: "");
        File.WriteAllText(Path.Combine(drive, "notes.txt"), "");

        MusicFolder.Choose(drive).Folder.ShouldBe("MUSIC");
    }

    [Test]
    public void With_the_apps_content_in_both_places_the_one_with_more_wins()
    {
        Marker(Path.Combine(drive, "Road Trip"));
        Marker(Path.Combine(drive, "Workout"));
        Marker(Path.Combine(drive, "Music", "Road Trip"));

        var choice = MusicFolder.Choose(drive);

        choice.ShouldBe(new MusicFolderChoice("", InMusic: 1, AtRoot: 2));
        choice.FoundInBoth.ShouldBeTrue();

        Marker(Path.Combine(drive, "Music", "Workout"));
        Marker(Path.Combine(drive, "Music", "Chill"));
        MusicFolder.Choose(drive).Folder.ShouldBe("Music");
    }

    [Test]
    public void A_tie_goes_to_the_music_folder()
    {
        Marker(Path.Combine(drive, "Road Trip"));
        Marker(Path.Combine(drive, "Music", "Road Trip"));

        MusicFolder.Choose(drive).Folder.ShouldBe("Music");
    }

    [Test]
    public void Choosing_writes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(drive, "MUSIC"));
        Download(@"Queen\Hot Space\11. Under Pressure.m4a", isrc: "");
        var before = Snapshot();

        MusicFolder.Choose(drive);

        Snapshot().ShouldBe(before);
    }

    private static void Marker(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PlaylistFolderMarker.FileName), "{}");
    }

    private void Download(string relativePath, string isrc)
    {
        var path = Path.Combine(drive, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tagging", "Assets", "silence.m4a"), path);
        TrackTagWriter.Write(path, new TrackMetadata { Title = Path.GetFileNameWithoutExtension(path), Isrc = isrc }, art: null);
    }

    // Files only: listing a folder updates the folder's own last-access time, as any scan of the player does.
    private string[] Snapshot() =>
        [.. Directory.EnumerateFiles(drive, "*", SearchOption.AllDirectories)
            .Select(p => $"{p}|{File.GetLastWriteTimeUtc(p):O}|{File.GetLastAccessTimeUtc(p):O}|{File.GetAttributes(p)}")
            .Order()];
}
