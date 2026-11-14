using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class LibraryBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private const string AlbumCover = "https://i.scdn.co/image/ab67616d0000b2731234567890abcdef12345678";
    private const string Mosaic = "https://mosaic.scdn.co/640/ab67616d0000b273aaaaab67616d0000b273bbbb";
    private const string UploadedCover = "https://image-cdn-ak.spotifycdn.com/image/ab67706c0000da84cafecafecafecafecafecafe";
    private const string NoCover = "https://placehold.co/30?text=blank";

    private static readonly PlaylistSummary[] Rows =
    [
        new(LibraryBuilder.LikedSongsUrl, "Liked Songs", "me", 7, LibraryBuilder.LikedSongsUrl, "https://exportify.net/liked_songs.jpeg"),
        new(LibraryBuilder.SavedAlbumsUrl, "Saved Albums", "me", 30, LibraryBuilder.SavedAlbumsUrl, "https://exportify.net/saved_albums.png"),
        new("https://open.spotify.com/playlist/mine", "Mine", "me", 30, null, Mosaic),
        new("https://open.spotify.com/playlist/theirs", "Theirs", "someone", 4, null, UploadedCover),
        new("https://open.spotify.com/playlist/seeker", "Seeker", "me", 11, null, AlbumCover),
        new("https://open.spotify.com/playlist/their-album", "Their album", "someone", 9, null, AlbumCover),
        new("https://open.spotify.com/playlist/empty", "Empty", "me", 0, null, NoCover),
    ];

    private static readonly SavedAlbum[] Albums =
    [
        new("album:2", "New album", "2020-05-01", Now.AddDays(-2), [Track("One", "New album")]),
        new("album:1", "Old album", "1999", Now.AddDays(-400), [Track("Two", "Old album"), Track("Three", "Old album")]),
    ];

    private static readonly Dictionary<string, ExportSummary> NoExports = [];

    private static ExportedTrack Track(string title, string album, string releaseDate = "2000") =>
        new(1, "", title, ["Band"], album, TimeSpan.FromMinutes(3), releaseDate, Now, "", false, "", "");

    private static string[] Names(IEnumerable<LibraryItem> items, LibraryFilter filter) =>
        LibraryBuilder.Present(items, filter, Now).Select(i => i.Name).ToArray();

    [Test]
    public void Splits_playlists_by_owner_and_puts_album_like_playlists_with_saved_albums()
    {
        var items = LibraryBuilder.Build(Rows, Albums, NoExports);

        Names(items, LibraryFilter.CreatedPlaylists).ShouldBe(new[] { "Liked Songs", "Mine", "Empty" });
        Names(items, LibraryFilter.SavedPlaylists).ShouldBe(new[] { "Theirs" });
        Names(items, LibraryFilter.SavedAlbums).ShouldBe(new[] { "Seeker", "Their album", "New album", "Old album" });
        Names(items, LibraryFilter.All).ShouldBe(new[] { "Liked Songs", "Mine", "Theirs", "Seeker", "Their album", "Empty", "New album", "Old album" });
    }

    [Test]
    public void Liked_songs_is_first_and_never_an_album_wherever_Exportify_lists_it()
    {
        PlaylistSummary[] rows =
        [
            new("https://open.spotify.com/playlist/mine", "Mine", "me", 30, null, Mosaic),
            new(LibraryBuilder.LikedSongsUrl, "Liked Songs", "me", 7, LibraryBuilder.LikedSongsUrl, AlbumCover),
        ];
        var exports = new Dictionary<string, ExportSummary> { [LibraryBuilder.LikedSongsUrl] = new(7, 0, null, IsSingleAlbum: true) };

        var items = LibraryBuilder.Build(rows, Albums, exports);

        Names(items, LibraryFilter.CreatedPlaylists).ShouldBe(new[] { "Liked Songs", "Mine" });
        Names(items, LibraryFilter.All)[0].ShouldBe("Liked Songs");
        Names(items, LibraryFilter.SavedAlbums).ShouldNotContain("Liked Songs");
    }

    [Test]
    public void Spotify_DJ_is_hidden_but_not_other_playlists_called_DJ()
    {
        PlaylistSummary[] rows =
        [
            new(LibraryBuilder.SpotifyDjUrl, "DJ", "spotify", 0, null),
            new(LibraryBuilder.SpotifyDjUrl + "?si=abc", "DJ (renamed)", "someone", 0, null),
            new("https://open.spotify.com/playlist/other-dj", "DJ", "Spotify", 0, null),
            new("https://open.spotify.com/playlist/my-dj", "DJ", "me", 12, null, Mosaic),
        ];

        var items = LibraryBuilder.Build(rows, null, NoExports);

        items.Select(i => i.Key).ShouldBe(new[] { "https://open.spotify.com/playlist/my-dj" });
    }
    [Test]
    public void Earlier_exports_override_the_cover()
    {
        var exports = new Dictionary<string, ExportSummary>
        {
            ["https://open.spotify.com/playlist/seeker"] = new(11, 0, null, IsSingleAlbum: false),
            ["https://open.spotify.com/playlist/mine"] = new(30, 0, null, IsSingleAlbum: true),
        };

        var items = LibraryBuilder.Build(Rows, albums: null, exports);

        Names(items, LibraryFilter.SavedAlbums).ShouldBe(new[] { "Mine", "Their album" });
        Names(items, LibraryFilter.CreatedPlaylists).ShouldContain("Seeker");
    }

    [Test]
    public void Owner_is_shown_except_on_created_playlists_tab()
    {
        PlaylistSummary[] rows =
        [
            new(LibraryBuilder.LikedSongsUrl, "Liked Songs", "me", 7, null),
            new("https://open.spotify.com/playlist/mine", "Mine", "me", 30, null, Mosaic),
        ];
        var items = LibraryBuilder.Build(rows, null, NoExports);

        string Byline(LibraryFilter filter) => LibraryBuilder.Present(items, filter, Now).Single(i => i.Name == "Mine").Byline;

        Byline(LibraryFilter.CreatedPlaylists).ShouldBeEmpty();
        Byline(LibraryFilter.All).ShouldBe("me");
        LibraryBuilder.Present(items, LibraryFilter.All, Now)[0].Byline.ShouldBeEmpty(customMessage: "Liked Songs");
    }

    [Test]
    public void Generated_owner_ids_are_hidden()
    {
        PlaylistSummary[] rows = [new("https://open.spotify.com/playlist/x", "X", "7fjct5b53ypygv8hdtcpuwzc2", 3, null, Mosaic)];

        LibraryBuilder.Present(LibraryBuilder.Build(rows, null, NoExports), LibraryFilter.All, Now).ShouldHaveSingleItem().Byline.ShouldBeEmpty();
    }

    [Test]
    public void Playlists_show_only_what_Exportify_lists_even_after_an_export()
    {
        var exports = new Dictionary<string, ExportSummary>
        {
            ["https://open.spotify.com/playlist/mine"] = new(30, (long)TimeSpan.FromMinutes(125).TotalMilliseconds, Now.AddDays(-2), false),
        };
        var items = LibraryBuilder.Present(LibraryBuilder.Build(Rows, null, exports), LibraryFilter.CreatedPlaylists, Now);

        var mine = items.Single(i => i.Name == "Mine");
        (mine.Stats, mine.DateText).ShouldBe(("30 tracks", ""));
    }

    [Test]
    public void Saved_albums_show_artist_year_tracks_and_when_saved()
    {
        var album = LibraryBuilder.Present(LibraryBuilder.Build(Rows, Albums, NoExports), LibraryFilter.SavedAlbums, Now)
            .Single(i => i.Name == "New album");

        (album.Byline, album.Stats, album.DateText).ShouldBe(("Band · 2020", "1 track", "Saved 2 days ago"));
    }

    [Test]
    public void Covers_exclude_Exportify_stand_ins()
    {
        var items = LibraryBuilder.Build(Rows, null, NoExports);

        items.Single(i => i.Name == "Theirs").CoverUrl.ShouldBe(UploadedCover);
        items.Single(i => i.Name == "Empty").CoverUrl.ShouldBeNull();
        items.Single(i => i.Name == "Liked Songs").CoverUrl.ShouldBeNull();
    }

    [Test]
    [Arguments(AlbumCover, true)]
    [Arguments(Mosaic, false)]
    [Arguments(UploadedCover, false)]
    [Arguments(NoCover, false)]
    [Arguments(null, false)]
    public void Cover_tells_whether_a_playlist_looks_like_an_album(string? imageUrl, bool expected)
    {
        PlaylistCover.LooksLikeAlbum(imageUrl).ShouldBe(expected);
    }

    [Test]
    [Arguments(UploadedCover, true)]
    [Arguments("http://localhost:5178/demo-cover/run.png", true)] // The mock's covers
    [Arguments("http://i.scdn.co/image/ab67616d0000b273", false)]
    [Arguments(NoCover, false)]
    public void Covers_are_shown_over_HTTPS_or_from_this_computer(string imageUrl, bool expected)
    {
        (PlaylistCover.RealCover(imageUrl) is not null).ShouldBe(expected);
    }

    [Test]
    public void Single_album_means_every_track_shares_album_and_release()
    {
        PlaylistCover.IsSingleAlbum(new([], [Track("A", "X"), Track("B", "X")])).ShouldBeTrue();
        PlaylistCover.IsSingleAlbum(new([], [Track("A", "X"), Track("B", "Y")])).ShouldBeFalse();
        PlaylistCover.IsSingleAlbum(new([], [Track("A", "X"), Track("B", "X", "2001")])).ShouldBeFalse();
        PlaylistCover.IsSingleAlbum(new([], [])).ShouldBeFalse();
    }
}

