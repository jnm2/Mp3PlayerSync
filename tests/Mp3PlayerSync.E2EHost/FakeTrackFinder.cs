using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.E2EHost;

/// <summary>
/// Stands in for YouTube Music in the end-to-end tests, "downloading" one .m4a file for every song. Among the Café
/// playlist's songs, " 2" needs review, " 3" can't be searched for, and " 1" is gone the first time it's downloaded.
/// Everything else matches quickly and takes a moment to download.
/// </summary>
/// <param name="holdFile">
/// While this file exists, looking for songs in the background stops after the first <see cref="HeldAfter"/> of each
/// batch, so a test can see progress that would otherwise be over in a moment.
/// </param>
internal sealed class FakeTrackFinder(string audioPath, string? holdFile = null) : ITrackFinder
{
    public const int HeldAfter = 10;
    private static readonly YouTubeFormat Aac = new("140", "m4a", "mp4a.40.2", "none", 129, 44100, "https", "medium", null, false);

    private readonly object gate = new();
    private readonly HashSet<string> skipped = [];
    private readonly Dictionary<string, ResolvedVideo> chosen = [];
    private readonly HashSet<string> gone = [];
    private readonly Dictionary<string, TrackResolution> known = [];
    private CancellationTokenSource? background;

    public event EventHandler<TrackResolutionProgress>? LookupProgress;

    public TrackResolution? GetCachedResolution(ExportedTrack track)
    {
        lock (gate)
        {
            if (skipped.Contains(track.Uri)) return new(track, TrackResolutionState.Skipped, null, [], null, FromCache: true);
            if (chosen.TryGetValue(track.Uri, out var video)) return new(track, TrackResolutionState.Resolved, video, [], null, FromCache: true);
            return known.TryGetValue(track.Uri, out var resolution) ? resolution with { Track = track, FromCache = true } : null;
        }
    }

    public IReadOnlyList<TrackResolution?> FindInBackground(IReadOnlyList<ExportedTrack> tracks)
    {
        background?.Cancel();
        var cancellation = background = new CancellationTokenSource();
        var results = tracks.Select(GetCachedResolution).ToList();
        var queued = tracks.Where((_, i) => results[i] is null).ToList();
        foreach (var track in queued) LookupProgress?.Invoke(this, new(track, TrackResolutionStage.Queued));

        _ = Task.Run(async () =>
        {
            var done = 0;
            foreach (var track in queued)
            {
                while (done >= HeldAfter && holdFile is not null && File.Exists(holdFile) && !cancellation.IsCancellationRequested)
                    await Task.Delay(50);
                if (cancellation.IsCancellationRequested) return;
                LookupProgress?.Invoke(this, new(track, TrackResolutionStage.Searching));
                await Task.Delay(Delay(track));
                LookupProgress?.Invoke(this, new(track, TrackResolutionStage.Completed, Look(track)));
                done++;
            }
        });
        return results;
    }

    public Task<TrackResolution> RetryAsync(ExportedTrack track, CancellationToken cancellationToken) => Task.FromResult(Look(track));

    private static int Delay(ExportedTrack track) => IsSpecial(track) ? 100 : 5;

    /// <summary>Looks the track up, remembering the result as the real cache does (but not failed searches).</summary>
    private TrackResolution Look(ExportedTrack track)
    {
        var resolution = Resolve(track);
        if (resolution.State != TrackResolutionState.LookupFailed)
        {
            lock (gate) known[track.Uri] = resolution;
        }
        return resolution;
    }

    public async Task<IReadOnlyList<TrackResolution>> ResolveTracksAsync(IReadOnlyList<ExportedTrack> tracks, IProgress<TrackResolutionProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var track in tracks) progress?.Report(new(track, TrackResolutionStage.Queued));

