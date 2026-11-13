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
    public void Only_song_from_its_album_goes_to_the_root()
    {
        var plan = PlacementPlanner.Plan([Track("Under Pressure", 11, artist: "Queen")], []);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo("Queen - Under Pressure.m4a"));
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void Root_name_uses_the_primary_artist_not_the_album_artist()
    {
        var plan = PlacementPlanner.Plan([Track("Song", 1, album: "Mix", artist: "Guest", albumArtists: ["Various Artists"])], []);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo("Guest - Song.m4a"));
    }

    [Test]
    public void Two_new_songs_from_one_album_get_an_album_folder()
    {
        var plan = PlacementPlanner.Plan([Track("Staying Power", 1), Track("Under Pressure", 11)], []);

        Assert.That(Destinations(plan), Is.EqualTo(new Dictionary<string, string>
        {
            ["Staying Power"] = @"Queen\Hot Space (1982)\01. Staying Power.m4a",
            ["Under Pressure"] = @"Queen\Hot Space (1982)\11. Under Pressure.m4a",
        }));
    }

    [Test]
    public void Album_folder_is_named_for_the_first_album_artist()
    {
        var plan = PlacementPlanner.Plan(
            [Track("A", 1, album: "Duets", artist: "X", albumArtists: ["Elton John", "Kiki Dee"]), Track("B", 2, album: "Duets", artist: "Y", albumArtists: ["Elton John", "Kiki Dee"])],
            []);

        Assert.That(plan.NewFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { @"Elton John\Duets (1982)\01. A.m4a", @"Elton John\Duets (1982)\02. B.m4a" }));
    }

    [Test]
    public void New_song_and_existing_root_song_from_one_album_both_go_into_a_new_album_folder()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File("Queen - Staying Power.mp3", "Staying Power", 1), File("Other - Thing.m4a", "Thing", 3, album: "Elsewhere", albumArtist: "Other")]);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\11. Under Pressure.m4a"));
        Assert.That(Moves(plan), Is.EqualTo(new[] { ("Queen - Staying Power.mp3", @"Queen\Hot Space (1982)\01. Staying Power.mp3") }));
    }

    [Test]
    public void Existing_album_folder_is_reused_and_root_songs_join_it()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [
                File(@"Music\Queen - Hot Space\01 Staying Power.m4a", "Staying Power", 1),
                File(@"music\QUEEN - HOT SPACE\02 Dancer.m4a", "Dancer", 2),
                File("Queen - Back Chat.m4a", "Back Chat", 3),
            ]);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo(@"Music\Queen - Hot Space\11. Under Pressure.m4a"));
        Assert.That(Moves(plan), Is.EqualTo(new[] { ("Queen - Back Chat.m4a", @"Music\Queen - Hot Space\03. Back Chat.m4a") }));
    }

    [Test]
    public void Existing_songs_in_different_folders_send_new_songs_to_the_root_and_nothing_moves()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11), Track("Las Palabras de Amor", 7)],
            [
                File(@"A\01. Staying Power.m4a", "Staying Power", 1),
                File(@"B\02. Dancer.m4a", "Dancer", 2),
                File("Queen - Back Chat.m4a", "Back Chat", 3),
            ]);

        Assert.That(Destinations(plan).Values, Is.EquivalentTo(new[] { "Queen - Under Pressure.m4a", "Queen - Las Palabras de Amor.m4a" }));
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void Albums_not_in_this_sync_are_left_alone()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File("Abba - Waterloo.m4a", "Waterloo", 1, album: "Waterloo", albumArtist: "ABBA"), File("Abba - Honey Honey.m4a", "Honey Honey", 3, album: "Waterloo", albumArtist: "ABBA")]);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo("Queen - Under Pressure.m4a"));
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void Year_is_omitted_when_unknown()
    {
        var plan = PlacementPlanner.Plan([Track("A", 1, year: null), Track("B", 2, year: null)], []);

        Assert.That(plan.NewFiles[0].RelativePath, Is.EqualTo(@"Queen\Hot Space\01. A.m4a"));
    }

    [Test]
    public void Track_number_is_omitted_when_unknown()
    {
        var plan = PlacementPlanner.Plan([Track("A"), Track("B", 2)], []);

        Assert.That(plan.NewFiles[0].RelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\A.m4a"));
    }

    [Test]
    public void Album_identity_ignores_case_width_and_extra_whitespace()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11, album: "Hot  Space ")],
            [File("Queen - Staying Power.m4a", "Staying Power", 1, album: "hot space", albumArtist: "ＱＵＥＥＮ")]);

        Assert.That(plan.Moves, Has.Count.EqualTo(1));
    }

    [Test]
    public void Album_identity_includes_the_album_artist()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Intro", 1, album: "Greatest Hits", artist: "Queen")],
            [File("ABBA - Waterloo.m4a", "Waterloo", 1, album: "Greatest Hits", albumArtist: "ABBA", artist: "ABBA")]);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo("Queen - Intro.m4a"));
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void Existing_file_without_album_artist_falls_back_to_its_artist()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File("Queen - Staying Power.m4a", "Staying Power", 1, albumArtist: "")]);

        Assert.That(plan.Moves, Has.Count.EqualTo(1));
    }

    [Test]
    public void Multiple_album_artists_match_the_joined_album_artist_tag()
    {
        var plan = PlacementPlanner.Plan(
            [Track("B", 2, album: "Duets", artist: "Kiki Dee", albumArtists: ["Elton John", "Kiki Dee"])],
            [File("Elton John - A.m4a", "A", 1, album: "Duets", albumArtist: "Elton John, Kiki Dee", artist: "Elton John")]);

        Assert.That(Moves(plan), Is.EqualTo(new[] { ("Elton John - A.m4a", @"Elton John\Duets (1982)\01. A.m4a") }));
        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo(@"Elton John\Duets (1982)\02. B.m4a"));
    }

    [Test]
    public void Tracks_without_an_album_always_go_to_the_root()
    {
        var plan = PlacementPlanner.Plan([Track("A", 1, album: ""), Track("B", 2, album: "")], [File("Queen - C.m4a", "C", 3, album: "")]);

        Assert.That(plan.NewFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { "Queen - A.m4a", "Queen - B.m4a" }));
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void Taken_names_get_a_numbered_suffix()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Song", 1, album: "One", id: "1"), Track("Song", 1, album: "Two", id: "2"), Track("Other", album: "Three", id: "3")],
            [File("queen - song.m4a", "Song", 1, album: "Zero")],
            otherOccupiedPaths: ["Queen - Other.m4a"]);

        Assert.That(Destinations(plan), Is.EqualTo(new Dictionary<string, string>
        {
            ["1"] = "Queen - Song (2).m4a",
            ["2"] = "Queen - Song (3).m4a",
            ["3"] = "Queen - Other (2).m4a",
        }));
    }

    [Test]
    public void Moved_root_file_never_overwrites_a_file_in_the_album_folder()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Under Pressure", 11)],
            [File(@"Hot Space\01. Staying Power.m4a", "Staying Power", 1), File("Queen - Staying Power.m4a", "Staying Power", 1)]);

        Assert.That(Moves(plan), Is.EqualTo(new[] { ("Queen - Staying Power.m4a", @"Hot Space\01. Staying Power (2).m4a") }));
    }

    [Test]
    public void New_album_folder_does_not_merge_into_another_albums_folder()
    {
        var plan = PlacementPlanner.Plan(
            [Track("A", 1, album: "Hits?"), Track("B", 2, album: "Hits?")],
            [File(@"Queen\Hits_ (1982)\01. X.m4a", "X", 1, album: "Hits_")]);

        Assert.That(plan.NewFiles[0].RelativePath, Is.EqualTo(@"Queen\Hits_ (1982) (2)\01. A.m4a"));
    }

    [Test]
    public void Moved_file_without_a_title_keeps_its_file_name()
    {
        var plan = PlacementPlanner.Plan([Track("B", 2)], [File("track01.MP3", "", 1)]);

        Assert.That(Moves(plan), Is.EqualTo(new[] { ("track01.MP3", @"Queen\Hot Space (1982)\01. track01.MP3") }));
    }

    [Test]
    public void Names_are_sanitized()
    {
        var plan = PlacementPlanner.Plan(
            [Track("What?: \"Live\"...", 1, album: "AC/DC Live.", artist: "AC/DC "), Track("Con", 2, album: "AC/DC Live.", artist: "AC/DC ")],
            []);

        Assert.That(plan.NewFiles.Select(f => f.RelativePath), Is.EqualTo(new[]
        {
            @"AC_DC\AC_DC Live (1982)\01. What__ _Live_.m4a",
            @"AC_DC\AC_DC Live (1982)\02. Con.m4a",
        }));
    }

    [Test]
    public void Root_name_that_is_a_reserved_device_name_is_escaped()
    {
        var plan = PlacementPlanner.Plan([Track("Song", album: "", artist: "") with { Title = "NUL" }], []);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo("_NUL.m4a"));
    }

    [Test]
    public void Long_names_are_shortened_to_fit_the_path_limit()
    {
        var longAlbum = new string('a', 300);
        var longTitle = new string('t', 300);
        var plan = PlacementPlanner.Plan([Track(longTitle, 1, album: longAlbum), Track("B", 2, album: longAlbum)], [], maxRelativePathLength: 120);

        var path = plan.NewFiles[0].RelativePath;
        Assert.That(path, Has.Length.LessThanOrEqualTo(120));
        Assert.That(path, Does.StartWith(@"Queen\aaaaaaaa"));
        Assert.That(path, Does.Contain(" (1982)\\01. tttttttt"));
        Assert.That(path, Does.EndWith("t.m4a"));
    }

    [Test]
    public void Album_folder_reaching_track_100_widens_existing_numbers()
    {
        var plan = PlacementPlanner.Plan(
            [Track("Finale", 100)],
            [
                File(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "Staying Power", 1),
                File(@"Queen\Hot Space (1982)\99. Encore.m4a", "Encore", 99),
                File(@"Queen\Hot Space (1982)\cover.mp3", "Bonus", null),
                File("Queen - Dancer.m4a", "Dancer", 2),
            ]);

        Assert.That(Moves(plan), Is.EqualTo(new[]
        {
            (@"Queen\Hot Space (1982)\01. Staying Power.m4a", @"Queen\Hot Space (1982)\001. Staying Power.m4a"),
            (@"Queen\Hot Space (1982)\99. Encore.m4a", @"Queen\Hot Space (1982)\099. Encore.m4a"),
            ("Queen - Dancer.m4a", @"Queen\Hot Space (1982)\002. Dancer.m4a"),
        }));
        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\100. Finale.m4a"));
    }

    [Test]
    public void Album_folder_at_track_99_keeps_2_digits()
    {
        var plan = PlacementPlanner.Plan([Track("Last", 99)], [File(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "Staying Power", 1)]);

        Assert.That(plan.Moves, Is.Empty);
        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\99. Last.m4a"));
    }

    [Test]
    public void Album_song_only_in_a_playlist_folder_does_not_make_it_the_album_folder()
    {
        // Corner case 1, probe P1: the album's only other song on the player is in the "Road trip" playlist folder.
        var plan = PlacementPlanner.Plan(
            [Track("Staying Power", 1), Track("Dancer", 2)],
            [File(@"Road trip\03. Queen - Back Chat.m4a", "Back Chat", 3)],
            playlistFolders: ["Road trip"]);

        Assert.That(plan.NewFiles.Select(f => f.RelativePath), Is.EqualTo(new[]
        {
            @"Queen\Hot Space (1982)\01. Staying Power.m4a",
            @"Queen\Hot Space (1982)\02. Dancer.m4a",
        }));
        Assert.That(plan.Moves, Is.Empty, "Playlist files are never moved or renamed.");
    }

    [Test]
    public void Single_new_album_song_goes_to_the_root_when_the_others_are_only_in_playlists()
    {
        var plan = PlacementPlanner.Plan([Track("Dancer", 2)], [File(@"Road trip\03. Queen - Back Chat.m4a", "Back Chat", 3)], playlistFolders: ["Road trip"]);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo("Queen - Dancer.m4a"));
    }

    [Test]
    public void Album_in_its_own_folder_and_a_playlist_folder_adds_to_its_own_folder()
    {
        // Corner case 2, probe P2: new tracks went to the root because the album seemed to span two folders.
        var plan = PlacementPlanner.Plan(
            [Track("Dancer", 2)],
            [File(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "Staying Power", 1), File(@"Liked Songs\07. Queen - Staying Power.m4a", "Staying Power", 1)],
            playlistFolders: ["Liked Songs"]);

        Assert.That(plan.NewFiles.Single().RelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\02. Dancer.m4a"));
        Assert.That(plan.Moves, Is.Empty);
    }

    [Test]
    public void New_album_folder_is_never_put_inside_a_playlist_folder()
    {
        var plan = PlacementPlanner.Plan([Track("A", 1), Track("B", 2)], [], playlistFolders: ["queen"]);

        Assert.That(plan.NewFiles[0].RelativePath, Is.EqualTo(@"Queen (2)\Hot Space (1982)\01. A.m4a"));
    }

    [Test]
    public void Compilation_tracks_share_a_various_artists_folder()
    {
        // Corner case 3, probes P3 and P9: each track of a compilation went to the root as its own one-song album.
        var tracks = new[]
        {
            new PlacementTrack("1", "Hooked on a Feeling", "Blue Swede", ["Various Artists"], "Awesome Mix Vol. 1", 2014, 1, ".m4a"),
            new PlacementTrack("2", "Come and Get Your Love", "Redbone", ["Various Artists"], "Awesome Mix Vol. 1", 2014, 3, ".m4a"),
        };

        var plan = PlacementPlanner.Plan(tracks, []);

        Assert.That(plan.NewFiles.Select(f => f.RelativePath), Is.EqualTo(new[]
        {
            @"Various Artists\Awesome Mix Vol. 1 (2014)\01. Hooked on a Feeling.m4a",
            @"Various Artists\Awesome Mix Vol. 1 (2014)\03. Come and Get Your Love.m4a",
        }));
    }

    [Test]
    public void Max_relative_path_length_accounts_for_the_player_root()
    {
        Assert.That(PlacementPlanner.MaxRelativePathLengthFor(@"E:\"), Is.EqualTo(PlayerPathNames.DefaultMaxRelativePathLength));
        Assert.That(PlacementPlanner.MaxRelativePathLengthFor(@"E:\" + new string('m', 100)), Is.EqualTo(259 - 103 - 1));
    }
}

public class PlayerPathNamesTests
{
    [TestCase("AC/DC", "AC_DC")]
    [TestCase("a\\b:c*d?e\"f<g>h|i", "a_b_c_d_e_f_g_h_i")]
    [TestCase("tab\there", "tab here")]
    [TestCase("bell\u0007", "bell_")]
    [TestCase("  Vol. 1...  ", "Vol. 1")]
    [TestCase("...", "_")]
    [TestCase("", "_")]
    [TestCase("con", "_con")]
    [TestCase("COM1.txt", "_COM1.txt")]
    [TestCase("Lpt9 ", "_Lpt9")]
    [TestCase("Console", "Console")]
    [TestCase("Cafe\u0301", "Café")]
    public void Sanitizes_components(string name, string expected)
    {
        Assert.That(PlayerPathNames.SanitizeComponent(name), Is.EqualTo(expected));
    }

    [Test]
    public void Limits_component_length_without_splitting_surrogate_pairs()
    {
        var name = new string('a', 254) + "😀😀";

        var sanitized = PlayerPathNames.SanitizeComponent(name);

        Assert.That(sanitized, Is.EqualTo(new string('a', 254)));
    }

    [Test]
    public void Fixed_pieces_are_never_shortened()
    {
        var path = PlayerPathNames.Join([new PathPiece(new string('f', 50), IsFixed: true), new PathPiece(new string('t', 50), ".m4a")], maxLength: 70);

        Assert.That(path, Is.EqualTo(new string('f', 50) + @"\" + new string('t', 15) + ".m4a"));
    }
}
