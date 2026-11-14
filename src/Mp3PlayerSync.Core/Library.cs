using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core;

public enum LibraryItemKind { Playlist, LikedSongs, AlbumPlaylist, SavedAlbum }

public enum LibraryFilter
{
    // Settings files store the names these tabs had in 0.1.0.
    [JsonStringEnumMemberName("PlaylistsByYou")] CreatedPlaylists,
    [JsonStringEnumMemberName("PlaylistsByOthers")] SavedPlaylists,
    SavedAlbums,
    All,
}

/// <summary>
/// What an earlier export of a playlist showed, remembered so the library can show it without exporting again.
/// </summary>
public sealed record ExportSummary(int TrackCount, long DurationMs, DateTimeOffset? LastAddedAt, bool IsSingleAlbum)
{
    public static ExportSummary From(ExportedPlaylist export) => new(
        export.Tracks.Count,
        (long)export.TotalDuration.TotalMilliseconds,
        export.Tracks.Max(t => t.AddedAt),
        PlaylistCover.IsSingleAlbum(export));
}

public sealed record LibraryItem(
    LibraryItemKind Kind,
    string Key,
    string Name,
    string Owner,
    bool IsOwnedByUser,
    int? TrackCount,
    string? CoverUrl,
    ExportSummary? Export,
    SavedAlbum? Album = null)
{
    /// <summary>An album from Exportify's Saved Albums export, as opposed to a playlist.</summary>
    public bool IsSavedAlbum => Kind == LibraryItemKind.SavedAlbum;

    // Display text, filled in by LibraryBuilder.Present for the selected tab.
    public string Byline { get; init; } = "";
    public string Stats { get; init; } = "";
    public string DateText { get; init; } = "";

    public string Glyph => Kind switch
    {
        LibraryItemKind.LikedSongs => "\uEB52",
        LibraryItemKind.SavedAlbum or LibraryItemKind.AlbumPlaylist => "\uE93C",
        _ => "\uE8FD",
    };

    // Used by XAML item containers as the accessible name.
    public override string ToString() => Name;
}

/// <summary>
/// Turns Exportify's playlist table, and the albums in its Saved Albums export, into one list in Spotify's
/// order: Liked Songs, then playlists as Exportify lists them, then saved albums, most recently saved first.
/// </summary>
public static class LibraryBuilder
{
    public const string LikedSongsUrl = "https://open.spotify.com/collection/tracks";
    public const string SavedAlbumsUrl = "https://open.spotify.com/collection/albums";

    /// <param name="albums">The saved albums, or null when Exportify's Saved Albums export hasn't been read.</param>
    /// <param name="exports">What earlier exports of playlists showed.</param>
    public static List<LibraryItem> Build(
        IReadOnlyList<PlaylistSummary> rows,
        IReadOnlyList<SavedAlbum>? albums,
        IReadOnlyDictionary<string, ExportSummary> exports)
    {
        // Exportify lists Liked Songs as owned by the signed-in user's ID, the same ID it shows for their playlists.
        var userId = rows.FirstOrDefault(r => r.Key == LikedSongsUrl)?.Owner;
        var items = new List<LibraryItem>();

        foreach (var row in rows)
        {
            exports.TryGetValue(row.Key, out var export);

            if (row.Key == LikedSongsUrl)
            {
                items.Add(new LibraryItem(LibraryItemKind.LikedSongs, row.Key, row.Name, row.Owner, true, row.TrackCount, null, export));
            }
            else if (row.Key == SavedAlbumsUrl)
            {
                // Listed as individual albums from its export instead.
            }
            else if (IsSpotifyDj(row))
            {
                // Spotify's DJ is a feature of its app, with no tracks to sync.
            }
            else
            {
                var owned = userId is not null && string.Equals(row.Owner, userId, StringComparison.Ordinal);
                var isAlbum = export?.IsSingleAlbum ?? PlaylistCover.LooksLikeAlbum(row.ImageUrl);
                items.Add(new LibraryItem(isAlbum ? LibraryItemKind.AlbumPlaylist : LibraryItemKind.Playlist,
                    row.Key, row.Name, row.Owner, owned, row.TrackCount, PlaylistCover.RealCover(row.ImageUrl), export));
            }
        }

        foreach (var album in albums ?? [])
        {
            var export = ExportSummary.From(album.ToExportedPlaylist()) with { LastAddedAt = album.AddedAt };
            items.Add(new LibraryItem(LibraryItemKind.SavedAlbum, album.Key, album.Name, "", false, album.Tracks.Count, null, export, album));
        }

        // Liked Songs is special: always first.
        return items.OrderBy(i => i.Kind != LibraryItemKind.LikedSongs).ToList();
    }

    public const string SpotifyDjUrl = "https://open.spotify.com/playlist/37i9dQZF1EYkqdzj48dyYq";

    private static bool IsSpotifyDj(PlaylistSummary row) =>
        Uri.TryCreate(row.Key, UriKind.Absolute, out var link)
            && link.AbsolutePath.TrimEnd('/').EndsWith("/playlist/37i9dQZF1EYkqdzj48dyYq", StringComparison.Ordinal)
        || string.Equals(row.Owner, "spotify", StringComparison.OrdinalIgnoreCase) && row.Name == "DJ";

