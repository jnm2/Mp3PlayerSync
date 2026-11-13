using System.Globalization;
using System.Text;
using Mp3PlayerSync.Core.Tagging;

namespace Mp3PlayerSync.Core.Placement;

/// <summary>A track that this sync will add to the player.</summary>
/// <param name="Id">The caller's identifier for the track (e.g. its Spotify URI), echoed back in the plan.</param>
/// <param name="Artist">The track's primary (first) artist, used for root file names.</param>
/// <param name="AlbumArtists">
/// The album's artists. Joined with ", " they must match the album artist tag the file gets (as <see cref="TrackTagWriter"/> writes it),
/// so the album is recognized on later syncs. The first one names the album's folder.
/// </param>
/// <param name="Extension">The file extension including the dot, e.g. ".m4a".</param>
public sealed record PlacementTrack(
    string Id,
    string Title,
    string Artist,
    IReadOnlyList<string> AlbumArtists,
    string Album,
    int? Year,
    int? TrackNumber,
    string Extension)
{
    public static PlacementTrack FromMetadata(string id, TrackMetadata metadata, string extension) =>
        new(id, metadata.Title, metadata.PrimaryArtist, metadata.AlbumArtists, metadata.Album, metadata.Year, metadata.TrackNumber, extension);
}

/// <summary>An audio file already on the player, as read from its tags.</summary>
/// <param name="RelativePath">The path relative to the player root, e.g. @"Queen\Hot Space (1982)\11. Under Pressure.m4a".</param>
public sealed record PlayerFile(
    string RelativePath,
    string AlbumArtist,
    string Album,
    int? TrackNumber,
    string Title,
    string Artist);

public sealed record PlannedNewFile(PlacementTrack Track, string RelativePath);

public sealed record PlannedMove(PlayerFile File, string FromRelativePath, string ToRelativePath);

public sealed record PlacementPlan(IReadOnlyList<PlannedMove> Moves, IReadOnlyList<PlannedNewFile> NewFiles);

/// <summary>
/// Decides where synced tracks go on the player, and which root files move into album folders.
/// </summary>
/// <remarks>
/// <para>For each album with at least one track in this sync (album identity: album artist + album name, normalized):</para>
/// <list type="bullet">
/// <item>If the album's existing non-root files are all in one folder, new tracks go there, and the album's root files move there.</item>
/// <item>If they're spread over several folders, new tracks go to the root and nothing moves.</item>
/// <item>Otherwise, if the album would have 2 or more files on the player, new tracks go in
/// <c>&lt;album artist&gt;\&lt;album&gt; (&lt;year&gt;)\</c> and the album's root files move there.</item>
/// <item>Otherwise the track goes to the root.</item>
/// </list>
/// <para>Root files are named <c>&lt;artist&gt; - &lt;title&gt;.&lt;ext&gt;</c>; album folder files <c>&lt;nn&gt;. &lt;title&gt;.&lt;ext&gt;</c>,
/// where &lt;nn&gt; is the track number (its position in Spotify's album order; see <see cref="Tagging.TrackMetadata.FromAlbum"/>).
/// &lt;nn&gt; has 2 digits, or 3 once the folder has a number of 100 or more (and so on); existing numbered files in the
/// folder are then renamed to the wider number. Other existing non-root files never move, and names that are taken
/// get a " (2)", " (3)", … suffix.</para>
/// <para>This is the layout for albums (<see cref="UsesAlbumLayout"/>). Playlists use <see cref="PlaylistPlanner"/>.</para>
/// <para>Pass tracks that are already on the player as <see cref="PlayerFile"/>s, not as tracks to sync.</para>
/// </remarks>
public static class PlacementPlanner
{
    /// <param name="playlistFolders">
    /// The player's playlist folders (relative paths, e.g. from <see cref="PlaylistFolders.FindPlaylistFolders"/>). Files in
    /// them belong to their playlists: they never make a folder an album's folder, never count toward an album, and
    /// are never moved or renamed. New album folders are never put inside one.
    /// </param>
    public static PlacementPlan Plan(
        IReadOnlyList<PlacementTrack> tracks,
        IReadOnlyList<PlayerFile> existingFiles,
        IEnumerable<string>? otherOccupiedPaths = null,
        int maxRelativePathLength = PlayerPathNames.DefaultMaxRelativePathLength,
        IEnumerable<string>? playlistFolders = null)
    {
        var playlistFolderSet = new HashSet<string>((playlistFolders ?? []).Select(PlayerPathNames.NormalizeRelativePath).Where(f => f.Length > 0), StringComparer.OrdinalIgnoreCase);
        bool IsInPlaylistFolder(string relativePath) =>
            playlistFolderSet.Count > 0
            && relativePath.Split(PlayerPathNames.Separator) is var parts
            && Enumerable.Range(1, parts.Length - 1).Any(n => playlistFolderSet.Contains(string.Join(PlayerPathNames.Separator, parts.Take(n))));

        var allFiles = existingFiles
            .Select(f => f with { RelativePath = PlayerPathNames.NormalizeRelativePath(f.RelativePath) })
            .ToList();
        var files = allFiles.Where(f => !IsInPlaylistFolder(f.RelativePath)).ToList();

        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allFiles.Select(f => f.RelativePath).Concat((otherOccupiedPaths ?? []).Select(PlayerPathNames.NormalizeRelativePath)))
            occupied.Add(path);

