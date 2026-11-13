namespace Mp3PlayerSync.Core.Download;

// The shared lookup pool: every lookup in the app (ResolveTracksAsync, FindInBackground, RetryAsync, and downloads
// of unresolved requests) runs here, at most MaxParallelLookups at a time (and, with downloads, at most
// MaxParallelLookupsAndDownloads), so browsing many playlists can't flood YouTube. Lookups that something is waiting
// for run before background lookups. Each track is looked up at most once at a time; later requests for it join the
// lookup in progress.
public sealed partial class YouTubeMusicDownloader
{
    private readonly Lock lookupLock = new();
    private readonly Dictionary<string, LookupItem> lookups = [];
    private readonly CancellationTokenSource lifetime = new();
    private int runningLookups;
    private long lookupSequence;
    private int isSaveScheduled;
    private bool lookupsDisposed;

    /// <summary>
    /// Raised as lookups are queued, start, and complete, for <see cref="FindInBackground"/>, <see
    /// cref="ResolveTracksAsync"/>, and <see cref="RetryAsync"/>. When several playlists contain the same track, it's
    /// raised for each <see cref="ExportedTrack"/> instance that asked. Raised on a background thread.
    /// </summary>
    public event EventHandler<TrackResolutionProgress>? LookupProgress;

    /// <summary>The number of lookups queued or running.</summary>
    public int PendingLookupCount
    {
        get { lock (lookupLock) return lookups.Count; }
    }

    /// <summary>
    /// Returns what's already known about each track instantly (<see langword="null"/> where nothing is), and looks
    /// up the rest in the background. Results arrive through <see cref="LookupProgress"/> and are remembered, so
    /// later calls return them instantly.
    /// </summary>
    /// <remarks>
    /// Each call replaces the previous set of background tracks: queued lookups for tracks that aren't in
    /// <paramref name="tracks"/> are dropped (they'll be queued again the next time their playlist is shown), and
    /// lookups that already started finish and are remembered. The new tracks are looked up in the given order,
    /// after any lookups that something is waiting for. Pass an empty list to stop background lookups.
    /// </remarks>
    public IReadOnlyList<TrackResolution?> FindInBackground(IReadOnlyList<ExportedTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var results = new TrackResolution?[tracks.Count];
        var queued = new List<ExportedTrack>();

        lock (lookupLock)
        {
            ObjectDisposedException.ThrowIf(lookupsDisposed, this);

            var wanted = new HashSet<string>();
            for (var i = 0; i < tracks.Count; i++)
            {
                results[i] = GetCachedResolution(tracks[i]);
                if (results[i] is null) wanted.Add(GetLookupKey(tracks[i]));
            }

            foreach (var item in lookups.Values.Where(i => i.InBackgroundSet && !wanted.Contains(i.Key)).ToList())
            {
                item.InBackgroundSet = false;
                if (!item.Started && item.ForegroundWaiters == 0)
                {
                    lookups.Remove(item.Key);
                    item.Completion.TrySetCanceled();
                }
            }

            for (var i = 0; i < tracks.Count; i++)
            {
                if (results[i] is not null) continue;

                var item = GetOrAddLookup(tracks[i], out var added);
                item.InBackgroundSet = true;
                if (!item.Started) item.Sequence = ++lookupSequence;
                if (added) queued.Add(tracks[i]);
            }
        }

        foreach (var track in queued)
            RaiseLookupProgress(new(track, TrackResolutionStage.Queued));

        PumpLookups();
        return results;
    }

    /// <summary>
    /// Looks a track up again, ignoring what's remembered, ahead of background lookups. Use for "Try again" when a
    /// track needs review or its lookup failed. The new result replaces what was remembered.
    /// </summary>
    public Task<TrackResolution> RetryAsync(ExportedTrack track, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        RaiseLookupProgress(new(track, TrackResolutionStage.Queued));
        return LookUpSharedAsync(track, progress: null, bypassCache: true, cancellationToken);
    }

    private async Task<TrackResolution> LookUpSharedAsync(ExportedTrack track, IProgress<TrackResolutionProgress>? progress, bool bypassCache, CancellationToken cancellationToken)
    {
        if (!bypassCache && GetCachedResolution(track) is { } cached)
            return cached;

        LookupItem item;
        bool started;
        lock (lookupLock)
        {
            ObjectDisposedException.ThrowIf(lookupsDisposed, this);

            item = GetOrAddLookup(track, out _);
            if (item.ForegroundWaiters++ == 0 && !item.Started) item.Sequence = ++lookupSequence;
            if (progress is not null) item.Observers.Add((track, progress));
            started = item.Started;
        }

        if (started) progress?.Report(new(track, TrackResolutionStage.Searching));
        PumpLookups();

        try
        {
            var result = await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result with { Track = track };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (lookupLock)
            {
                item.ForegroundWaiters--;
                if (!item.Started && item.ForegroundWaiters == 0 && !item.InBackgroundSet && lookups.GetValueOrDefault(item.Key) == item)
                {
                    lookups.Remove(item.Key);
                    item.Completion.TrySetCanceled();
                }
            }

            throw;
        }
    }

