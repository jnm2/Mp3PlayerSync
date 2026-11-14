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

    [Test]
    [Arguments("https://open.spotify.com/playlist/37i9dQZF1DX?si=abc", "spotify:playlist:37i9dQZF1DX")]
    [Arguments("https://open.spotify.com/intl-de/playlist/37i9dQZF1DX", "spotify:playlist:37i9dQZF1DX")]
    [Arguments("https://open.spotify.com/collection/tracks", "spotify:collection:tracks")]
    [Arguments("spotify:playlist:abc", "spotify:playlist:abc")]
    public async Task Playlist_id_comes_from_the_link(string key, string id)
    {
        await Assert.That(PlaylistFolderMarker.PlaylistIdFromKey(key)).IsEqualTo(id);
    }

    [Test]
    public async Task New_playlist_gets_a_folder_named_after_it()
    {
        var folder = Assign("My Mix", Folder("Queen", hasSubfolders: true));

        await Assert.That(folder).IsEqualTo(new PlaylistFolderAssignment("My Mix", "My Mix", new PlaylistFolderMarker("spotify:playlist:mix1", "My Mix", "me"), Exists: false));
        await Assert.That(folder.NeedsRename).IsFalse();
    }

    [Test]
    public async Task Name_taken_by_another_owners_playlist_gets_the_owner_appended()
    {
        var folder = Assign("My Mix", Folder("my mix", "spotify:playlist:other", owner: "bob"));

        await Assert.That(folder.CurrentRelativePath).IsEqualTo("My Mix (me)");
    }

    [Test]
    public async Task Name_taken_by_the_same_owners_playlist_gets_a_number()
    {
        var folder = Assign("My Mix", Folder("My Mix", "spotify:playlist:other"), Folder("My Mix (2)", "spotify:playlist:third"));

        await Assert.That(folder.CurrentRelativePath).IsEqualTo("My Mix (3)");
    }

    [Test]
    public async Task Sanitized_names_that_collide_are_kept_apart()
    {
        var folder = Assign("Rock/Pop", Folder("Rock_Pop", "spotify:playlist:other"));

        await Assert.That(folder.CurrentRelativePath).IsEqualTo("Rock_Pop (2)");
    }

    [Test]
    public async Task Name_taken_by_a_file_or_a_folder_with_subfolders_is_not_used()
    {
        await Assert.That(PlaylistFolders.Assign(MixKey, "Notes", "me", [], rootFileNames: ["notes"]).CurrentRelativePath).IsEqualTo("Notes (2)");
        await Assert.That(Assign("Queen", Folder("Queen", hasSubfolders: true)).CurrentRelativePath).IsEqualTo("Queen (2)");
    }

    [Test]
    public async Task Folder_from_before_markers_is_adopted()
    {
        var folder = Assign("My Mix", Folder("my mix"));

        await Assert.That((folder.CurrentRelativePath, folder.FinalRelativePath, folder.Exists)).IsEqualTo(("my mix", "My Mix", true));
    }

    [Test]
    public async Task Folder_with_an_unreadable_marker_is_never_used()
    {
        var folder = Assign("My Mix", new PlayerRootFolder("My Mix", PlaylistFolderMarker.Unreadable, false));

        await Assert.That(folder.CurrentRelativePath).IsEqualTo("My Mix (2)");
    }

    [Test]
    public async Task Owned_folder_is_found_by_id_and_renamed_when_the_playlist_is()
    {
        var folder = Assign("New Name", Folder("Old Name", "spotify:playlist:mix1"), Folder("New", "spotify:playlist:other"));

        await Assert.That((folder.CurrentRelativePath, folder.FinalRelativePath, folder.Exists, folder.NeedsRename)).IsEqualTo(("Old Name", "New Name", true, true));
    }

    [Test]
    public async Task Rename_to_a_taken_name_picks_a_free_one()
    {
        var folder = Assign("Taken", Folder("Old Name", "spotify:playlist:mix1"), Folder("Taken", "spotify:playlist:other"));

        await Assert.That(folder.FinalRelativePath).IsEqualTo("Taken (2)");
    }

    [Test]
    public async Task Disambiguated_folder_keeps_its_name()
    {
        var folder = Assign("My Mix", Folder("My Mix (2)", "spotify:playlist:mix1"));

        await Assert.That((folder.CurrentRelativePath, folder.FinalRelativePath)).IsEqualTo(("My Mix (2)", "My Mix (2)"));
    }

    [Test]
    public async Task Case_change_on_spotify_renames_the_folder()
    {
        await Assert.That(Assign("My Mix", Folder("my mix", "spotify:playlist:mix1")).FinalRelativePath).IsEqualTo("My Mix");
    }

    [Test]
    public async Task Folder_name_fits_the_path_limit()
    {
        var folder = PlaylistFolders.Assign(MixKey, new string('n', 300), "me", [], maxRelativePathLength: 100);

        await Assert.That(folder.CurrentRelativePath.Length).IsLessThanOrEqualTo(100 - 41);
    }
}

