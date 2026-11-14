using Mp3PlayerSync.Core.Placement;

namespace Mp3PlayerSync.Tests.Placement;

public class PlacementPlannerTests
{
    private static PlacementTrack Track(string title, int? number = null, string album = "Hot Space", string artist = "Queen", string[]? albumArtists = null, int? year = 1982, string id = "") =>
        new(id.Length > 0 ? id : title, title, artist, albumArtists ?? [artist], album, year, number, ".m4a");

    private static PlayerFile File(string path, string title, int? number = null, string album = "Hot Space", string albumArtist = "Queen", string artist = "Queen") =>
        new(path, albumArtist, album, number, title, artist);

    private static Dictionary<string, string> Destinations(PlacementPlan plan) =>
        plan.NewFiles.ToDictionary(f => f.Track.Id, f => f.RelativePath);

    private static List<(string From, string To)> Moves(PlacementPlan plan) =>
        plan.Moves.Select(m => (m.FromRelativePath, m.ToRelativePath)).ToList();

    [Test]
    public async Task Only_song_from_its_album_goes_to_the_root()
    {
        var plan = PlacementPlanner.Plan([Track("Under Pressure", 11, artist: "Queen")], []);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo("Queen - Under Pressure.m4a");
        await Assert.That(plan.Moves).IsEmpty();
    }

    [Test]
    public async Task Root_name_uses_the_primary_artist_not_the_album_artist()
    {
        var plan = PlacementPlanner.Plan([Track("Song", 1, album: "Mix", artist: "Guest", albumArtists: ["Various Artists"])], []);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo("Guest - Song.m4a");
    }

    [Test]
    public async Task Two_new_songs_from_one_album_get_an_album_folder()
    {
        var plan = PlacementPlanner.Plan([Track("Staying Power", 1), Track("Under Pressure", 11)], []);

        await Assert.That(Destinations(plan)).IsEquivalentTo(new Dictionary<string, string>
        {
            ["Staying Power"] = @"Queen\Hot Space (1982)\01. Staying Power.m4a",
            ["Under Pressure"] = @"Queen\Hot Space (1982)\11. Under Pressure.m4a",
        });
    }

    [Test]
    public async Task Album_folder_is_named_for_the_first_album_artist()
    {
        var plan = PlacementPlanner.Plan(
            [Track("A", 1, album: "Duets", artist: "X", albumArtists: ["Elton John", "Kiki Dee"]), Track("B", 2, album: "Duets", artist: "Y", albumArtists: ["Elton John", "Kiki Dee"])],
            []);

        await Assert.That(plan.NewFiles.Select(f => f.RelativePath)).IsEquivalentTo(new[] { @"Elton John\Duets (1982)\01. A.m4a", @"Elton John\Duets (1982)\02. B.m4a" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task New_song_and_existing_root_song_from_one_album_both_go_into_a_new_album_folder()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File("Queen - Staying Power.mp3", "Staying Power", 1), File("Other - Thing.m4a", "Thing", 3, album: "Elsewhere", albumArtist: "Other")]);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo(@"Queen\Hot Space (1982)\11. Under Pressure.m4a");
        await Assert.That(Moves(plan)).IsEquivalentTo(new[] { ("Queen - Staying Power.mp3", @"Queen\Hot Space (1982)\01. Staying Power.mp3") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Existing_album_folder_is_reused_and_root_songs_join_it()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [
                File(@"Music\Queen - Hot Space\01 Staying Power.m4a", "Staying Power", 1),
                File(@"music\QUEEN - HOT SPACE\02 Dancer.m4a", "Dancer", 2),
                File("Queen - Back Chat.m4a", "Back Chat", 3),
            ]);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo(@"Music\Queen - Hot Space\11. Under Pressure.m4a");
        await Assert.That(Moves(plan)).IsEquivalentTo(new[] { ("Queen - Back Chat.m4a", @"Music\Queen - Hot Space\03. Back Chat.m4a") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Existing_songs_in_different_folders_send_new_songs_to_the_root_and_nothing_moves()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11), Track("Las Palabras de Amor", 7)],
            [
                File(@"A\01. Staying Power.m4a", "Staying Power", 1),
                File(@"B\02. Dancer.m4a", "Dancer", 2),
                File("Queen - Back Chat.m4a", "Back Chat", 3),
            ]);

        await Assert.That(Destinations(plan).Values).IsEquivalentTo(new[] { "Queen - Under Pressure.m4a", "Queen - Las Palabras de Amor.m4a" });
        await Assert.That(plan.Moves).IsEmpty();
    }