    private LookupItem GetOrAddLookup(ExportedTrack track, out bool added)
    {
        var key = GetLookupKey(track);
        added = false;

        // A retry can join a lookup that's queued or running: lookups never read the cache once queued.
        if (!lookups.TryGetValue(key, out var item))
        {
            item = new LookupItem(key, track);
            lookups.Add(key, item);
            added = true;
        }
        else if (!item.Tracks.Contains(track))
        {
            item.Tracks.Add(track);
        }

        return item;
    }

    private static string GetLookupKey(ExportedTrack track) =>
        track.Uri.Length > 0 ? "uri:" + track.Uri
        : track.Isrc.Length > 0 ? "isrc:" + track.Isrc.ToUpperInvariant()
        : "track:" + track.Title + "\n" + string.Join("\n", track.Artists) + "\n" + track.Duration;

    private void PumpLookups()
    {
        var toStart = new List<LookupItem>();
        lock (lookupLock)
        {
            while (!lookupsDisposed && runningLookups < options.MaxParallelLookups)
            {
                var next = lookups.Values
                    .Where(i => !i.Started)
                    .MinBy(i => (i.ForegroundWaiters > 0 ? 0 : 1, i.Sequence));
                if (next is null) break;

                next.Started = true;
                runningLookups++;
                toStart.Add(next);
            }
        }

        foreach (var item in toStart)
            _ = Task.Run(() => RunLookupAsync(item));
    }

    private async Task RunLookupAsync(LookupItem item)
    {
        Notify(item, null, TrackResolutionStage.Searching);

        TrackResolution? result = null;
        try
        {
            await lookupAndDownloadSlots.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                var (source, lease) = await acquireSourceAsync(lifetime.Token).ConfigureAwait(false);
                using (lease)
                    result = await LookUpAsync(source, item.Tracks[0], lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                lookupAndDownloadSlots.Release();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Usually that yt-dlp isn't downloaded yet and can't be (e.g. offline).
            result = new TrackResolution(item.Tracks[0], TrackResolutionState.LookupFailed, null, [], $"Couldn't search YouTube Music. {ex.Message}", FromCache: false);
        }

        if (result is not null && !lifetime.IsCancellationRequested)
        {
            RememberUnresolved(result);
            ScheduleCacheSave();
        }

        lock (lookupLock)
        {
            runningLookups--;
            if (lookups.GetValueOrDefault(item.Key) == item) lookups.Remove(item.Key);
        }

        if (result is null)
        {
            item.Completion.TrySetCanceled();
        }
        else
        {
            item.Completion.TrySetResult(result);
            Notify(item, result, TrackResolutionStage.Completed);
        }

        PumpLookups();
    }

    private void Notify(LookupItem item, TrackResolution? result, TrackResolutionStage stage)
    {
        List<ExportedTrack> tracks;
        List<(ExportedTrack Track, IProgress<TrackResolutionProgress> Progress)> observers;
        lock (lookupLock)
        {
            tracks = item.Tracks.ToList();
            observers = item.Observers.ToList();
        }

        foreach (var track in tracks)
            RaiseLookupProgress(new(track, stage, result is null ? null : result with { Track = track }));

        foreach (var (track, progress) in observers)
            progress.Report(new(track, stage, result is null ? null : result with { Track = track }));
    }

    private void RaiseLookupProgress(TrackResolutionProgress progress)
    {
        try
        {
            LookupProgress?.Invoke(this, progress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing handler mustn't stop the lookup pool.
        }
    }

    private void ScheduleCacheSave()
    {
        if (Interlocked.Exchange(ref isSaveScheduled, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            Volatile.Write(ref isSaveScheduled, 0);
            TrySaveCache();
        });
    }

    private void TrySaveCache()
    {
        try
        {
            cache.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DisposeLookups()
    {
        List<LookupItem> pending;
        lock (lookupLock)
        {
            if (lookupsDisposed) return;
            lookupsDisposed = true;
            pending = lookups.Values.Where(i => !i.Started).ToList();
            foreach (var item in pending) lookups.Remove(item.Key);
        }

        lifetime.Cancel();
        foreach (var item in pending) item.Completion.TrySetCanceled();
    }

    private sealed class LookupItem(string key, ExportedTrack track)
    {
        public string Key { get; } = key;

        /// <summary>Every track instance that asked for this lookup (e.g. the same song in several playlists).</summary>
        public List<ExportedTrack> Tracks { get; } = [track];

        public List<(ExportedTrack Track, IProgress<TrackResolutionProgress> Progress)> Observers { get; } = [];

        public TaskCompletionSource<TrackResolution> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many callers are awaiting this lookup. Those lookups run before background lookups.</summary>
        public int ForegroundWaiters { get; set; }

        public bool InBackgroundSet { get; set; }

        public long Sequence { get; set; }

        public bool Started { get; set; }
    }
}
