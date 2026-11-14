using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Tests.Placement;

public class PlaylistPlannerTests
{
    private const string Root = @"E:\";

    internal static ExportedTrack Song(string title, string artist = "Queen", string isrc = "") =>
        new(0, "spotify:track:" + title, title, [artist], "Album of " + title, TimeSpan.FromMinutes(3), "1982", null, "", null, isrc, "");

    internal static IndexedFile FileAt(string root, string relativePath, string title, string artist = "Queen", string isrc = "") =>
        new(Path.Combine(root, relativePath), 1, DateTime.UnixEpoch,
            new AudioTags(title, [artist], "Album of " + title, [artist], null, null, null, TimeSpan.FromMinutes(3), isrc), HasTags: true);

    private static IndexedFile File(string relativePath, string title, string artist = "Queen", string isrc = "") =>
        FileAt(Root, relativePath, title, artist, isrc);

    private static PlaylistPlan Plan(IReadOnlyList<ExportedTrack> tracks, IEnumerable<IndexedFile> files, IEnumerable<string>? cleanup = null) =>
        PlaylistPlanner.Plan("My Mix", tracks, Root, files, cleanup);

    private static IEnumerable<(string From, string To)> Renames(PlaylistPlan plan) => plan.Renames.Select(r => (r.FromRelativePath, r.ToRelativePath));

    [Test]
    public async Task New_playlist_downloads_every_track_into_the_playlist_folder()
    {
        var plan = Plan([Song("Under Pressure", "Queen"), Song("Heroes", "David Bowie")], []);

        await Assert.That(plan.FolderRelativePath).IsEqualTo("My Mix");
        await Assert.That(plan.Tracks.Select(t => (t.Position, t.State, t.TargetRelativePath))).IsEquivalentTo(new[]
        {
            (1, PlaylistTrackState.Download, @"My Mix\01. Queen - Under Pressure.m4a"),
            (2, PlaylistTrackState.Download, @"My Mix\02. David Bowie - Heroes.m4a"),
        }, CollectionOrdering.Matching);
        await Assert.That(plan.Downloads.Count()).IsEqualTo(2);
        await Assert.That(plan.Renames).IsEmpty();
        await Assert.That(plan.PlayerOnlyFiles).IsEmpty();
    }

    [Test]
    public async Task Folder_name_is_sanitized()
    {
        await Assert.That(PlaylistPlanner.FolderFor("Rock/Pop: Best of...")).IsEqualTo("Rock_Pop_ Best of");
        await Assert.That(PlaylistPlanner.FolderFor("  ")).IsEqualTo("Playlist");
    }

    [Test]
    public async Task Tracks_already_in_place_are_left_alone()
    {
        var plan = Plan(
            [Song("A", isrc: "X1"), Song("B")],
            [File(@"My Mix\01. Queen - A.m4a", "A", isrc: "X1"), File(@"My Mix\02. Queen - B.m4a", "B")]);

        await Assert.That(plan.Tracks.Select(t => t.State)).All(item => item == PlaylistTrackState.InPlace);
        await Assert.That(plan.Tracks[0].CurrentRelativePath).IsEqualTo(@"My Mix\01. Queen - A.m4a");
        await Assert.That(plan.Renames).IsEmpty();
    }

    [Test]
    public async Task Reordered_tracks_swap_numbers()
    {
        var plan = Plan([Song("B"), Song("A")], [File(@"My Mix\01. Queen - A.m4a", "A"), File(@"My Mix\02. Queen - B.m4a", "B")]);

        await Assert.That(plan.Tracks.Select(t => t.State)).All(item => item == PlaylistTrackState.Renumber);
        await Assert.That(Renames(plan)).IsEquivalentTo(new[]
        {
            (@"My Mix\02. Queen - B.m4a", @"My Mix\01. Queen - B.m4a"),
            (@"My Mix\01. Queen - A.m4a", @"My Mix\02. Queen - A.m4a"),
        });
    }