        // Which albums each existing folder holds, so a new album folder doesn't merge into another album's folder.
        var folderAlbums = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var folder = PlayerPathNames.GetFolder(file.RelativePath);
            if (folder.Length == 0) continue;
            if (!folderAlbums.TryGetValue(folder, out var albums)) folderAlbums[folder] = albums = [];
            albums.Add(AlbumKey(file) ?? "");
        }

        var filesByAlbum = files
            .Where(f => AlbumKey(f) is not null)
            .ToLookup(f => AlbumKey(f)!);

        var moves = new List<PlannedMove>();
        var destinations = new Dictionary<PlacementTrack, string>(ReferenceEqualityComparer.Instance);

        string Reserve(IReadOnlyList<PathPiece> folder, string stem, string extension)
        {
            for (var copy = 1; ; copy++)
            {
                var suffix = (copy > 1 ? string.Create(CultureInfo.InvariantCulture, $" ({copy})") : "") + extension;
                var path = PlayerPathNames.Join([.. folder, new PathPiece(stem, suffix)], maxRelativePathLength);
                if (occupied.Add(path)) return path;
            }
        }

        // A track without an album is its own group.
        foreach (var group in tracks.Select((t, i) => (Track: t, Key: AlbumKey(t) ?? $"\u0000{i}")).GroupBy(x => x.Key, x => x.Track, StringComparer.Ordinal))
        {
            var key = group.Key;
            var albumTracks = group.ToList();
            var existing = filesByAlbum[key].ToList();
            var rootFiles = existing.Where(f => PlayerPathNames.GetFolder(f.RelativePath).Length == 0).ToList();
            var existingFolders = existing
                .Select(f => PlayerPathNames.GetFolder(f.RelativePath))
                .Where(folder => folder.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            IReadOnlyList<PathPiece>? folder = existingFolders.Count switch
            {
                1 => existingFolders[0].Split(PlayerPathNames.Separator).Select(part => new PathPiece(part, IsFixed: true)).ToList(),
                0 when rootFiles.Count + albumTracks.Count >= 2 => NewAlbumFolder(albumTracks, key, folderAlbums, playlistFolderSet, maxRelativePathLength),
                _ => null,
            };

            if (folder is null)
            {
                foreach (var track in albumTracks)
                {
                    var title = TitleOrDefault(track.Title);
                    var stem = track.Artist.Trim().Length > 0 ? $"{track.Artist.Trim()} - {title}" : title;
                    destinations[track] = Reserve([], PlayerPathNames.SanitizeComponent(stem), track.Extension);
                }

                continue;
            }

            var folderPath = string.Join(PlayerPathNames.Separator, folder.Select(p => p.Text));
            var folderFiles = files.Where(f => string.Equals(PlayerPathNames.GetFolder(f.RelativePath), folderPath, StringComparison.OrdinalIgnoreCase)).ToList();
            var width = TrackNumbering.WidthFor(folderFiles.Select(f => TrackNumbering.Parse(Path.GetFileName(f.RelativePath)).Number ?? 0)
                .Concat(rootFiles.Select(f => f.TrackNumber ?? 0))
                .Concat(albumTracks.Select(t => t.TrackNumber ?? 0))
                .DefaultIfEmpty(0)
                .Max());

            // Numbered files written with fewer digits than the folder now needs are widened, so the folder keeps sorting in order.
            foreach (var file in folderFiles)
            {
                var name = Path.GetFileName(file.RelativePath);
                if (TrackNumbering.Parse(name) is not { Number: { } number, Digits: var digits } || digits >= width) continue;

                occupied.Remove(file.RelativePath);
                var widened = TrackNumbering.WithNumber(name, number, width);
                moves.Add(new PlannedMove(file, file.RelativePath, Reserve(folder, Path.GetFileNameWithoutExtension(widened), Path.GetExtension(widened))));
            }

            foreach (var file in rootFiles)
            {
                var originalStem = Path.GetFileNameWithoutExtension(file.RelativePath);
                var stem = AlbumFileStem(file.TrackNumber, file.Title.Trim().Length > 0 ? file.Title : originalStem, width);
                var to = Reserve(folder, stem, Path.GetExtension(file.RelativePath));
                moves.Add(new PlannedMove(file, file.RelativePath, to));
            }

            foreach (var track in albumTracks)
                destinations[track] = Reserve(folder, AlbumFileStem(track.TrackNumber, TitleOrDefault(track.Title), width), track.Extension);
        }

        return new PlacementPlan(moves, tracks.Select(t => new PlannedNewFile(t, destinations[t])).ToList());
    }

    /// <summary>
    /// Whether a library item is placed by album (this planner) rather than into a playlist folder (<see cref="PlaylistPlanner"/>).
    /// </summary>
    public static bool UsesAlbumLayout(LibraryItemKind kind) => kind is LibraryItemKind.SavedAlbum or LibraryItemKind.AlbumPlaylist;

    /// <summary>
    /// The longest relative path that fits under Windows' 259-character limit below <paramref name="playerRoot"/>.
    /// </summary>
    public static int MaxRelativePathLengthFor(string playerRoot) =>
        Math.Min(PlayerPathNames.DefaultMaxRelativePathLength, 259 - Path.TrimEndingDirectorySeparator(Path.GetFullPath(playerRoot)).Length - 1);

    private static List<PathPiece> NewAlbumFolder(
        List<PlacementTrack> albumTracks,
        string albumKey,
        Dictionary<string, HashSet<string>> folderAlbums,
        HashSet<string> playlistFolders,
        int maxRelativePathLength)
    {
        var first = albumTracks[0];
        var artist = first.AlbumArtists.FirstOrDefault(a => a.Trim().Length > 0) ?? first.Artist;
        var year = albumTracks.Select(t => t.Year).FirstOrDefault(y => y is > 0);
        var yearSuffix = year is { } y ? string.Create(CultureInfo.InvariantCulture, $" ({y})") : "";

        var artistText = PlayerPathNames.SanitizeComponent(artist, "Unknown Artist");
        var albumText = PlayerPathNames.SanitizeComponent(first.Album, "Unknown Album");

        // Leaves room for a reasonable file name inside the folder.
        var fileNameAllowance = new PathPiece(new string('x', 40), IsFixed: true);

        // An artist folder named like a playlist folder would put the album inside the playlist.
        var artistPiece = new PathPiece(artistText);
        for (var copy = 2; playlistFolders.Contains(PlayerPathNames.Join([artistPiece, new PathPiece(albumText, yearSuffix), fileNameAllowance], maxRelativePathLength).Split(PlayerPathNames.Separator)[0]); copy++)
            artistPiece = new PathPiece(artistText, string.Create(CultureInfo.InvariantCulture, $" ({copy})"));

        for (var copy = 1; ; copy++)
        {
            var suffix = yearSuffix + (copy > 1 ? string.Create(CultureInfo.InvariantCulture, $" ({copy})") : "");
            var joined = PlayerPathNames.Join([artistPiece, new PathPiece(albumText, suffix), fileNameAllowance], maxRelativePathLength);
            var folder = PlayerPathNames.GetFolder(joined);

            if (!folderAlbums.TryGetValue(folder, out var albums)) folderAlbums[folder] = albums = [];
            if (albums.Count == 0 || albums.All(a => a == albumKey))
            {
                albums.Add(albumKey);
                return folder.Split(PlayerPathNames.Separator).Select(part => new PathPiece(part, IsFixed: true)).ToList();
            }
        }
    }

    private static string AlbumFileStem(int? trackNumber, string title, int width) =>
        PlayerPathNames.SanitizeComponent(trackNumber is > 0
            ? $"{TrackNumbering.Format(trackNumber.Value, width)}. {TitleOrDefault(title)}"
            : TitleOrDefault(title));

    private static string TitleOrDefault(string title) => title.Trim().Length > 0 ? title.Trim() : "Unknown Track";

    private static string? AlbumKey(PlacementTrack track)
    {
        var albumArtist = track.AlbumArtists.Count > 0 ? string.Join(", ", track.AlbumArtists) : track.Artist;
        return AlbumKey(albumArtist, track.Album);
    }

    private static string? AlbumKey(PlayerFile file) =>
        AlbumKey(file.AlbumArtist.Trim().Length > 0 ? file.AlbumArtist : file.Artist, file.Album);

    /// <summary>Album identity: album artist + album name, compared case-, width- and whitespace-insensitively.</summary>
    public static string? AlbumKey(string albumArtist, string album)
    {
        var normalizedAlbum = NormalizeForIdentity(album);
        return normalizedAlbum.Length == 0 ? null : NormalizeForIdentity(albumArtist) + "\u0001" + normalizedAlbum;
    }

    private static string NormalizeForIdentity(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}
