using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mp3PlayerSync.MockExportify;

internal sealed record MockPlaylist(string Id, string Name, string Owner, int Total, string? Cover = null, bool Fails = false, int NewSinceLastSync = 0);

internal sealed record MockAlbum(string Id, string Name, string Artist, string Release, DateTimeOffset Added, int Total);

internal sealed record MockTrack(string Id, string Name, IReadOnlyList<string> Artists, string Album, string Release, int DurationMs, bool Explicit, string Isrc);

/// <summary>
/// The Spotify library the mock lists and exports: Liked Songs, the playlists and the saved albums, and the tracks in
/// each. The test library is what the end-to-end tests check against; the demo library is a believable one for the
/// README's screenshots.
/// </summary>
internal abstract class MockLibrary
{
    public const string UserId = "mockuser";
    public const string SomeoneElse = "someoneelse";

    public abstract IReadOnlyList<MockPlaylist> Playlists { get; }

    public abstract int LikedTotal { get; }

    /// <summary>The saved albums, most recently saved first.</summary>
    public abstract IReadOnlyList<MockAlbum> Albums { get; }

    /// <summary>The text the page adds to its error element with every export, or null for none.</summary>
    public abstract string? ExportWarning { get; }

    protected abstract MockTrack Track(string listId, int index);

    protected abstract DateTimeOffset AddedAt(MockPlaylist playlist, int index);

    /// <summary>
    /// The CSV for Liked Songs (<c>liked</c>), the saved albums (<c>albums</c>) or a playlist, with the columns the app
    /// reads, or null when there's no such list.
    /// </summary>
    public string? ExportCsv(string listId)
    {
        var rows = new List<(MockTrack Track, DateTimeOffset AddedAt)>();
        if (listId == "liked")
        {
            for (var i = 0; i < LikedTotal; i++)
                rows.Add((Track("liked", i), new DateTimeOffset(2024, 12, 24, 0, 0, 0, TimeSpan.Zero).AddDays(-i)));
        }
        else if (listId == "albums")
        {
            foreach (var album in Albums)
            {
                for (var i = 0; i < album.Total; i++)
                    rows.Add((Track(album.Id, i) with { Artists = [album.Artist], Album = album.Name, Release = album.Release }, album.Added));
            }
        }
        else if (Playlists.FirstOrDefault(p => p.Id == listId) is { } playlist)
        {
            for (var i = 0; i < playlist.Total; i++)
                rows.Add((Track(playlist.Id, i), AddedAt(playlist, i)));
        }
        else
        {
            return null;
        }

        var csv = new StringBuilder("Track URI,Track Name,Artist Name(s),Album Name,Release Date,Duration (ms),Explicit,ISRC,Added By,Added At\n");
        foreach (var (track, addedAt) in rows)
        {
            csv.AppendJoin(',', [
                "spotify:track:" + track.Id, Quote(track.Name), Quote(string.Join(';', track.Artists)), Quote(track.Album), track.Release,
                track.DurationMs.ToString(CultureInfo.InvariantCulture), track.Explicit ? "true" : "false", track.Isrc, UserId,
                addedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)]);
            csv.Append('\n');
        }
        return csv.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    public static MockLibrary Test { get; } = new TestLibrary();