    [Test]
    public async Task Albums_not_in_this_sync_are_left_alone()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File("Abba - Waterloo.m4a", "Waterloo", 1, album: "Waterloo", albumArtist: "ABBA"), File("Abba - Honey Honey.m4a", "Honey Honey", 3, album: "Waterloo", albumArtist: "ABBA")]);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo("Queen - Under Pressure.m4a");
        await Assert.That(plan.Moves).IsEmpty();
    }

    [Test]
    public async Task Year_is_omitted_when_unknown()
    {
        var plan = PlacementPlanner.Plan([Track("A", 1, year: null), Track("B", 2, year: null)], []);

        await Assert.That(plan.NewFiles[0].RelativePath).IsEqualTo(@"Queen\Hot Space\01. A.m4a");
    }

    [Test]
    public async Task Track_number_is_omitted_when_unknown()
    {
        var plan = PlacementPlanner.Plan([Track("A"), Track("B", 2)], []);

        await Assert.That(plan.NewFiles[0].RelativePath).IsEqualTo(@"Queen\Hot Space (1982)\A.m4a");
    }

    [Test]
    public async Task Album_identity_ignores_case_width_and_extra_whitespace()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11, album: "Hot  Space ")],
            [File("Queen - Staying Power.m4a", "Staying Power", 1, album: "hot space", albumArtist: "ＱＵＥＥＮ")]);

        await Assert.That(plan.Moves).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Album_identity_includes_the_album_artist()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Intro", 1, album: "Greatest Hits", artist: "Queen")],
            [File("ABBA - Waterloo.m4a", "Waterloo", 1, album: "Greatest Hits", albumArtist: "ABBA", artist: "ABBA")]);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo("Queen - Intro.m4a");
        await Assert.That(plan.Moves).IsEmpty();
    }

    [Test]
    public async Task Existing_file_without_album_artist_falls_back_to_its_artist()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File("Queen - Staying Power.m4a", "Staying Power", 1, albumArtist: "")]);

        await Assert.That(plan.Moves).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Multiple_album_artists_match_the_joined_album_artist_tag()
    {
        var plan = PlacementPlanner.Plan(
            [Track("B", 2, album: "Duets", artist: "Kiki Dee", albumArtists: ["Elton John", "Kiki Dee"])],
            [File("Elton John - A.m4a", "A", 1, album: "Duets", albumArtist: "Elton John, Kiki Dee", artist: "Elton John")]);

        await Assert.That(Moves(plan)).IsEquivalentTo(new[] { ("Elton John - A.m4a", @"Elton John\Duets (1982)\01. A.m4a") }, CollectionOrdering.Matching);
        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo(@"Elton John\Duets (1982)\02. B.m4a");
    }

    [Test]
    public async Task Tracks_without_an_album_always_go_to_the_root()
    {
        var plan = PlacementPlanner.Plan([Track("A", 1, album: ""), Track("B", 2, album: "")], [File("Queen - C.m4a", "C", 3, album: "")]);

        await Assert.That(plan.NewFiles.Select(f => f.RelativePath)).IsEquivalentTo(new[] { "Queen - A.m4a", "Queen - B.m4a" }, CollectionOrdering.Matching);
        await Assert.That(plan.Moves).IsEmpty();
    }

    [Test]
    public async Task Taken_names_get_a_numbered_suffix()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Song", 1, album: "One", id: "1"), Track("Song", 1, album: "Two", id: "2"), Track("Other", album: "Three", id: "3")],
            [File("queen - song.m4a", "Song", 1, album: "Zero")],
            otherOccupiedPaths: ["Queen - Other.m4a"]);

        await Assert.That(Destinations(plan)).IsEquivalentTo(new Dictionary<string, string>
        {
            ["1"] = "Queen - Song (2).m4a",
            ["2"] = "Queen - Song (3).m4a",
            ["3"] = "Queen - Other (2).m4a",
        });
    }

    [Test]
    public async Task Moved_root_file_never_overwrites_a_file_in_the_album_folder()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File(@"Hot Space\01. Staying Power.m4a", "Staying Power", 1), File("Queen - Staying Power.m4a", "Staying Power", 1)]);

        await Assert.That(Moves(plan)).IsEquivalentTo(new[] { ("Queen - Staying Power.m4a", @"Hot Space\01. Staying Power (2).m4a") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task New_album_folder_does_not_merge_into_another_albums_folder()
    {
        var plan = PlacementPlanner.Plan(
            [Track("A", 1, album: "Hits?"), Track("B", 2, album: "Hits?")],
            [File(@"Queen\Hits_ (1982)\01. X.m4a", "X", 1, album: "Hits_")]);

        await Assert.That(plan.NewFiles[0].RelativePath).IsEqualTo(@"Queen\Hits_ (1982) (2)\01. A.m4a");
    }

    [Test]
    public async Task Moved_file_without_a_title_keeps_its_file_name()
    {
        var plan = PlacementPlanner.Plan([Track("B", 2)], [File("track01.MP3", "", 1)]);

        await Assert.That(Moves(plan)).IsEquivalentTo(new[] { ("track01.MP3", @"Queen\Hot Space (1982)\01. track01.MP3") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Names_are_sanitized()
    {
        var plan = PlacementPlanner.Plan(
            [Track("What?: \"Live\"...", 1, album: "AC/DC Live.", artist: "AC/DC "), Track("Con", 2, album: "AC/DC Live.", artist: "AC/DC ")],
            []);

        await Assert.That(plan.NewFiles.Select(f => f.RelativePath)).IsEquivalentTo(new[]
        {
            @"AC_DC\AC_DC Live (1982)\01. What__ _Live_.m4a",
            @"AC_DC\AC_DC Live (1982)\02. Con.m4a",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Root_name_that_is_a_reserved_device_name_is_escaped()
    {
        var plan = PlacementPlanner.Plan([Track("Song", album: "", artist: "") with { Title = "NUL" }], []);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo("_NUL.m4a");
    }

    [Test]
    public async Task Long_names_are_shortened_to_fit_the_path_limit()
    {
        var longAlbum = new string('a', 300);
        var longTitle = new string('t', 300);
        var plan = PlacementPlanner.Plan([Track(longTitle, 1, album: longAlbum), Track("B", 2, album: longAlbum)], [], maxRelativePathLength: 120);

        var path = plan.NewFiles[0].RelativePath;
        await Assert.That(path.Length).IsLessThanOrEqualTo(120);
        await Assert.That(path).StartsWith(@"Queen\aaaaaaaa");
        await Assert.That(path).Contains(" (1982)\\01. tttttttt");
        await Assert.That(path).EndsWith("t.m4a");
    }

    [Test]
    public async Task Album_folder_reaching_track_100_widens_existing_numbers()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Finale", 100)],
            [
                File(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "Staying Power", 1),
                File(@"Queen\Hot Space (1982)\99. Encore.m4a", "Encore", 99),
                File(@"Queen\Hot Space (1982)\cover.mp3", "Bonus", null),
                File("Queen - Dancer.m4a", "Dancer", 2),
            ]);

        await Assert.That(Moves(plan)).IsEquivalentTo(new[]
        {
            (@"Queen\Hot Space (1982)\01. Staying Power.m4a", @"Queen\Hot Space (1982)\001. Staying Power.m4a"),
            (@"Queen\Hot Space (1982)\99. Encore.m4a", @"Queen\Hot Space (1982)\099. Encore.m4a"),
            ("Queen - Dancer.m4a", @"Queen\Hot Space (1982)\002. Dancer.m4a"),
        }, CollectionOrdering.Matching);
        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo(@"Queen\Hot Space (1982)\100. Finale.m4a");
    }

    [Test]
    public async Task Album_folder_at_track_99_keeps_2_digits()
    {
        var plan = PlacementPlanner.Plan([Track("Last", 99)], [File(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "Staying Power", 1)]);

        await Assert.That(plan.Moves).IsEmpty();
        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo(@"Queen\Hot Space (1982)\99. Last.m4a");
    }

    [Test]
    public async Task Album_song_only_in_a_playlist_folder_does_not_make_it_the_album_folder()
    {
        // Corner case 1, probe P1: the album's only other song on the player is in the "Road trip" playlist folder.
        var plan = PlacementPlanner.Plan(
            [Track("Staying Power", 1), Track("Dancer", 2)],
            [File(@"Road trip\03. Queen - Back Chat.m4a", "Back Chat", 3)],
            playlistFolders: ["Road trip"]);

        await Assert.That(plan.NewFiles.Select(f => f.RelativePath)).IsEquivalentTo(new[]
        {
            @"Queen\Hot Space (1982)\01. Staying Power.m4a",
            @"Queen\Hot Space (1982)\02. Dancer.m4a",
        }, CollectionOrdering.Matching);
        await Assert.That(plan.Moves).IsEmpty().Because("Playlist files are never moved or renamed.");
    }

    [Test]
    public async Task Single_new_album_song_goes_to_the_root_when_the_others_are_only_in_playlists()
    {
        var plan = PlacementPlanner.Plan([Track("Dancer", 2)], [File(@"Road trip\03. Queen - Back Chat.m4a", "Back Chat", 3)], playlistFolders: ["Road trip"]);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo("Queen - Dancer.m4a");
    }

    [Test]
    public async Task Album_in_its_own_folder_and_a_playlist_folder_adds_to_its_own_folder()
    {
        // Corner case 2, probe P2: new tracks went to the root because the album seemed to span two folders.
        var plan = PlacementPlanner.Plan(
            [Track("Dancer", 2)],
            [File(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "Staying Power", 1), File(@"Liked Songs\07. Queen - Staying Power.m4a", "Staying Power", 1)],
            playlistFolders: ["Liked Songs"]);

        await Assert.That(plan.NewFiles.Single().RelativePath).IsEqualTo(@"Queen\Hot Space (1982)\02. Dancer.m4a");
        await Assert.That(plan.Moves).IsEmpty();
    }

    [Test]
    public async Task New_album_folder_is_never_put_inside_a_playlist_folder()
    {
        var plan = PlacementPlanner.Plan([Track("A", 1), Track("B", 2)], [], playlistFolders: ["queen"]);

        await Assert.That(plan.NewFiles[0].RelativePath).IsEqualTo(@"Queen (2)\Hot Space (1982)\01. A.m4a");
    }

    [Test]
    public async Task Compilation_tracks_share_a_various_artists_folder()
    {
        // Corner case 3, probes P3 and P9: each track of a compilation went to the root as its own one-song album.
        var tracks = new[]
        {
            new PlacementTrack("1", "Hooked on a Feeling", "Blue Swede", ["Various Artists"], "Awesome Mix Vol. 1", 2014, 1, ".m4a"),
            new PlacementTrack("2", "Come and Get Your Love", "Redbone", ["Various Artists"], "Awesome Mix Vol. 1", 2014, 3, ".m4a"),
        };

        var plan = PlacementPlanner.Plan(tracks, []);

        await Assert.That(plan.NewFiles.Select(f => f.RelativePath)).IsEquivalentTo(new[]
        {
            @"Various Artists\Awesome Mix Vol. 1 (2014)\01. Hooked on a Feeling.m4a",
            @"Various Artists\Awesome Mix Vol. 1 (2014)\03. Come and Get Your Love.m4a",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Max_relative_path_length_accounts_for_the_player_root()
    {
        await Assert.That(PlacementPlanner.MaxRelativePathLengthFor(@"E:\")).IsEqualTo(PlayerPathNames.DefaultMaxRelativePathLength);
        await Assert.That(PlacementPlanner.MaxRelativePathLengthFor(@"E:\" + new string('m', 100))).IsEqualTo(259 - 103 - 1);
    }
}

public class PlayerPathNamesTests
{
    [Test]
    [Arguments("AC/DC", "AC_DC")]
    [Arguments("a\\b:c*d?e\"f<g>h|i", "a_b_c_d_e_f_g_h_i")]
    [Arguments("tab\there", "tab here")]
    [Arguments("bell\u0007", "bell_")]
    [Arguments("  Vol. 1...  ", "Vol. 1")]
    [Arguments("...", "_")]
    [Arguments("", "_")]
    [Arguments("con", "_con")]
    [Arguments("COM1.txt", "_COM1.txt")]
    [Arguments("Lpt9 ", "_Lpt9")]
    [Arguments("Console", "Console")]
    [Arguments("Cafe\u0301", "Café")]
    public async Task Sanitizes_components(string name, string expected)
    {
        await Assert.That(PlayerPathNames.SanitizeComponent(name)).IsEqualTo(expected);
    }

    [Test]
    public async Task Limits_component_length_without_splitting_surrogate_pairs()
    {
        var name = new string('a', 254) + "😀😀";

        var sanitized = PlayerPathNames.SanitizeComponent(name);

        await Assert.That(sanitized).IsEqualTo(new string('a', 254));
    }

    [Test]
    public async Task Fixed_pieces_are_never_shortened()
    {
        var path = PlayerPathNames.Join([new PathPiece(new string('f', 50), IsFixed: true), new PathPiece(new string('t', 50), ".m4a")], maxLength: 70);

        await Assert.That(path).IsEqualTo(new string('f', 50) + @"\" + new string('t', 15) + ".m4a");
    }
}