public class OwnerNameTests
{
    [Test]
    [Arguments("jnm236", true)]
    [Arguments("someone", true)]
    [Arguments("1234567", true)] // Short numbers are ordinary names
    [Arguments("abc7fjct5b53ypygv8hdtcpuwzc", true)] // 27 characters
    [Arguments("x31xprpmnul6fqtufeje253axvece", true)] // 29 characters
    [Arguments("7FJCT5B53YPYGV8HDTCPUWZC2", true)] // Generated IDs are lowercase
    [Arguments("spotify", false)]
    [Arguments("Spotify", false)]
    [Arguments("12345678", false)]
    [Arguments("1210987654", false)]
    [Arguments("7fjct5b53ypygv8hdtcpuwzc2", false)]
    [Arguments("21geiktnqpsxb7vtwgu5oj3kq", false)]
    [Arguments("whtyar93ro8wciwptbhwn18mh", false)]
    [Arguments("31xprpmnul6fqtufeje253axvece", false)]
    [Arguments("", false)]
    public void Is_shown(string owner, bool expected)
    {
        OwnerName.IsShown(owner).ShouldBe(expected);
    }
}

public class SavedAlbumTests
{
    [Test]
    public void Groups_saved_albums_export_by_album_in_export_order()
    {
        var savedLater = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var savedEarlier = new DateTimeOffset(2023, 5, 5, 0, 0, 0, TimeSpan.Zero);
        ExportedTrack Track(int n, string album, DateTimeOffset saved, string release = "2001") =>
            new(n, "spotify:track:" + n, "Song " + n, ["Band"], album, null, release, saved, "", null, "", "");

        var albums = SavedAlbum.FromExport(new([],
        [
            Track(1, "Greatest Hits", savedLater),
            Track(2, "Greatest Hits", savedLater),
            Track(3, "Greatest Hits", savedEarlier, "1990"), // Same name, a different saved album
        ]));

        albums.Select(a => (a.Name, a.ReleaseDate, a.Tracks.Count)).ShouldBe(new[] { ("Greatest Hits", "2001", 2), ("Greatest Hits", "1990", 1) });
        albums[1].ToExportedPlaylist().Tracks.ShouldHaveSingleItem().Number.ShouldBe(1);
        albums.Select(a => a.Key).ShouldBeUnique();
    }
}

