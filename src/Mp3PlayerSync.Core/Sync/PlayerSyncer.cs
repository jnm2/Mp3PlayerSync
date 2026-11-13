using System.Diagnostics;
using System.Globalization;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Scanning;
using Mp3PlayerSync.Core.Tagging;

namespace Mp3PlayerSync.Core.Sync;

public enum TrackSyncState
{
    Queued,
    Searching,
    Downloading,
    Saving,
    Done,
    Failed,

    /// <summary>The track's match can't be used any more (e.g. the video is gone), so the user has to choose again.</summary>
    NeedsReview,
}

public sealed record TrackSyncProgress(ExportedTrack Track, TrackSyncState State, double? Percent = null, string? Message = null);

/// <param name="Kind">Whether this is an album (album layout) or a playlist (its own folder).</param>
/// <param name="Key">The playlist's or album's key (its Spotify URL), which identifies a playlist's folder.</param>
/// <param name="Name">The playlist's or album's name.</param>
/// <param name="Owner">Who the playlist belongs to, which tells apart playlists with the same name.</param>
/// <param name="AllTracks">Every track, in Spotify's order.</param>
/// <param name="Selected">The tracks the user chose to add.</param>
/// <param name="Cleanup">Player-only files in the playlist's folder the user chose to delete, relative to the player root.</param>
/// <param name="Resolutions">
/// What each track to download was resolved to (by reference). Selected tracks that need downloading but aren't
/// <see cref="TrackResolutionState.Resolved"/> here are left out.
/// </param>
public sealed record SyncRequest(
    LibraryItemKind Kind,
    string Key,
    string Name,
    string Owner,
    IReadOnlyList<ExportedTrack> AllTracks,
    IReadOnlyList<ExportedTrack> Selected,
    IReadOnlyList<string> Cleanup,
    IReadOnlyDictionary<ExportedTrack, TrackResolution>? Resolutions = null);

/// <summary>How a sync went. Songs are the request's own tracks (by reference).</summary>
/// <param name="Errors">Problems that aren't about one song, e.g. a full player.</param>
/// <param name="Added">The songs that were put on the player.</param>
/// <param name="Failed">The songs that couldn't be synced, with why.</param>
/// <param name="NeedsReview">The songs whose match can't be used any more, so the user has to choose again.</param>
public sealed record SyncResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<ExportedTrack> Added,
    IReadOnlyList<(ExportedTrack Track, string? Message)> Failed,
    IReadOnlyList<ExportedTrack> NeedsReview);

/// <summary>
/// Puts tracks onto the MP3 player: downloads each resolved track from YouTube Music, tags it from Exportify's
/// data, then places it following the player's layout: albums by the album rules, playlists in their own
/// numbered folder (renumbering, copying from elsewhere on the player, and cleaning up as planned).
/// </summary>
/// <remarks>
/// First find out which tracks have to be downloaded (<see cref="TracksToDownload"/>), resolve them (and let the
/// user review uncertain matches), then call <see cref="SyncAsync"/> with the resolutions.
/// </remarks>
public sealed class PlayerSyncer(ITrackFinder finder, TrackTagWriter tagWriter, string workDirectory)
{
    private const string Extension = ".m4a";

    /// <summary>Tells whether the volume a path is on is write-protected (e.g. a card's lock switch), without writing.</summary>
    public Func<string, bool>? IsWriteProtected { get; init; }

    /// <summary>
    /// Where finished downloads are kept until they're on the player, so an interrupted sync resumes without
    /// downloading them again. Without one, every sync downloads everything it needs.
    /// </summary>
    public DownloadCache? Cache { get; init; }

