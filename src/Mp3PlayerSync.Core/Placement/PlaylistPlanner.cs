using System.Globalization;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Core.Placement;

public enum PlaylistTrackState
{
    /// <summary>Already in the playlist folder under its number.</summary>
    InPlace,

    /// <summary>In the playlist folder under another number; renamed to its position.</summary>
    Renumber,

    /// <summary>Not in the playlist folder but elsewhere on the player; that file is copied in.</summary>
    CopyFromPlayer,

    /// <summary>Not on the player; downloaded.</summary>
    Download,
}

/// <param name="Position">The track's 1-based position in the playlist.</param>
/// <param name="TargetRelativePath">Where the track's file will be, relative to the player root.</param>
/// <param name="CurrentRelativePath">The track's file in the playlist folder now (<see cref="PlaylistTrackState.InPlace"/> and <see cref="PlaylistTrackState.Renumber"/>).</param>
/// <param name="CopySourceRelativePath">
/// The file to copy (<see cref="PlaylistTrackState.CopyFromPlayer"/>), as it will be named once the plan's renames are done.
/// </param>
public sealed record PlaylistTrackPlacement(
    int Position,
    ExportedTrack Track,
    PlaylistTrackState State,
    string TargetRelativePath,
    string? CurrentRelativePath = null,
    string? CopySourceRelativePath = null)
{
    /// <summary>The <see cref="PlacementChange.TrackId"/> the executor reports for this track: its position.</summary>
    public string Id => Position.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A file in the playlist folder that isn't (or is no longer) in the playlist.</summary>
/// <param name="RelativePath">Where the file is now.</param>
/// <param name="TargetRelativePath">Where it will be: the same, unless its number is widened or narrowed to the folder's width.</param>
/// <param name="AfterPosition">
/// Where to show it among the playlist's tracks: after the track at this position, or before the first track when 0.
/// It keeps its place relative to the files that were before it in the folder.
/// </param>
/// <param name="Delete">Whether the user chose to clean it up, so the plan deletes it.</param>
public sealed record PlayerOnlyFile(IndexedFile File, string RelativePath, string TargetRelativePath, int AfterPosition, bool Delete);

public sealed record PlannedRename(string FromRelativePath, string ToRelativePath);

/// <summary>One row of the playlist as shown to the user: a playlist track or a player-only file.</summary>
public sealed record PlaylistRow(PlaylistTrackPlacement? Track, PlayerOnlyFile? PlayerOnlyFile);

/// <param name="FolderRelativePath">The playlist's folder, relative to the player root.</param>
/// <param name="Tracks">Every playlist track, in playlist order.</param>
/// <param name="PlayerOnlyFiles">The folder's files that aren't in the playlist, in display order.</param>
/// <param name="Renames">Renumbering, including widening or narrowing numbers when the folder's number width changes.</param>
public sealed record PlaylistPlan(
    string FolderRelativePath,
    IReadOnlyList<PlaylistTrackPlacement> Tracks,
    IReadOnlyList<PlayerOnlyFile> PlayerOnlyFiles,
    IReadOnlyList<PlannedRename> Renames)
{
    public IEnumerable<PlayerOnlyFile> Deletions => PlayerOnlyFiles.Where(f => f.Delete);

    /// <summary>The tracks whose files have to be downloaded.</summary>
    public IEnumerable<PlaylistTrackPlacement> Downloads => Tracks.Where(t => t.State == PlaylistTrackState.Download);

    /// <summary>The playlist's tracks in order, with the player-only files inserted where they were in the folder.</summary>
    public IReadOnlyList<PlaylistRow> Rows
    {
        get
        {
            var byAfter = PlayerOnlyFiles.ToLookup(f => f.AfterPosition);
            var rows = new List<PlaylistRow>(Tracks.Count + PlayerOnlyFiles.Count);
            rows.AddRange(byAfter[0].Select(f => new PlaylistRow(null, f)));
            foreach (var track in Tracks)
            {
                rows.Add(new PlaylistRow(track, null));
                rows.AddRange(byAfter[track.Position].Select(f => new PlaylistRow(null, f)));
            }

            return rows;
        }
    }
}

/// <summary>
/// Plans a playlist's folder on the player: <c>&lt;playlist name&gt;\&lt;nn&gt;. &lt;artist&gt; - &lt;title&gt;.&lt;ext&gt;</c>,
/// where &lt;nn&gt; is the track's position in the playlist.
/// </summary>
/// <remarks>
/// <para>Files directly in the folder are matched to the playlist's tracks with <see cref="PlayerIndexSnapshot.Match(ExportedTrack)"/>
/// (ISRC, then normalized artist and title). Matched files are renumbered to their track's position, keeping the rest
/// of their name. A track with no file in the folder is copied from elsewhere on the player when it's there (matched by
/// ISRC or artist and title), and downloaded otherwise.</para>
/// <para>&lt;nn&gt; has 2 digits, or 3 when the folder has 100 or more entries (and so on); every numbered file in the
/// folder is renamed to that width.</para>
/// <para>Files in the folder that aren't in the playlist are player-only. They're never removed unless the user selects
/// them for cleanup.</para>
/// <para>This is the layout for playlists and Liked Songs (see <see cref="PlacementPlanner.UsesAlbumLayout"/>).</para>
/// </remarks>
public static class PlaylistPlanner
{
    /// <summary>The playlist's folder, relative to the player root.</summary>
    public static string FolderFor(string playlistName, int maxRelativePathLength = PlayerPathNames.DefaultMaxRelativePathLength)
    {
        // Leaves room for a reasonable file name inside the folder.
        var fileNameAllowance = new PathPiece(new string('x', 40), IsFixed: true);
        var joined = PlayerPathNames.Join([new PathPiece(PlayerPathNames.SanitizeComponent(playlistName, "Playlist")), fileNameAllowance], maxRelativePathLength);
        return PlayerPathNames.GetFolder(joined);
    }

    /// <summary>Plans the playlist in the folder named after it, without checking which playlist owns that folder.</summary>
    /// <remarks>Prefer the overload taking a <see cref="PlaylistFolderAssignment"/>, which keeps same-named playlists apart.</remarks>
    /// <param name="tracks">The playlist's tracks, in playlist order.</param>
    /// <param name="playerFiles">Every audio file on the player, as scanned.</param>
    /// <param name="cleanupSelection">The relative paths of the player-only files the user chose to delete.</param>
    /// <param name="downloadExtension">The extension downloaded files will have, including the dot.</param>
    public static PlaylistPlan Plan(
        string playlistName,
        IReadOnlyList<ExportedTrack> tracks,
        string playerRoot,
        IEnumerable<IndexedFile> playerFiles,
        IEnumerable<string>? cleanupSelection = null,
        string downloadExtension = ".m4a",
        int maxRelativePathLength = PlayerPathNames.DefaultMaxRelativePathLength) =>
        PlanIn(FolderFor(playlistName, maxRelativePathLength), tracks, playerRoot, playerFiles, cleanupSelection, downloadExtension, maxRelativePathLength);

    /// <summary>
    /// Plans the playlist in the folder <see cref="PlaylistFolders.Assign(string, string, string, string, int)"/> gave it
    /// (its <see cref="PlaylistFolderAssignment.CurrentRelativePath"/>).
    /// </summary>
    /// <param name="tracks">The playlist's tracks, in playlist order.</param>
    /// <param name="playerFiles">Every audio file on the player, as scanned.</param>
    /// <param name="cleanupSelection">The relative paths of the player-only files the user chose to delete.</param>
    /// <param name="downloadExtension">The extension downloaded files will have, including the dot.</param>
    public static PlaylistPlan Plan(
        PlaylistFolderAssignment folder,
        IReadOnlyList<ExportedTrack> tracks,
        string playerRoot,
        IEnumerable<IndexedFile> playerFiles,
        IEnumerable<string>? cleanupSelection = null,
        string downloadExtension = ".m4a",
        int maxRelativePathLength = PlayerPathNames.DefaultMaxRelativePathLength) =>
        PlanIn(PlayerPathNames.NormalizeRelativePath(folder.CurrentRelativePath), tracks, playerRoot, playerFiles, cleanupSelection, downloadExtension, maxRelativePathLength);

    private static PlaylistPlan PlanIn(
        string folder,
        IReadOnlyList<ExportedTrack> tracks,
        string playerRoot,
        IEnumerable<IndexedFile> playerFiles,
        IEnumerable<string>? cleanupSelection,
        string downloadExtension,
        int maxRelativePathLength)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(playerRoot));
        var folderPieces = folder.Split(PlayerPathNames.Separator).Select(part => new PathPiece(part, IsFixed: true)).ToList();

        var relativePaths = new Dictionary<IndexedFile, string>(ReferenceEqualityComparer.Instance);
        var inFolder = new List<IndexedFile>();
        var outside = new List<IndexedFile>();
        foreach (var file in playerFiles)
        {
            var relative = Path.GetRelativePath(root, file.Path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;

            relative = PlayerPathNames.NormalizeRelativePath(relative);
            relativePaths[file] = relative;
            (string.Equals(PlayerPathNames.GetFolder(relative), folder, StringComparison.OrdinalIgnoreCase) ? inFolder : outside).Add(file);
        }

        string NameOf(IndexedFile file) => Path.GetFileName(relativePaths[file]);
        int? NumberOf(IndexedFile file) => TrackNumbering.Parse(NameOf(file)).Number;

        // Match the folder's files to tracks, keeping files that already have their track's number where they are.
        var folderIndex = new PlayerIndexSnapshot(inFolder);
        var candidates = tracks.Select(t => folderIndex.Match(t).Files).ToList();
        var assigned = new IndexedFile?[tracks.Count];
        var positionOf = new Dictionary<IndexedFile, int>(ReferenceEqualityComparer.Instance);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                if (assigned[i] is not null) continue;
                var file = candidates[i].FirstOrDefault(f => !positionOf.ContainsKey(f) && (pass == 1 || NumberOf(f) == i + 1));
                if (file is null) continue;
                assigned[i] = file;
                positionOf[file] = i + 1;
            }
        }

        var selection = new HashSet<string>((cleanupSelection ?? []).Select(PlayerPathNames.NormalizeRelativePath), StringComparer.OrdinalIgnoreCase);
        var playerOnly = inFolder
            .Where(f => !positionOf.ContainsKey(f))
            .Select(f => (File: f, Delete: selection.Contains(relativePaths[f])))
            .ToList();

        var width = TrackNumbering.WidthFor(Math.Max(
            tracks.Count,
            playerOnly.Where(p => !p.Delete).Select(p => NumberOf(p.File) ?? 0).DefaultIfEmpty(0).Max()));

        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Reserve(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var copy = 1; ; copy++)
            {
                var suffix = (copy > 1 ? string.Create(CultureInfo.InvariantCulture, $" ({copy})") : "") + extension;
                var path = PlayerPathNames.Join([.. folderPieces, new PathPiece(stem, suffix)], maxRelativePathLength);
                if (occupied.Add(path)) return path;
            }
        }

        string PlayerOnlyName(IndexedFile file)
        {
            var name = NameOf(file);
            return TrackNumbering.Parse(name) is { Number: { } number }
                ? TrackNumbering.WithNumber(name, number, width)
                : TrackNumbering.StripTemporaryPrefix(name);
        }

        // Player-only files that keep their name hold on to it; the rest are named after the tracks.
        var finalPaths = new Dictionary<IndexedFile, string>(ReferenceEqualityComparer.Instance);
        foreach (var (file, _) in playerOnly.Where(p => !p.Delete && PlayerOnlyName(p.File) == NameOf(p.File)))
            finalPaths[file] = Reserve(NameOf(file));

        var placements = new PlaylistTrackPlacement?[tracks.Count];
        var renames = new List<PlannedRename>();
        for (var i = 0; i < tracks.Count; i++)
        {
            if (assigned[i] is not { } file) continue;
            var current = relativePaths[file];
            var target = Reserve(TrackNumbering.WithNumber(NameOf(file), i + 1, width));
            finalPaths[file] = target;
            var inPlace = string.Equals(current, target, StringComparison.Ordinal);
            if (!inPlace) renames.Add(new PlannedRename(current, target));
            placements[i] = new PlaylistTrackPlacement(i + 1, tracks[i], inPlace ? PlaylistTrackState.InPlace : PlaylistTrackState.Renumber, target, CurrentRelativePath: current);
        }

        foreach (var (file, _) in playerOnly.Where(p => !p.Delete && !finalPaths.ContainsKey(p.File)))
        {
            finalPaths[file] = Reserve(PlayerOnlyName(file));
            renames.Add(new PlannedRename(relativePaths[file], finalPaths[file]));
        }

        var outsideIndex = new PlayerIndexSnapshot(outside);
        for (var i = 0; i < tracks.Count; i++)
        {
            if (placements[i] is not null) continue;
            var track = tracks[i];

            // A file elsewhere on the player, or (for a track listed twice) the file another position of it gets.
            var source = ReliableMatches(outsideIndex, track).FirstOrDefault()
                ?? ReliableMatches(folderIndex, track).FirstOrDefault(finalPaths.ContainsKey);
            var sourcePath = source is null ? null : finalPaths.GetValueOrDefault(source) ?? relativePaths[source];

            var title = track.Title.Trim().Length > 0 ? track.Title.Trim() : "Unknown Track";
            var artist = track.Artists.FirstOrDefault()?.Trim() ?? "";
            var stem = PlayerPathNames.SanitizeComponent($"{TrackNumbering.Format(i + 1, width)}. {(artist.Length > 0 ? $"{artist} - {title}" : title)}");
            var target = Reserve(stem + (sourcePath is null ? downloadExtension : Path.GetExtension(sourcePath)));

            placements[i] = sourcePath is null
                ? new PlaylistTrackPlacement(i + 1, track, PlaylistTrackState.Download, target)
                : new PlaylistTrackPlacement(i + 1, track, PlaylistTrackState.CopyFromPlayer, target, CopySourceRelativePath: sourcePath);
        }

        // Show each player-only file after the track whose file was before it in the folder.
        var playerOnlyFiles = new List<PlayerOnlyFile>();
        var deleted = playerOnly.Where(p => p.Delete).Select(p => p.File).ToHashSet(ReferenceEqualityComparer.Instance);
        var after = 0;
        foreach (var file in inFolder.OrderBy(f => NumberOf(f) ?? int.MaxValue).ThenBy(NameOf, StringComparer.OrdinalIgnoreCase))
        {
            if (positionOf.TryGetValue(file, out var position))
            {
                after = position;
                continue;
            }

            var isDeleted = deleted.Contains(file);
            playerOnlyFiles.Add(new PlayerOnlyFile(
                file,
                relativePaths[file],
                isDeleted ? relativePaths[file] : finalPaths[file],
                NumberOf(file) is null ? tracks.Count : after,
                isDeleted));
        }

        return new PlaylistPlan(folder, placements.Select(p => p!).ToList(), playerOnlyFiles, renames);
    }

    private static IEnumerable<IndexedFile> ReliableMatches(PlayerIndexSnapshot index, ExportedTrack track) =>
        index.Match(track) is { Kind: TrackMatchKind.Isrc or TrackMatchKind.ArtistAndTitle } match
            ? match.Files.OrderByDescending(f => f.HasTags)
            : [];
}