    [Test]
    public async Task Insertion_renumbers_the_tracks_after_it()
    {
        var plan = Plan([Song("A"), Song("New"), Song("B")], [File(@"My Mix\01. Queen - A.m4a", "A"), File(@"My Mix\02. Queen - B.m4a", "B")]);

        await Assert.That(plan.Tracks.Select(t => (t.State, t.TargetRelativePath))).IsEquivalentTo(new[]
        {
            (PlaylistTrackState.InPlace, @"My Mix\01. Queen - A.m4a"),
            (PlaylistTrackState.Download, @"My Mix\02. Queen - New.m4a"),
            (PlaylistTrackState.Renumber, @"My Mix\03. Queen - B.m4a"),
        }, CollectionOrdering.Matching);
        await Assert.That(Renames(plan)).IsEquivalentTo(new[] { (@"My Mix\02. Queen - B.m4a", @"My Mix\03. Queen - B.m4a") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Renumbering_keeps_the_rest_of_the_file_name()
    {
        var plan = Plan([Song("New"), Song("A")], [File(@"My Mix\01. my own name.mp3", "A"), File(@"My Mix\untitled.mp3", "New")]);

        await Assert.That(Renames(plan)).IsEquivalentTo(new[]
        {
            (@"My Mix\untitled.mp3", @"My Mix\01. untitled.mp3"),
            (@"My Mix\01. my own name.mp3", @"My Mix\02. my own name.mp3"),
        });
    }

    [Test]
    public async Task Reaching_100_tracks_widens_every_number_to_3_digits()
    {
        var tracks = Enumerable.Range(1, 100).Select(i => Song($"Song {i}")).ToList();
        var files = Enumerable.Range(1, 99).Select(i => File($@"My Mix\{i:D2}. Queen - Song {i}.m4a", $"Song {i}")).ToList();
        files.Add(File(@"My Mix\50. Old - Gone.m4a", "Gone", "Old"));

        var plan = Plan(tracks, files);

        await Assert.That(plan.Tracks.Take(99).Select(t => t.State)).All(item => item == PlaylistTrackState.Renumber);
        await Assert.That(plan.Tracks[0].TargetRelativePath).IsEqualTo(@"My Mix\001. Queen - Song 1.m4a");
        await Assert.That(plan.Tracks[98].TargetRelativePath).IsEqualTo(@"My Mix\099. Queen - Song 99.m4a");
        await Assert.That(plan.Tracks[99].TargetRelativePath).IsEqualTo(@"My Mix\100. Queen - Song 100.m4a");
        await Assert.That(plan.PlayerOnlyFiles.Single().TargetRelativePath).IsEqualTo(@"My Mix\050. Old - Gone.m4a").Because("Player-only files are widened too.");
        await Assert.That(plan.Renames).Count().IsEqualTo(100);
    }

    [Test]
    public async Task At_99_tracks_numbers_stay_2_digits()
    {
        var tracks = Enumerable.Range(1, 99).Select(i => Song($"Song {i}")).ToList();

        await Assert.That(Plan(tracks, []).Tracks[98].TargetRelativePath).IsEqualTo(@"My Mix\99. Queen - Song 99.m4a");
    }

    [Test]
    public async Task Shrinking_below_100_tracks_narrows_numbers_again()
    {
        var plan = Plan([Song("A")], [File(@"My Mix\001. Queen - A.m4a", "A")]);

        await Assert.That(Renames(plan)).IsEquivalentTo(new[] { (@"My Mix\001. Queen - A.m4a", @"My Mix\01. Queen - A.m4a") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Files_no_longer_in_the_playlist_are_player_only_and_shown_where_they_were()
    {
        var gone = File(@"My Mix\02. Old - Gone.m4a", "Gone", "Old");
        var plan = Plan(
            [Song("A"), Song("B")],
            [File(@"My Mix\01. Queen - A.m4a", "A"), gone, File(@"My Mix\03. Queen - B.m4a", "B"), File(@"My Mix\notes.mp3", "Notes", "Me")]);

        await Assert.That(plan.PlayerOnlyFiles.Select(f => (f.RelativePath, f.TargetRelativePath, f.AfterPosition, f.Delete))).IsEquivalentTo(new[]
        {
            (@"My Mix\02. Old - Gone.m4a", @"My Mix\02. Old - Gone.m4a", 1, false),
            (@"My Mix\notes.mp3", @"My Mix\notes.mp3", 2, false),
        }, CollectionOrdering.Matching);
        await Assert.That(plan.PlayerOnlyFiles[0].File).IsSameReferenceAs(gone);
        await Assert.That(plan.Rows.Select(r => r.Track?.Track.Title ?? r.PlayerOnlyFile!.File.Tags.Title)).IsEquivalentTo(new[] { "A", "Gone", "B", "Notes" }, CollectionOrdering.Matching);
        await Assert.That(Renames(plan)).IsEquivalentTo(new[] { (@"My Mix\03. Queen - B.m4a", @"My Mix\02. Queen - B.m4a") }, CollectionOrdering.Matching);
        await Assert.That(plan.Deletions).IsEmpty().Because("Nothing is deleted unless the user selects it.");
    }

    [Test]
    public async Task Player_only_file_before_every_track_is_shown_first()
    {
        var plan = Plan([Song("A")], [File(@"My Mix\01. Old - Gone.m4a", "Gone", "Old"), File(@"My Mix\02. Queen - A.m4a", "A")]);

        await Assert.That(plan.PlayerOnlyFiles.Single().AfterPosition).IsZero();
        await Assert.That(plan.Rows[0].PlayerOnlyFile).IsNotNull();
    }

    [Test]
    public async Task Only_player_only_files_selected_for_cleanup_are_deleted()
    {
        var plan = Plan(
            [Song("A")],
            [File(@"My Mix\01. Queen - A.m4a", "A"), File(@"My Mix\02. Old - Gone.m4a", "Gone", "Old"), File(@"My Mix\03. Old - Kept.m4a", "Kept", "Old")],
            cleanup: [@"my mix/02. Old - Gone.m4a", @"My Mix\01. Queen - A.m4a", @"Elsewhere.m4a"]);

        await Assert.That(plan.Deletions.Select(f => f.RelativePath)).IsEquivalentTo(new[] { @"My Mix\02. Old - Gone.m4a" }, CollectionOrdering.Matching);
        await Assert.That(plan.Tracks.Single().State).IsEqualTo(PlaylistTrackState.InPlace).Because("A playlist track is never deleted, even if selected.");
    }

    [Test]
    public async Task Track_elsewhere_on_the_player_is_copied_into_the_folder()
    {
        var plan = Plan(
            [Song("Under Pressure", isrc: "GB1"), Song("Heroes", "David Bowie")],
            [File(@"Queen\Hot Space (1982)\11. Under Pressure.mp3", "Under Pressure", isrc: "GB1"), File(@"Other Mix\01. David Bowie - Heroes.m4a", "Heroes", "David Bowie")]);

        await Assert.That(plan.Tracks.Select(t => (t.State, t.CopySourceRelativePath, t.TargetRelativePath))).IsEquivalentTo(new (PlaylistTrackState, string?, string)[]
        {
            (PlaylistTrackState.CopyFromPlayer, @"Queen\Hot Space (1982)\11. Under Pressure.mp3", @"My Mix\01. Queen - Under Pressure.mp3"),
            (PlaylistTrackState.CopyFromPlayer, @"Other Mix\01. David Bowie - Heroes.m4a", @"My Mix\02. David Bowie - Heroes.m4a"),
        }, CollectionOrdering.Matching);
        await Assert.That(plan.PlayerOnlyFiles).IsEmpty().Because("Files outside the folder are never player-only.");
    }

    [Test]
    public async Task Only_a_reliable_match_elsewhere_is_copied()
    {
        var plan = Plan([Song("Heroes", "David Bowie")], [File(@"Heroes.m4a", "Heroes", "Someone Else")]);

        await Assert.That(plan.Tracks.Single().State).IsEqualTo(PlaylistTrackState.Download);
    }

    [Test]
    public async Task Track_listed_twice_copies_the_folder_file_after_it_is_renumbered()
    {
        var plan = Plan([Song("New"), Song("A"), Song("A")], [File(@"My Mix\01. Queen - A.m4a", "A")]);

        await Assert.That(plan.Tracks.Select(t => (t.State, t.TargetRelativePath, t.CopySourceRelativePath))).IsEquivalentTo(new (PlaylistTrackState, string, string?)[]
        {
            (PlaylistTrackState.Download, @"My Mix\01. Queen - New.m4a", null),
            (PlaylistTrackState.Renumber, @"My Mix\02. Queen - A.m4a", null),
            (PlaylistTrackState.CopyFromPlayer, @"My Mix\03. Queen - A.m4a", @"My Mix\02. Queen - A.m4a"),
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Files_parked_by_an_interrupted_sync_are_renamed_into_place()
    {
        var plan = Plan(
            [Song("A"), Song("B")],
            [File(@"My Mix\~mps-0a1b2c 02. Queen - A.m4a", "A"), File(@"My Mix\02. Queen - B.m4a", "B"), File(@"My Mix\~mps-ffffff 07. Old - Gone.m4a", "Gone", "Old")]);

        await Assert.That(Renames(plan)).IsEquivalentTo(new[]
        {
            (@"My Mix\~mps-0a1b2c 02. Queen - A.m4a", @"My Mix\01. Queen - A.m4a"),
            (@"My Mix\~mps-ffffff 07. Old - Gone.m4a", @"My Mix\07. Old - Gone.m4a"),
        });
        await Assert.That(plan.Tracks[1].State).IsEqualTo(PlaylistTrackState.InPlace);
    }

    [Test]
    public async Task Name_held_by_a_player_only_file_gets_a_suffix()
    {
        var plan = Plan([Song("A")], [File(@"My Mix\01. Queen - A.m4a", "Not A", "Someone")]);

        await Assert.That(plan.Tracks.Single().TargetRelativePath).IsEqualTo(@"My Mix\01. Queen - A (2).m4a");
    }

    [Test]
    public async Task Files_in_subfolders_and_outside_the_player_are_ignored()
    {
        var plan = Plan([Song("A")], [File(@"My Mix\Sub\01. Queen - X.m4a", "X"), new IndexedFile(@"F:\My Mix\01. Queen - Y.m4a", 1, DateTime.UnixEpoch, AudioTags.Empty, false)]);

        await Assert.That(plan.PlayerOnlyFiles).IsEmpty();
        await Assert.That(plan.Tracks.Single().State).IsEqualTo(PlaylistTrackState.Download);
    }

    [Test]
    [Arguments(LibraryItemKind.SavedAlbum, true)]
    [Arguments(LibraryItemKind.AlbumPlaylist, true)]
    [Arguments(LibraryItemKind.Playlist, false)]
    [Arguments(LibraryItemKind.LikedSongs, false)]
    public async Task Albums_use_the_album_layout_and_playlists_the_playlist_layout(LibraryItemKind kind, bool album)
    {
        await Assert.That(PlacementPlanner.UsesAlbumLayout(kind)).IsEqualTo(album);
    }
}

public class TrackNumberingTests
{
    [Test]
    [Arguments(0, 2)]
    [Arguments(99, 2)]
    [Arguments(100, 3)]
    [Arguments(999, 3)]
    [Arguments(1000, 4)]
    public async Task Width_grows_with_the_highest_number(int max, int width)
    {
        await Assert.That(TrackNumbering.WidthFor(max)).IsEqualTo(width);
    }

    [Test]
    [Arguments("07. Queen - A.m4a", 7, 2, "Queen - A.m4a")]
    [Arguments("~mps-a1b2c3 107. Queen - A.m4a", 107, 3, "Queen - A.m4a")]
    [Arguments("99 Luftballons.mp3", null, 0, "99 Luftballons.mp3")]
    [Arguments("~mps-a1b2c3 untitled.mp3", null, 0, "untitled.mp3")]
    public async Task Parses_number_prefix(string name, int? number, int digits, string rest)
    {
        await Assert.That(TrackNumbering.Parse(name)).IsEqualTo(new NumberedFileName(number, digits, rest));
    }

    [Test]
    public async Task Temporary_prefix_is_recognized()
    {
        var parked = TrackNumbering.AddTemporaryPrefix("05. A.m4a");

        await Assert.That(TrackNumbering.HasTemporaryPrefix(parked)).IsTrue();
        await Assert.That(TrackNumbering.StripTemporaryPrefix(parked)).IsEqualTo("05. A.m4a");
        await Assert.That(TrackNumbering.WithNumber(parked, 6, 3)).IsEqualTo("006. A.m4a");
    }
}