    /// <summary>
    /// Checks, just before anything is copied, that the player (perhaps swapped since the sync started) isn't locked
    /// and has room for the files.
    /// </summary>
    /// <returns>Why nothing can be copied, if it can't.</returns>
    private string? CheckPlayerBeforeCopying(string root, IEnumerable<long> sizes, long freedBytes)
    {
        if (IsWriteProtected?.Invoke(root) == true) return PlacementExecutor.PlayerLockedMessage;

        long free;
        try
        {
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // E.g. a network folder; the copies themselves will tell
        }

        return SpaceEstimate.Check(sizes.ToList(), free, freedBytes) is { } shortfall ? SpaceEstimate.Describe(shortfall) : null;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The selected tracks that aren't on the player yet and so have to be downloaded, in the playlist's or album's
    /// order. A playlist's tracks found elsewhere on the player are copied instead.
    /// </summary>
    public static IReadOnlyList<ExportedTrack> TracksToDownload(PlayerLibrary library, SyncRequest request)
    {
        var selected = new HashSet<ExportedTrack>(request.Selected, ReferenceEqualityComparer.Instance);
        if (PlacementPlanner.UsesAlbumLayout(request.Kind))
            return request.AllTracks.Where(selected.Contains).ToList();

        var root = library.RootPath;
        var plan = PlaylistPlanner.Plan(
            FolderFor(root, request), request.AllTracks, root, library.GetSnapshot().Files,
            downloadExtension: Extension,
            maxRelativePathLength: PlacementPlanner.MaxRelativePathLengthFor(root));
        return plan.Tracks.Where(t => t.State == PlaylistTrackState.Download && selected.Contains(t.Track)).Select(t => t.Track).ToList();
    }

    /// <summary>The playlist's own folder on the player: found by its marker, or a new one with a name of its own.</summary>
    public static PlaylistFolderAssignment FolderFor(string playerRoot, SyncRequest request) =>
        PlaylistFolders.Assign(playerRoot, request.Key, request.Name, request.Owner, PlacementPlanner.MaxRelativePathLengthFor(playerRoot));

    /// <exception cref="OperationCanceledException">Canceled before anything was placed on the player.</exception>
    /// <exception cref="HttpRequestException">yt-dlp isn't available and couldn't be downloaded.</exception>
    public async Task<SyncResult> SyncAsync(PlayerLibrary library, SyncRequest request, IProgress<TrackSyncProgress> progress, CancellationToken cancellationToken)
    {
        var root = library.RootPath;
        var selected = new HashSet<ExportedTrack>(request.Selected, ReferenceEqualityComparer.Instance);
        var errors = new List<string>();
        // How each song ended up, for the result. Every final state is decided in this method's own flow, one at a
        // time (download progress, which arrives concurrently, only reports states along the way).
        var added = new List<ExportedTrack>();
        var failed = new List<(ExportedTrack Track, string? Message)>();
        var needsReview = new List<ExportedTrack>();
        var finished = new HashSet<ExportedTrack>(ReferenceEqualityComparer.Instance);

        void Finish(ExportedTrack track, TrackSyncState state, string? message = null)
        {
            // A song's final state is decided once: a failed download is never placed, and only added files carry a song.
            var first = finished.Add(track);
            Debug.Assert(first, $"{track.Title} already finished");
            switch (state)
            {
                case TrackSyncState.Done: added.Add(track); break;
                case TrackSyncState.Failed: failed.Add((track, message)); break;
                case TrackSyncState.NeedsReview: needsReview.Add(track); break;
                default: throw new ArgumentOutOfRangeException(nameof(state), state, "Not a final state.");
            }
            progress.Report(new(track, state, Message: message));
        }

        SyncResult Result() => new(errors, added, failed, needsReview);
        var downloadFailed = new HashSet<string>();
        var downloadedVideoIds = new Dictionary<string, string>();
        bool IsResolved(ExportedTrack track) => request.Resolutions?.GetValueOrDefault(track)?.State == TrackResolutionState.Resolved;

        Cache?.Prune(DateTimeOffset.UtcNow);

        // Downloads go to the local disk first; only finished, tagged files are copied onto the player.
        // Also removes what a crashed sync left behind.
        var staging = StagingDirectory.Create(workDirectory);
        var batchDirectory = staging.Path;
        try
        {
            PlacementReport report;
            Func<string?, ExportedTrack?> trackForId;
            PlaylistFolderAssignment? folder = null;

            if (PlacementPlanner.UsesAlbumLayout(request.Kind))
            {
                // Track numbers come from the whole album's order, even when only some tracks are synced.
                var metadata = TrackMetadata.FromAlbum(request.AllTracks);
                var wanted = request.AllTracks.Select((track, i) => (Id: i.ToString(CultureInfo.InvariantCulture), Track: track, Metadata: metadata[i]))
                    .Where(t => selected.Contains(t.Track) && IsResolved(t.Track))
                    .ToList();

                var downloaded = await DownloadAndTagAsync(wanted, progress, cancellationToken);
                if (downloaded.Count == 0) return Result();

                // One plan for the whole batch: whether an album gets a folder depends on everything going on the player.
                var existing = library.GetSnapshot().Files.Select(file => ToPlayerFile(root, file)).ToList();
                var placementTracks = wanted.Where(w => downloaded.ContainsKey(w.Id)).Select(w => PlacementTrack.FromMetadata(w.Id, w.Metadata, Extension)).ToList();
                var plan = PlacementPlanner.Plan(placementTracks, existing, maxRelativePathLength: PlacementPlanner.MaxRelativePathLengthFor(root),
                    playlistFolders: PlaylistFolders.FindPlaylistFolders(root));

                if (CheckPlayerBeforeCopying(root, downloaded.Values.Select(SizeOf), 0) is { } albumProblem)
                    return NotCopied(albumProblem, wanted.Where(w => downloaded.ContainsKey(w.Id)).Select(w => w.Track));

                report = await new PlacementExecutor(root) { DeleteDownloadsAfterCopy = true }.ExecuteAsync(plan, t => downloaded.GetValueOrDefault(t.Id), cancellationToken);
                var byId = wanted.ToDictionary(w => w.Id, w => w.Track);
                trackForId = id => id is null ? null : byId.GetValueOrDefault(id);
            }
            else
            {
                folder = FolderFor(root, request);
                var fullPlan = PlaylistPlanner.Plan(
                    folder, request.AllTracks, root, library.GetSnapshot().Files,
                    cleanupSelection: request.Cleanup,
                    downloadExtension: Extension,
                    maxRelativePathLength: PlacementPlanner.MaxRelativePathLengthFor(root));

                // Renumbering and cleanup always apply; adding a track only when chosen, and downloading it only when resolved.
                var plan = fullPlan with
                {
                    Tracks = fullPlan.Tracks
                        .Where(t => t.State switch
                        {
                            PlaylistTrackState.InPlace or PlaylistTrackState.Renumber => true,
                            PlaylistTrackState.CopyFromPlayer => selected.Contains(t.Track),
                            _ => selected.Contains(t.Track) && IsResolved(t.Track),
                        })
                        .ToList(),
                };

                var wanted = plan.Downloads.Select(t => (Id: t.Id, t.Track, Metadata: TrackMetadata.FromExportedTrack(t.Track))).ToList();
                var downloaded = await DownloadAndTagAsync(wanted, progress, cancellationToken);
                foreach (var copy in plan.Tracks.Where(t => t.State == PlaylistTrackState.CopyFromPlayer))
                    progress.Report(new(copy.Track, TrackSyncState.Saving));

                // Everything written to the player goes through here, one write at a time; nothing at all once cancelled.
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotSizes = library.GetSnapshot().Files.ToDictionary(f => f.Path, f => f.Size, StringComparer.OrdinalIgnoreCase);
                long SizeOnPlayer(string relativePath) => snapshotSizes.GetValueOrDefault(Path.Combine(root, relativePath));
                var adding = plan.Tracks.Where(t => t.State == PlaylistTrackState.CopyFromPlayer || downloaded.ContainsKey(t.Id)).ToList();
                var addingSizes = adding.Select(t => t.State == PlaylistTrackState.CopyFromPlayer ? SizeOnPlayer(t.CopySourceRelativePath!) : SizeOf(downloaded[t.Id]));
                if (CheckPlayerBeforeCopying(root, addingSizes, plan.Deletions.Sum(d => SizeOnPlayer(d.RelativePath))) is { } playlistProblem)
                    return NotCopied(playlistProblem, adding.Select(t => t.Track));
                PlaylistFolders.Claim(root, folder);
                report = await new PlacementExecutor(root) { DeleteDownloadsAfterCopy = true }.ExecuteAsync(plan, t => downloaded.GetValueOrDefault(t.Id), cancellationToken);
                var byId = plan.Tracks.ToDictionary(t => t.Id, t => t.Track);
                trackForId = id => id is null ? null : byId.GetValueOrDefault(id);
            }

            if (report.StoppedBecause is { } stopped) errors.Add(stopped);

            foreach (var change in report.Changes)
            {
                var track = trackForId(change.TrackId);
                if (!change.Succeeded)
                {
                    // Its download failure has been reported already.
                    if (change.TrackId is { } failedId && downloadFailed.Contains(failedId)) continue;
                    if (track is not null) Finish(track, TrackSyncState.Failed, change.Error);
                    else errors.Add(change.Error!);
                    continue;
                }

                switch (change.Kind)
                {
                    case PlacementChangeKind.Added:
                        library.NotifyFileWritten(Path.Combine(root, change.ActualRelativePath!));
                        if (track is not null) Finish(track, TrackSyncState.Done);
                        // On the player now, so the cached download isn't needed.
                        if (change.FromRelativePath is null && change.TrackId is { } addedId && downloadedVideoIds.TryGetValue(addedId, out var videoId))
                            Cache?.Remove(videoId);
                        break;
                    case PlacementChangeKind.Moved or PlacementChangeKind.Renamed:
                        library.NotifyMoved(Path.Combine(root, change.FromRelativePath!), Path.Combine(root, change.ActualRelativePath!));
                        break;
                    case PlacementChangeKind.Deleted:
                        library.NotifyDeleted(Path.Combine(root, change.FromRelativePath!));
                        break;
                }
            }

            // A playlist's folder takes its final name (e.g. after the playlist was renamed) once its files are in place.
            if (folder is not null)
            {
                try
                {
                    var actual = PlaylistFolders.FinishRename(root, folder);
                    if (!string.Equals(actual, folder.CurrentRelativePath, StringComparison.Ordinal))
                        library.NotifyMoved(Path.Combine(root, folder.CurrentRelativePath), Path.Combine(root, actual));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"Could not rename the playlist's folder: {ex.Message}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Result();
        }
        finally
        {
            staging.Dispose();
        }

        SyncResult NotCopied(string problem, IEnumerable<ExportedTrack> tracks)
        {
            foreach (var track in tracks) Finish(track, TrackSyncState.Failed, problem);
            errors.Add(problem);
            return Result();
        }

        async Task<Dictionary<string, string>> DownloadAndTagAsync(
            IReadOnlyList<(string Id, ExportedTrack Track, TrackMetadata Metadata)> wanted,
            IProgress<TrackSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            var paths = new Dictionary<string, string>();
            if (wanted.Count == 0) return paths;

            string? VideoIdOf(ExportedTrack track) => request.Resolutions?.GetValueOrDefault(track)?.Video?.VideoId;

            // Songs downloaded by an earlier sync that didn't finish aren't downloaded again.
            var finished = new Dictionary<string, (string Path, string? CoverArtUrl)>();
            var toDownload = new List<(string Id, ExportedTrack Track, TrackMetadata Metadata)>();
            foreach (var w in wanted)
            {
                var destination = Path.Combine(batchDirectory, w.Id + Extension);
                if (VideoIdOf(w.Track) is { } videoId && Cache?.TryGet(videoId) is { } cached)
                {
                    try
                    {
                        File.Copy(cached.Path, destination, overwrite: true);
                        finished[w.Id] = (destination, cached.CoverArtUrl);
                        downloadedVideoIds[w.Id] = videoId;
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Download it again instead.
                    }
                }

                toDownload.Add(w);
            }

            var cachedIds = new HashSet<string>();
            void Keep(TrackDownloadResult result)
            {
                if (Cache is null || VideoIdOf(result.Request.Track) is not { } videoId) return;
                lock (cachedIds)
                {
                    if (!cachedIds.Add(result.Request.DestinationPath)) return;
                }

                Cache.Add(videoId, DownloadCache.FormatIdOrUnknown(result.Format?.FormatId), result.CoverArtUrl, result.Request.DestinationPath);
            }

            var requests = toDownload.Select(w => new TrackDownloadRequest(request.Resolutions![w.Track], Path.Combine(batchDirectory, w.Id + Extension))).ToList();
            var downloads = requests.Count == 0 ? [] : await finder.DownloadTracksAsync(requests, update =>
            {
                // Kept as soon as each one finishes, so cancelling the rest doesn't lose it.
                if (update is { Stage: TrackDownloadStage.Succeeded, Result: { Succeeded: true } result }) Keep(result);

                TrackSyncState? state = update.Stage switch
                {
                    TrackDownloadStage.Queued => TrackSyncState.Queued,
                    TrackDownloadStage.Searching => TrackSyncState.Searching,
                    TrackDownloadStage.Preparing or TrackDownloadStage.Downloading => TrackSyncState.Downloading,
                    TrackDownloadStage.Remuxing => TrackSyncState.Saving,
                    _ => null, // Success and failure are reported from the results
                };
                if (state is { } s) progress.Report(new(update.Request.Track, s, update.Fraction * 100));
            }, cancellationToken);

            for (var i = 0; i < downloads.Count; i++)
            {
                var (id, track, _) = toDownload[i];
                var download = downloads[i];
                if (!download.Succeeded)
                {
                    downloadFailed.Add(id);
                    Finish(track, download.NeedsReview ? TrackSyncState.NeedsReview : TrackSyncState.Failed, download.FailureReason);
                    continue;
                }

                Keep(download);
                finished[id] = (download.Request.DestinationPath, download.CoverArtUrl);
                if (VideoIdOf(track) is { } videoId) downloadedVideoIds[id] = videoId;
            }

            foreach (var (id, track, metadata) in wanted)
            {
                if (!finished.TryGetValue(id, out var download)) continue;

                progress.Report(new(track, TrackSyncState.Saving));
                try
                {
                    // Exportify has no album image, so the art comes from the YouTube Music match.
                    var tags = metadata.AlbumImageUrl.Length == 0 && download.CoverArtUrl is { } coverArtUrl
                        ? metadata with { AlbumImageUrl = coverArtUrl }
                        : metadata;
                    await tagWriter.WriteAsync(download.Path, tags, cancellationToken);
                    paths[id] = download.Path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
                {
                    downloadFailed.Add(id);
                    Finish(track, TrackSyncState.Failed, "Could not tag the download: " + ex.Message);
                }
            }

            return paths;
        }
    }

    private static PlayerFile ToPlayerFile(string root, IndexedFile file) => new(
        Path.GetRelativePath(root, file.Path),
        // As TrackTagWriter writes it, so albums synced earlier are recognized
        string.Join(", ", file.Tags.AlbumArtists),
        file.Tags.Album,
        file.Tags.TrackNumber,
        file.Tags.Title,
        file.Tags.Artists.FirstOrDefault() ?? "");
}
