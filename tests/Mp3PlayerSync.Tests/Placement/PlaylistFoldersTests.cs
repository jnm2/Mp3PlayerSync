using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Tests.Placement;

public class PlaylistFoldersTests
{
    private const string MixKey = "https://open.spotify.com/playlist/mix1";

    private static PlayerRootFolder Folder(string name, string? id = null, string owner = "me", bool hasSubfolders = false) =>
        new(name, id is null ? null : new PlaylistFolderMarker(id, name, owner), hasSubfolders);

    private static PlaylistFolderAssignment Assign(string name, params PlayerRootFolder[] folders) =>
        PlaylistFolders.Assign(MixKey, name, "me", folders);

    [TestCase("https://open.spotify.com/playlist/37i9dQZF1DX?si=abc", "spotify:playlist:37i9dQZF1DX")]
    [TestCase("https://open.spotify.com/intl-de/playlist/37i9dQZF1DX", "spotify:playlist:37i9dQZF1DX")]
    [TestCase("https://open.spotify.com/collection/tracks", "spotify:collection:tracks")]
    [TestCase("spotify:playlist:abc", "spotify:playlist:abc")]
    public void Playlist_id_comes_from_the_link(string key, string id)
    {
        Assert.That(PlaylistFolderMarker.PlaylistIdFromKey(key), Is.EqualTo(id));
    }

    [Test]
    public void New_playlist_gets_a_folder_named_after_it()
    {
        var folder = Assign("My Mix", Folder("Queen", hasSubfolders: true));

        Assert.That(folder, Is.EqualTo(new PlaylistFolderAssignment("My Mix", "My Mix", new PlaylistFolderMarker("spotify:playlist:mix1", "My Mix", "me"), Exists: false)));
        Assert.That(folder.NeedsRename, Is.False);
    }

    [Test]
    public void Name_taken_by_another_owners_playlist_gets_the_owner_appended()
    {
        var folder = Assign("My Mix", Folder("my mix", "spotify:playlist:other", owner: "bob"));

        Assert.That(folder.CurrentRelativePath, Is.EqualTo("My Mix (me)"));
    }

    [Test]
    public void Name_taken_by_the_same_owners_playlist_gets_a_number()
    {
        var folder = Assign("My Mix", Folder("My Mix", "spotify:playlist:other"), Folder("My Mix (2)", "spotify:playlist:third"));

        Assert.That(folder.CurrentRelativePath, Is.EqualTo("My Mix (3)"));
    }

    [Test]
    public void Sanitized_names_that_collide_are_kept_apart()
    {
        var folder = Assign("Rock/Pop", Folder("Rock_Pop", "spotify:playlist:other"));

        Assert.That(folder.CurrentRelativePath, Is.EqualTo("Rock_Pop (2)"));
    }

    [Test]
    public void Name_taken_by_a_file_or_a_folder_with_subfolders_is_not_used()
    {
        Assert.That(PlaylistFolders.Assign(MixKey, "Notes", "me", [], rootFileNames: ["notes"]).CurrentRelativePath, Is.EqualTo("Notes (2)"));
        Assert.That(Assign("Queen", Folder("Queen", hasSubfolders: true)).CurrentRelativePath, Is.EqualTo("Queen (2)"));
    }

    [Test]
    public void Folder_from_before_markers_is_adopted()
    {
        var folder = Assign("My Mix", Folder("my mix"));

        Assert.That((folder.CurrentRelativePath, folder.FinalRelativePath, folder.Exists), Is.EqualTo(("my mix", "My Mix", true)));
    }

    [Test]
    public void Folder_with_an_unreadable_marker_is_never_used()
    {
        var folder = Assign("My Mix", new PlayerRootFolder("My Mix", PlaylistFolderMarker.Unreadable, false));

        Assert.That(folder.CurrentRelativePath, Is.EqualTo("My Mix (2)"));
    }

    [Test]
    public void Owned_folder_is_found_by_id_and_renamed_when_the_playlist_is()
    {
        var folder = Assign("New Name", Folder("Old Name", "spotify:playlist:mix1"), Folder("New", "spotify:playlist:other"));

        Assert.That((folder.CurrentRelativePath, folder.FinalRelativePath, folder.Exists, folder.NeedsRename), Is.EqualTo(("Old Name", "New Name", true, true)));
    }

    [Test]
    public void Rename_to_a_taken_name_picks_a_free_one()
    {
        var folder = Assign("Taken", Folder("Old Name", "spotify:playlist:mix1"), Folder("Taken", "spotify:playlist:other"));

        Assert.That(folder.FinalRelativePath, Is.EqualTo("Taken (2)"));
    }

    [Test]
    public void Disambiguated_folder_keeps_its_name()
    {
        var folder = Assign("My Mix", Folder("My Mix (2)", "spotify:playlist:mix1"));

        Assert.That((folder.CurrentRelativePath, folder.FinalRelativePath), Is.EqualTo(("My Mix (2)", "My Mix (2)")));
    }