public class AppSettingsTests
{
    [Test]
    [Arguments("PlaylistsByYou", LibraryFilter.CreatedPlaylists)]
    [Arguments("PlaylistsByOthers", LibraryFilter.SavedPlaylists)]
    public void Reads_the_tab_chosen_in_0_1_0_and_keeps_the_player(string stored, LibraryFilter expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $$"""{ "player": { "folderPath": "D:\\Player" }, "filter": "{{stored}}" }""");

            var loaded = AppSettings.Load(path);

            loaded.ShouldSatisfyAllConditions(
                l => l.Filter.ShouldBe(expected),
                l => l.Player.ShouldNotBeNull().FolderPath.ShouldBe(@"D:\Player"));
            loaded.Save(path);
            File.ReadAllText(path).ShouldContain($"\"{stored}\"", Case.Sensitive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Round_trips_choices_and_export_summaries()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        try
        {
            var summary = new ExportSummary(3, 540000, DateTimeOffset.UnixEpoch, IsSingleAlbum: true);
            var settings = new AppSettings { LastSelectedKey = "k", Filter = LibraryFilter.SavedAlbums };
            settings.Exports["p"] = summary;
            settings.Save(path);

            var loaded = AppSettings.Load(path);

            (loaded.LastSelectedKey, loaded.Filter).ShouldBe(("k", LibraryFilter.SavedAlbums));
            loaded.Exports.ShouldBe(new Dictionary<string, ExportSummary> { ["p"] = summary }, ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            AppSettings.Load(path).Filter.ShouldBe(LibraryFilter.CreatedPlaylists);
            AppSettings.Load(path + ".missing").LastSelectedKey.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