        var results = new List<TrackResolution>();
        foreach (var track in tracks)
        {
            if (GetCachedResolution(track) is not { } resolution)
            {
                progress?.Report(new(track, TrackResolutionStage.Searching));
                await Task.Delay(Delay(track), cancellationToken);
                resolution = Look(track);
            }
            progress?.Report(new(track, TrackResolutionStage.Completed, resolution));
            results.Add(resolution);
        }
        return results;
    }

    private TrackResolution Resolve(ExportedTrack track)
    {
        lock (gate)
        {
            if (skipped.Contains(track.Uri)) return new(track, TrackResolutionState.Skipped, null, [], null, FromCache: true);
            if (chosen.TryGetValue(track.Uri, out var video)) return new(track, TrackResolutionState.Resolved, video, [], null, FromCache: true);
            if (IsSpecial(track) && track.Title.EndsWith(" 2", StringComparison.Ordinal) || gone.Contains(track.Uri))
                return new(track, TrackResolutionState.NeedsReview, null, Candidates(track), "The closest result has a different title.", FromCache: false);
        }

        return IsSpecial(track) && track.Title.EndsWith(" 3", StringComparison.Ordinal)
            ? new(track, TrackResolutionState.LookupFailed, null, [], "The connection timed out.", FromCache: false)
            : new(track, TrackResolutionState.Resolved, Video(track, VideoId("auto", track), ResolutionSource.Automatic), [], null, FromCache: false);
    }

    private static bool IsSpecial(ExportedTrack track) => track.Title.StartsWith("cafe ", StringComparison.Ordinal);

    private static List<ResolutionCandidate> Candidates(ExportedTrack track) =>
    [
        new(Info(track, VideoId("live", track), track.Title + " (Live)", (YouTubeFormat[])[Aac]), Score(0.72), Aac, null),
        new(Info(track, VideoId("opus", track), track.Title + " (Remix)", []), Score(0.6), null, "Only Opus audio is available."),
    ];

    private static YouTubeVideoInfo Info(ExportedTrack track, string id, string title, IReadOnlyList<YouTubeFormat> formats) =>
        new(id, YouTubeUrls.GetMusicWatchUrl(id), title, title, track.Artists, "Live Album", null, track.Duration, 1, formats);

    private static MatchScore Score(double total) => new(total, total, 1, 1, 0, 0, IsAcceptable: false);

    private static ResolvedVideo Video(ExportedTrack track, string id, ResolutionSource source) =>
        new(id, track.Title, track.Artists, track.Album, track.Duration, 1, source, DateTimeOffset.UtcNow);

    // Eleven characters, like a real video ID.
    private static string VideoId(string prefix, ExportedTrack track) =>
        (prefix + new string(track.Uri.Where(char.IsLetterOrDigit).Reverse().ToArray()) + "00000000000")[..11];

    public Task<ManualChoiceResult> ChooseVideoAsync(ExportedTrack track, string urlOrVideoId, CancellationToken cancellationToken)
    {
        if (!YouTubeUrls.TryGetVideoId(urlOrVideoId, out var id, out var error))
            return Task.FromResult(new ManualChoiceResult(null, error));

        var video = Video(track, id, ResolutionSource.Manual);
        lock (gate) chosen[track.Uri] = video;
        return Task.FromResult(new ManualChoiceResult(new TrackResolution(track, TrackResolutionState.Resolved, video, [], null, FromCache: false), null));
    }

    public TrackResolution Skip(ExportedTrack track)
    {
        lock (gate) skipped.Add(track.Uri);
        return new(track, TrackResolutionState.Skipped, null, [], null, FromCache: false);
    }

    public bool Forget(ExportedTrack track)
    {
        lock (gate) return skipped.Remove(track.Uri) | chosen.Remove(track.Uri) | known.Remove(track.Uri);
    }


    public async Task<IReadOnlyList<TrackDownloadResult>> DownloadTracksAsync(IReadOnlyList<TrackDownloadRequest> requests, Action<TrackDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new List<TrackDownloadResult>();
        foreach (var request in requests)
        {
            progress?.Invoke(new(request, TrackDownloadStage.Downloading, 0.5));
            await Task.Delay(100, cancellationToken);

            var track = request.Track;
            bool isGone;
            lock (gate)
            {
                isGone = IsSpecial(track) && track.Title.EndsWith(" 1", StringComparison.Ordinal) && request.Resolution.Video?.Source == ResolutionSource.Automatic && gone.Add(track.Uri);
                if (isGone) known.Remove(track.Uri); // As the real cache drops a vanished video
            }
            if (isGone)
            {
                results.Add(new(request, null, null, "The video is no longer available.", NeedsReview: true));
                continue;
            }

            File.Copy(audioPath, request.DestinationPath, overwrite: true);
            results.Add(new(request, Aac, null, null));
        }
        return results;
    }
}