    [Test]
    public void Case_change_on_spotify_renames_the_folder()
    {
        Assert.That(Assign("My Mix", Folder("my mix", "spotify:playlist:mix1")).FinalRelativePath, Is.EqualTo("My Mix"));
    }

    [Test]
    public void Folder_name_fits_the_path_limit()
    {
        var folder = PlaylistFolders.Assign(MixKey, new string('n', 300), "me", [], maxRelativePathLength: 100);

        Assert.That(folder.CurrentRelativePath.Length, Is.LessThanOrEqualTo(100 - 41));
    }
}

public class PlaylistFoldersDiskTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "Mp3PlayerSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var marker in Directory.GetFiles(root, PlaylistFolderMarker.FileName, SearchOption.AllDirectories))
            File.SetAttributes(marker, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    [Test]
    public void Same_named_playlists_get_separate_folders()
    {
        var first = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistFolders.Claim(root, first);
        var second = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/b", "Mix", "me");
        PlaylistFolders.Claim(root, second);

        Assert.That((first.CurrentRelativePath, second.CurrentRelativePath), Is.EqualTo(("Mix", "Mix (2)")));
        Assert.That(PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me").CurrentRelativePath, Is.EqualTo("Mix"));
        Assert.That(PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/b?si=x", "Mix", "me").CurrentRelativePath, Is.EqualTo("Mix (2)"));
    }

    [Test]
    public void Marker_is_hidden_not_audio_and_only_written_when_it_changes()
    {
        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistFolders.Claim(root, folder);
        var marker = Path.Combine(root, "Mix", PlaylistFolderMarker.FileName);
        var written = File.GetLastWriteTimeUtc(marker);
        File.SetLastWriteTimeUtc(marker, written.AddHours(-1));

        PlaylistFolders.Claim(root, folder);

        Assert.That(File.GetAttributes(marker).HasFlag(FileAttributes.Hidden), Is.True);
        Assert.That(AudioFileScanner.IsAudioFile(marker), Is.False);
        Assert.That(File.GetLastWriteTimeUtc(marker), Is.EqualTo(written.AddHours(-1)), "An unchanged marker isn't rewritten.");
        Assert.That(PlaylistFolderMarker.Read(Path.Combine(root, "Mix")), Is.EqualTo(folder.Marker));

        PlaylistFolders.Claim(root, folder with { Marker = folder.Marker with { Name = "Renamed" } });
        Assert.That(PlaylistFolderMarker.Read(Path.Combine(root, "Mix"))!.Name, Is.EqualTo("Renamed"), "A hidden marker can be updated.");
    }

    [Test]
    public void Claim_refuses_another_playlists_folder()
    {
        Directory.CreateDirectory(Path.Combine(root, "Mix"));
        new PlaylistFolderMarker("spotify:playlist:other", "Mix", "me").Write(Path.Combine(root, "Mix"));

        var stale = new PlaylistFolderAssignment("Mix", "Mix", new PlaylistFolderMarker("spotify:playlist:a", "Mix", "me"), Exists: true);

        Assert.That(() => PlaylistFolders.Claim(root, stale), Throws.InstanceOf<IOException>());
    }

    [Test]
    public void Renamed_playlist_folder_is_renamed_after_the_sync()
    {
        PlaylistFolders.Claim(root, PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Old", "me"));
        File.WriteAllText(Path.Combine(root, "Old", "01. A - B.m4a"), "x");

        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "New", "me");
        PlaylistFolders.Claim(root, folder);
        var actual = PlaylistFolders.FinishRename(root, folder);

        Assert.That(actual, Is.EqualTo("New"));
        Assert.That(File.Exists(Path.Combine(root, "New", "01. A - B.m4a")), Is.True);
        Assert.That(PlaylistFolderMarker.Read(Path.Combine(root, "New"))!.Name, Is.EqualTo("New"));
    }

    [Test]
    public void Case_only_rename_works()
    {
        PlaylistFolders.Claim(root, PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "mix", "me"));

        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");

        Assert.That(PlaylistFolders.FinishRename(root, folder), Is.EqualTo("Mix"));
        Assert.That(Directory.GetDirectories(root).Select(Path.GetFileName), Is.EqualTo(new[] { "Mix" }));
    }

    [Test]
    public void Rename_keeps_the_old_name_if_the_new_one_was_taken_since()
    {
        PlaylistFolders.Claim(root, PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Old", "me"));
        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "New", "me");
        Directory.CreateDirectory(Path.Combine(root, "New"));

        Assert.That(PlaylistFolders.FinishRename(root, folder), Is.EqualTo("Old"));
        Assert.That(Directory.Exists(Path.Combine(root, "Old")), Is.True);
    }

    [Test]
    public void Assigning_and_planning_write_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "Mix"));
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);

        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistPlanner.Plan(folder, [PlaylistPlannerTests.Song("A")], root, []);

        Assert.That(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories), Is.EqualTo(before));
    }
}