    public static MockLibrary LoadDemo(string jsonPath) =>
        new DemoLibrary(JsonSerializer.Deserialize<DemoLibraryFile>(File.ReadAllText(jsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{jsonPath} is empty."));

    /// <summary>
    /// What the end-to-end tests expect, down to the counts, names, durations and covers they check.
    /// </summary>
    private sealed class TestLibrary : MockLibrary
    {
        public override IReadOnlyList<MockPlaylist> Playlists { get; } = CreatePlaylists();

        public override int LikedTotal => 7;

        public override IReadOnlyList<MockAlbum> Albums { get; } =
        [
            new("albumone", "Mock Album One", "The Mockers", "2019-04-05", new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), 3),
            new("bigalbum", "Big Album", "Prolific Band", "2010-10-10", new(2025, 1, 15, 0, 0, 0, TimeSpan.Zero), 60),
            new("oldalbum", "Old Album", "Earth, Wind & Fire", "1975-01-01", new(2023, 5, 5, 0, 0, 0, TimeSpan.Zero), 2),
        ];

        public override string ExportWarning => "Spotify refused a request for some track details (HTTP 403).";

        private static List<MockPlaylist> CreatePlaylists()
        {
            List<MockPlaylist> playlists =
            [
                new("roadtrip", "Road Trip, Vol. 2", UserId, 250),
                new("cafe", "Café ☕ \"Quotes\"", UserId, 3),
                new("broken", "Broken", UserId, 5, Fails: true),
            ];
            // Covers as Spotify makes them: an album's cover for a playlist of fewer than four albums, a mosaic for
            // more, or an uploaded picture. Playlist 5 looks like an album by its cover but has tracks from four albums.
            var covers = new Dictionary<int, string>
            {
                [5] = "https://i.scdn.co/image/ab67616d0000b273000000000000000000000005",
                [14] = "https://i.scdn.co/image/ab67616d0000b273000000000000000000000014",
                [8] = "https://mosaic.scdn.co/640/ab67616d0000b273aaaaab67616d0000b273bbbbab67616d0000b273ccccab67616d0000b273dddd",
                [10] = "https://image-cdn-ak.spotifycdn.com/image/ab67706c0000da84000000000000000000000010",
            };
            for (var n = 4; n <= 60; n++)
                playlists.Add(new("p" + n, "Playlist " + n, n % 3 == 0 ? SomeoneElse : UserId, n % 7 + 1, covers.GetValueOrDefault(n)));
            return playlists;
        }

        protected override MockTrack Track(string listId, int index) => new(
            Id: listId + "t" + index,
            Name: $"{listId} song {index + 1}",
            Artists: index % 5 == 0 ? ["Earth, Wind & Fire", "Chaka Khan"] : ["Artist " + index % 10],
            Album: "Album " + index % 4,
            Release: "2001-01-01",
            DurationMs: 180000 + index * 1000,
            Explicit: index % 3 == 0,
            Isrc: "ISRC" + index);

        protected override DateTimeOffset AddedAt(MockPlaylist playlist, int index) => new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed record DemoLibraryFile(
        string[][] Artists, string[] Titles, DemoPlaylist[] Playlists, int LikedTotal, DemoAlbum[] Albums);

    private sealed record DemoPlaylist(string Id, string Name, int Total, int NewSinceLastSync, bool Saved);

    private sealed record DemoAlbum(string Id, string Name, string Artist, string Release, DateTimeOffset Added, int Total);

    /// <summary>
    /// The made-up library in DemoLibrary.json. Track i of a list has title (seed + 5i) and artist (seed + 7i), where
    /// seed is a hash of the list's ID, as tests\Update-ReadmeScreenshots.ps1 works out too.
    /// </summary>
    private sealed class DemoLibrary(DemoLibraryFile file) : MockLibrary
    {
        public override IReadOnlyList<MockPlaylist> Playlists { get; } = file.Playlists
            .Select(p => new MockPlaylist(p.Id, p.Name, p.Saved ? SomeoneElse : UserId, p.Total, $"/demo-cover/{p.Id}.png", NewSinceLastSync: p.NewSinceLastSync))
            .ToList();

        public override int LikedTotal => file.LikedTotal;

        public override IReadOnlyList<MockAlbum> Albums { get; } = file.Albums
            .Select(a => new MockAlbum(a.Id, a.Name, a.Artist, a.Release, a.Added, a.Total))
            .ToList();

        public override string? ExportWarning => null;

        protected override MockTrack Track(string listId, int index)
        {
            var seed = 7u;
            foreach (var c in listId) seed = unchecked(seed * 31 + c);
            var title = (int)(((ulong)seed + (ulong)index * 5) % (ulong)file.Titles.Length);
            var artist = (int)(((ulong)seed + (ulong)index * 7) % (ulong)file.Artists.Length);
            return new(
                Id: listId + "t" + index,
                Name: file.Titles[title],
                Artists: [file.Artists[artist][0]],
                Album: file.Artists[artist][1],
                Release: file.Artists[artist][2],
                DurationMs: 150000 + (title * 7919 + artist * 104729) % 120000,
                Explicit: false,
                // The same song has the same ISRC in every list, so the app finds it elsewhere on the player.
                Isrc: $"DEMO{title}X{artist}");
        }

        // Playlists were built up over a year, and some have songs added since the last sync.
        protected override DateTimeOffset AddedAt(MockPlaylist playlist, int index) =>
            index >= playlist.Total - playlist.NewSinceLastSync
                ? new DateTimeOffset(2025, 5, 2, 0, 0, 0, TimeSpan.Zero).AddDays(index - playlist.Total)
                : new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index * 9);
    }
}