    /// <summary>The items on a tab, with their display text.</summary>
    public static List<LibraryItem> Present(IEnumerable<LibraryItem> items, LibraryFilter filter, DateTimeOffset now)
    {
        return Filter(items, filter).Select(item => item with
        {
            Byline = item.Kind switch
            {
                LibraryItemKind.SavedAlbum => JoinParts(string.Join(", ", item.Album!.Artists), item.Album.ReleaseDate is { Length: >= 4 } date ? date[..4] : null),
                LibraryItemKind.LikedSongs => "",
                _ => filter != LibraryFilter.CreatedPlaylists && OwnerName.IsShown(item.Owner) ? item.Owner : "",
            },
            // Only what's known for every item alike, not what earlier exports of some playlists showed.
            Stats = TrackCountText(item.TrackCount) ?? "",
            DateText = item.Album?.AddedAt is { } saved ? $"Saved {FormatRelative(saved, now)}" : "",
        }).ToList();
    }

    public static IEnumerable<LibraryItem> Filter(IEnumerable<LibraryItem> items, LibraryFilter filter) => filter switch
    {
        LibraryFilter.CreatedPlaylists => items.Where(i => i.Kind == LibraryItemKind.LikedSongs || i.Kind == LibraryItemKind.Playlist && i.IsOwnedByUser),
        LibraryFilter.SavedPlaylists => items.Where(i => i.Kind == LibraryItemKind.Playlist && !i.IsOwnedByUser),
        LibraryFilter.SavedAlbums => items.Where(i => i.Kind is LibraryItemKind.SavedAlbum or LibraryItemKind.AlbumPlaylist),
        _ => items,
    };

    public static string FormatRelative(DateTimeOffset time, DateTimeOffset now)
    {
        var days = (now.ToLocalTime().Date - time.ToLocalTime().Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => time.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
        };
    }

    private static string JoinParts(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string? TrackCountText(int? count) => count is { } c ? $"{c} {(c == 1 ? "track" : "tracks")}" : null;
}

/// <summary>
/// Decides whether a playlist owner's Spotify user ID is worth showing.
/// </summary>
public static partial class OwnerName
{
    /// <summary>
    /// False for Spotify itself and for IDs Spotify generated, which mean nothing to people: old numeric IDs
    /// (8 or more digits) and newer lowercase alphanumeric ones of exactly 25 or 28 characters.
    /// </summary>
    public static bool IsShown(string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && !owner.Equals("spotify", StringComparison.OrdinalIgnoreCase)
        && !NumericId().IsMatch(owner)
        && !GeneratedId().IsMatch(owner);

    [GeneratedRegex("^[0-9]{8,}$")]
    private static partial Regex NumericId();

    [GeneratedRegex("^(?:[a-z0-9]{25}|[a-z0-9]{28})$")]
    private static partial Regex GeneratedId();
}

/// <summary>
/// Reads what a playlist's cover says about its contents.
/// </summary>
/// <remarks>
/// Without an uploaded cover, Spotify makes a mosaic (mosaic.scdn.co) once a playlist spans four or more albums,
/// and otherwise uses an album cover (image IDs starting ab67616d). Uploaded covers start ab67706c.
/// </remarks>
public static class PlaylistCover
{
    public static bool LooksLikeAlbum(string? imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Host.Equals("mosaic.scdn.co", StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.Segments.LastOrDefault()?.StartsWith("ab67616d", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>The cover to show, or null for Exportify's stand-in when a playlist has no cover.</summary>
    /// <remarks>Covers served over plain HTTP are only shown from this computer, as the mock Exportify serves them.</remarks>
    public static string? RealCover(string? imageUrl) =>
        Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        && !uri.Host.Equals("placehold.co", StringComparison.OrdinalIgnoreCase)
            ? imageUrl
            : null;

    /// <summary>Whether an exported playlist's tracks all come from one album.</summary>
    public static bool IsSingleAlbum(ExportedPlaylist export) =>
        export.Tracks.Count > 0
        && export.Tracks.Select(t => (t.Album, t.ReleaseDate)).Distinct().Count() == 1;
}

/// <summary>
/// A saved album, recovered from Exportify's Saved Albums export, which lists every saved album's tracks.
/// </summary>
public sealed record SavedAlbum(string Key, string Name, string ReleaseDate, DateTimeOffset? AddedAt, IReadOnlyList<ExportedTrack> Tracks)
{
    public const string KeyPrefix = "album:";

    public IReadOnlyList<string> Artists => Tracks.Count > 0 ? Tracks[0].Artists : [];

    public ExportedPlaylist ToExportedPlaylist() =>
        new([], Tracks.Select((track, index) => track with { Number = index + 1 }).ToList());

    /// <summary>
    /// Groups the Saved Albums export by album, keeping the export's order (most recently saved first).
    /// </summary>
    /// <remarks>
    /// Each track's Added At is when its album was saved, so it tells apart albums that share a name.
    /// </remarks>
    public static List<SavedAlbum> FromExport(ExportedPlaylist export)
    {
        return export.Tracks
            .GroupBy(track => (track.AddedAt, track.Album, track.ReleaseDate))
            .Select(group => new SavedAlbum(
                Key: $"{KeyPrefix}{group.Key.AddedAt?.ToUnixTimeSeconds()}:{group.Key.Album}:{group.Key.ReleaseDate}",
                Name: group.Key.Album,
                ReleaseDate: group.Key.ReleaseDate,
                AddedAt: group.Key.AddedAt,
                Tracks: group.ToList()))
            .ToList();
    }
}