public class PlaylistFoldersDiskTests
{
    private string root = null!;

    [Before(Test)]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "Mp3PlayerSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var marker in Directory.GetFiles(root, PlaylistFolderMarker.FileName, SearchOption.AllDirectories))
            File.SetAttributes(marker, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    [Test]
    public async Task Same_named_playlists_get_separate_folders()
    {
        var first = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistFolders.Claim(root, first);
        var second = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/b", "Mix", "me");
        PlaylistFolders.Claim(root, second);

        await Assert.That((first.CurrentRelativePath, second.CurrentRelativePath)).IsEqualTo(("Mix", "Mix (2)"));
        await Assert.That(PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me").CurrentRelativePath).IsEqualTo("Mix");
        await Assert.That(PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/b?si=x", "Mix", "me").CurrentRelativePath).IsEqualTo("Mix (2)");
    }

    [Test]
    public async Task Marker_is_hidden_not_audio_and_only_written_when_it_changes()
    {
        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistFolders.Claim(root, folder);
        var marker = Path.Combine(root, "Mix", PlaylistFolderMarker.FileName);
        var written = File.GetLastWriteTimeUtc(marker);
        File.SetLastWriteTimeUtc(marker, written.AddHours(-1));

        PlaylistFolders.Claim(root, folder);

        await Assert.That(File.GetAttributes(marker).HasFlag(FileAttributes.Hidden)).IsTrue();
        await Assert.That(AudioFileScanner.IsAudioFile(marker)).IsFalse();
        await Assert.That(File.GetLastWriteTimeUtc(marker)).IsEqualTo(written.AddHours(-1)).Because("An unchanged marker isn't rewritten.");
        await Assert.That(PlaylistFolderMarker.Read(Path.Combine(root, "Mix"))).IsEqualTo(folder.Marker);

        PlaylistFolders.Claim(root, folder with { Marker = folder.Marker with { Name = "Renamed" } });
        await Assert.That(PlaylistFolderMarker.Read(Path.Combine(root, "Mix"))!.Name).IsEqualTo("Renamed").Because("A hidden marker can be updated.");
    }

    [Test]
    public async Task Claim_refuses_another_playlists_folder()
    {
        Directory.CreateDirectory(Path.Combine(root, "Mix"));
        new PlaylistFolderMarker("spotify:playlist:other", "Mix", "me").Write(Path.Combine(root, "Mix"));

        var stale = new PlaylistFolderAssignment("Mix", "Mix", new PlaylistFolderMarker("spotify:playlist:a", "Mix", "me"), Exists: true);

        await Assert.That(() => PlaylistFolders.Claim(root, stale)).Throws<IOException>();
    }

    [Test]
    public async Task Renamed_playlist_folder_is_renamed_after_the_sync()
    {
        PlaylistFolders.Claim(root, PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Old", "me"));
        File.WriteAllText(Path.Combine(root, "Old", "01. A - B.m4a"), "x");

        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "New", "me");
        PlaylistFolders.Claim(root, folder);
        var actual = PlaylistFolders.FinishRename(root, folder);

        await Assert.That(actual).IsEqualTo("New");
        await Assert.That(File.Exists(Path.Combine(root, "New", "01. A - B.m4a"))).IsTrue();
        await Assert.That(PlaylistFolderMarker.Read(Path.Combine(root, "New"))!.Name).IsEqualTo("New");
    }

    [Test]
    public async Task Case_only_rename_works()
    {
        PlaylistFolders.Claim(root, PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "mix", "me"));

        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");

        await Assert.That(PlaylistFolders.FinishRename(root, folder)).IsEqualTo("Mix");
        await Assert.That(Directory.GetDirectories(root).Select(Path.GetFileName)).IsEquivalentTo(new string?[] { "Mix" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rename_keeps_the_old_name_if_the_new_one_was_taken_since()
    {
        PlaylistFolders.Claim(root, PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Old", "me"));
        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "New", "me");
        Directory.CreateDirectory(Path.Combine(root, "New"));

        await Assert.That(PlaylistFolders.FinishRename(root, folder)).IsEqualTo("Old");
        await Assert.That(Directory.Exists(Path.Combine(root, "Old"))).IsTrue();
    }

    [Test]
    public async Task Assigning_and_planning_write_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "Mix"));
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);

        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistPlanner.Plan(folder, [PlaylistPlannerTests.Song("A")], root, []);

        await Assert.That(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }
}
