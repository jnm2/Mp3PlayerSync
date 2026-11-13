using System.Globalization;
using Mp3PlayerSync.Core.Download.Tools;

namespace Mp3PlayerSync.Core.Download;

public sealed class YouTubeMusicDownloaderOptions
{
    /// <summary>
    /// Where yt-dlp is cached, e.g. a <c>Tools</c> folder in the app's local data folder.
    /// </summary>
    public required string ToolCacheDirectory { get; init; }

    /// <summary>
    /// The JSON file where track resolutions (automatic matches, manual choices, and skips) are remembered between
    /// runs, e.g. <c>YouTubeMatches.json</c> in the app's local data folder. If null, they're only remembered for the
    /// lifetime of the <see cref="YouTubeMusicDownloader"/>.
    /// </summary>
    public string? ResolutionCachePath { get; init; }

    public int MaxParallelLookups { get; init; } = 3;

    public int MaxParallelDownloads { get; init; } = 3;

    /// <summary>
    /// The most lookups and downloads running at once, together, so that the two pools share one budget when they
    /// overlap. Each is still limited by <see cref="MaxParallelLookups"/> or <see cref="MaxParallelDownloads"/>.
    /// </summary>
    public int MaxParallelLookupsAndDownloads { get; init; } = 6;

    /// <summary>
    /// How many flat YouTube Music search results (title only) to consider per track and search section.
    /// </summary>
    public int SearchResultCount { get; init; } = 10;

    /// <summary>
    /// The most search results per section to extract full metadata for (about 1.8 s each) when looking for a
    /// confident match. The best result by title is extracted first; others are only extracted if it isn't a
    /// confident match.
    /// </summary>
    public int MaxFullExtractions { get; init; } = 4;

    /// <summary>How many candidates to offer when a track needs review.</summary>
    public int ReviewCandidateCount { get; init; } = 5;

    /// <summary>
    /// How long to remember that a track had no confident match (with its review candidates) before looking it up
    /// again. YouTube Music's catalog changes slowly.
    /// </summary>
    public TimeSpan ReviewCacheLifetime { get; init; } = TimeSpan.FromDays(7);

    public double MinimumBitrateKbps { get; init; } = AudioFormatSelector.DefaultMinimumBitrateKbps;

    /// <summary>
    /// Additional yt-dlp arguments, for example <c>["--cookies-from-browser", "firefox"]</c> so that a YouTube Premium
    /// account can get format 141 (256 kbps AAC).
    /// </summary>
    public IReadOnlyList<string> ExtraYtDlpArguments { get; init; } = [];

    public string UserAgent { get; init; } = "Mp3PlayerSync";

    /// <summary>
    /// Paces requests to YouTube and stops them while YouTube pushes back. Share one with everything else that talks
    /// to YouTube. If null, requests aren't paced.
    /// </summary>
    public YouTubeTraffic? Traffic { get; init; }
}

/// <param name="Resolution">A resolution whose state is <see cref="TrackResolutionState.Resolved"/>.</param>
/// <param name="DestinationPath">
/// Where to write the .m4a file. Any existing file is overwritten. Nothing is written here if the download fails.
/// </param>
public sealed record TrackDownloadRequest(TrackResolution Resolution, string DestinationPath)
{
    /// <summary>
    /// Requests a track that hasn't been resolved. It's resolved when downloading (from the cache, or by looking it
    /// up), and fails if it needs review. Prefer resolving first with <see
    /// cref="YouTubeMusicDownloader.ResolveTracksAsync"/> so that the user can review uncertain matches.
    /// </summary>
    public TrackDownloadRequest(ExportedTrack track, string destinationPath)
        : this(new TrackResolution(track, TrackResolutionState.NeedsReview, null, [], "The track hasn't been looked up yet.", FromCache: false) { ResolveWhenDownloading = true }, destinationPath)
    {
    }

    public ExportedTrack Track => Resolution.Track;
}

public enum TrackDownloadStage
{
    Queued,

    /// <summary>Looking up a track that wasn't resolved before downloading.</summary>
    Searching,

    /// <summary>Checking that the resolved video is still available and getting its formats.</summary>
    Preparing,

    Downloading,
    Remuxing,
    Succeeded,
    Failed,
}

/// <param name="Fraction">Download progress from 0 to 1 during <see cref="TrackDownloadStage.Downloading"/>.</param>
public sealed record TrackDownloadProgress(TrackDownloadRequest Request, TrackDownloadStage Stage, double? Fraction = null)
{
    /// <summary>
    /// At <see cref="TrackDownloadStage.Succeeded"/>, the finished download, already complete at
    /// <see cref="TrackDownloadRequest.DestinationPath"/>, so it can be kept even if the rest of the batch is cancelled.
    /// </summary>
    public TrackDownloadResult? Result { get; init; }
}

/// <param name="Format">The YouTube format that was downloaded.</param>
/// <param name="Audio">What was actually written to <see cref="TrackDownloadRequest.DestinationPath"/>.</param>
/// <param name="FailureReason">Why the track wasn't downloaded, or <see langword="null"/> on success.</param>
/// <param name="NeedsReview">
/// Whether the resolved video can no longer be used (removed, private, region-locked, or, for an automatic match, no
/// acceptable audio anymore). The resolution has been forgotten, so resolving the track again will look it up or ask
/// for review. Failures that may be temporary (offline, rate limit, bot check) keep the resolution, including manual
/// choices, so the track can simply be downloaded again later.
/// </param>
public sealed record TrackDownloadResult(
    TrackDownloadRequest Request,
    YouTubeFormat? Format,
    M4aAudioInfo? Audio,
    string? FailureReason,
    bool NeedsReview = false)
{
    public bool Succeeded => FailureReason is null;

    /// <summary>The downloaded video's cover art (see <see cref="YouTubeCoverArt.SelectUrl"/>), if it has any.</summary>
    public string? CoverArtUrl { get; init; }

    /// <summary>
    /// Whether YouTube refused the request because this connection made too many (429, bot check). The match is
    /// kept; stop all YouTube traffic for a while and then download again.
    /// </summary>
    public bool IsThrottled { get; init; }
}

/// <summary>
/// The result of <see cref="YouTubeMusicDownloader.CheckToolsAsync"/>.
/// </summary>
/// <param name="DenoVersion">The JavaScript runtime yt-dlp used, or null if it couldn't be downloaded.</param>
/// <param name="Messages">yt-dlp's warnings and errors.</param>
/// <param name="BestAac">The format that would be downloaded, or null if there's no acceptable AAC.</param>
public sealed record ToolCheckResult(
    string YtDlpVersion,
    string? DenoVersion,
    IReadOnlyList<string> Messages,
    IReadOnlyList<YouTubeFormat> AudioFormats,
    YouTubeFormat? BestAac)
{
    public bool HasJavaScriptRuntimeWarning => Messages.Any(m => m.Contains("JavaScript runtime", StringComparison.OrdinalIgnoreCase) || m.Contains("JS runtime", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Finds tracks on YouTube Music and downloads their audio as AAC in .m4a files, using yt-dlp and no ffmpeg. Opus is
/// never downloaded.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="ResolveTracksAsync"/> first. Tracks with a confident match, or that were resolved before, come
/// back <see cref="TrackResolutionState.Resolved"/>. Others need review: the user picks one of the candidates or
/// pastes a URL (<see cref="ChooseVideoAsync"/>), or skips the track (<see cref="Skip"/>). Then pass the resolved
/// tracks to <see cref="DownloadTracksAsync"/>.
/// </para>
/// <para>
/// Automatic matches, manual choices, and skips are remembered (<see
/// cref="YouTubeMusicDownloaderOptions.ResolutionCachePath"/>), keyed by Spotify track URI and then ISRC.
/// Low-confidence matches aren't remembered, so they're looked up again next time.
/// </para>
/// </remarks>
public sealed partial class YouTubeMusicDownloader : IDisposable
{
    /// <summary>
    /// How long a lookup failure (e.g. offline) is remembered, so that browsing doesn't retry it over and over. Never
    /// saved, so it is retried next run.
    /// </summary>
    private static readonly TimeSpan LookupFailureLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A verified match at least this good (title, artist, duration, and album all match) is taken without
    /// extracting other candidates to compare, which might be on the original album rather than a compilation.
    /// </summary>
    private const double ConfidentScore = 0.98;

    /// <summary>A verified match at least this good is only compared against results with equally good titles.</summary>
    private const double GoodScore = 0.9;

    // The one-folder build starts in about 0.5 s; the single-file yt-dlp.exe unpacks itself on every start, which
    // takes about 1.3 s, and resolving and downloading a track takes 3 or more yt-dlp runs.
    private const string YtDlpCacheFolderName = "yt-dlp-win";
    private const string LegacyYtDlpCacheFolderName = "yt-dlp";

    private static string YtDlpAssetName => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "yt-dlp_win_arm64.zip",
        System.Runtime.InteropServices.Architecture.X86 => "yt-dlp_win_x86.zip",
        _ => "yt-dlp_win.zip",
    };

    /// <summary>Deno publishes no 32-bit Windows build; there, yt-dlp runs without a JavaScript runtime.</summary>
    private static string? DenoAssetName => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "deno-aarch64-pc-windows-msvc.zip",
        System.Runtime.InteropServices.Architecture.X64 => "deno-x86_64-pc-windows-msvc.zip",
        _ => null,
    };

    /// <summary>A well-known YouTube Music song ("Africa" by Toto) for <see cref="CheckToolsAsync"/>.</summary>
    private const string ToolCheckVideoUrl = "https://music.youtube.com/watch?v=QAo_Ycocl1E";

    /// <summary>YouTube stream URLs expire after a few hours; re-extract after this long to be safe.</summary>
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(1);

    private readonly YouTubeMusicDownloaderOptions options;
    private readonly ToolResolver? toolResolver;
    private readonly ToolResolver? denoResolver;
    private readonly Func<CancellationToken, Task<(IYouTubeMusicSource Source, IDisposable Lease)>> acquireUnpacedSourceAsync;
    private readonly Func<CancellationToken, Task<(IYouTubeMusicSource Source, IDisposable Lease)>> acquireSourceAsync;
    private readonly TrackResolutionCache cache;

    /// <summary>Slots for lookups and downloads together (<see cref="YouTubeMusicDownloaderOptions.MaxParallelLookupsAndDownloads"/>).</summary>
    private readonly SemaphoreSlim lookupAndDownloadSlots;

    public YouTubeMusicDownloader(YouTubeMusicDownloaderOptions options)
    {
        this.options = Validate(options);
        lookupAndDownloadSlots = new SemaphoreSlim(options.MaxParallelLookupsAndDownloads);
        cache = options.ResolutionCachePath is { } cachePath ? TrackResolutionCache.Load(cachePath) : TrackResolutionCache.CreateInMemory();

        var toolCacheDirectory = Path.GetFullPath(options.ToolCacheDirectory);
        toolResolver = new ToolResolver(
            Path.Join(toolCacheDirectory, YtDlpCacheFolderName),
            "yt-dlp.exe",
            DownloadResolvers.GitHubReleaseAsset("yt-dlp", "yt-dlp", YtDlpAssetName, options.UserAgent));

        if (DenoAssetName is { } denoAssetName)
        {
            denoResolver = new ToolResolver(
                Path.Join(toolCacheDirectory, "deno"),
                "deno.exe",
                DownloadResolvers.GitHubReleaseAsset("denoland", "deno", denoAssetName, options.UserAgent));
        }

        acquireUnpacedSourceAsync = async cancellationToken =>
        {
            var lease = await toolResolver.LeaseToolAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var denoLease = await TryLeaseDenoAsync(cancellationToken).ConfigureAwait(false);
                return (new YtDlpSource(new YtDlp(lease.FilePath, options.ExtraYtDlpArguments, denoLease?.FilePath)), new CompositeDisposable(lease, denoLease));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        };
        acquireSourceAsync = Paced(acquireUnpacedSourceAsync, options.Traffic);
    }

    /// <summary>
    /// Leases Deno, downloading it first if needed. Without it yt-dlp still works for now (with a warning and possibly
    /// fewer formats), so a failure to get it (e.g. offline on first run) doesn't stop lookups or downloads.
    /// </summary>
    private async Task<ToolLease?> TryLeaseDenoAsync(CancellationToken cancellationToken)
    {
        if (denoResolver is null) return null;

        try
        {
            return await denoResolver.LeaseToolAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal YouTubeMusicDownloader(YouTubeMusicDownloaderOptions options, IYouTubeMusicSource source)
    {
        this.options = Validate(options);
        lookupAndDownloadSlots = new SemaphoreSlim(options.MaxParallelLookupsAndDownloads);
        cache = options.ResolutionCachePath is { } cachePath ? TrackResolutionCache.Load(cachePath) : TrackResolutionCache.CreateInMemory();
        acquireUnpacedSourceAsync = _ => Task.FromResult<(IYouTubeMusicSource, IDisposable)>((source, new NullDisposable()));
        acquireSourceAsync = Paced(acquireUnpacedSourceAsync, options.Traffic);
    }

    private static Func<CancellationToken, Task<(IYouTubeMusicSource Source, IDisposable Lease)>> Paced(
        Func<CancellationToken, Task<(IYouTubeMusicSource Source, IDisposable Lease)>> acquireAsync, YouTubeTraffic? traffic)
    {
        if (traffic is null) return acquireAsync;

        return async cancellationToken =>
        {
            var (source, lease) = await acquireAsync(cancellationToken).ConfigureAwait(false);
            return (new PacedSource(source, traffic), lease);
        };
    }

    /// <summary>
    /// Checks whether YouTube takes requests, by extracting one well-known video, for <see
    /// cref="YouTubeTraffic"/> after a cool-down. Throws if YouTube is still pushing back.
    /// </summary>
    /// <returns>Whether the video was extracted.</returns>
    public async Task<bool> CheckYouTubeAsync(CancellationToken cancellationToken)
    {
        var (source, lease) = await acquireUnpacedSourceAsync(cancellationToken).ConfigureAwait(false);
        using (lease)
        {
            try
            {
                return (await source.ExtractAsync([ToolCheckVideoUrl], cancellationToken).ConfigureAwait(false)).Count == 1;
            }
            catch (YtDlpException ex) when (!ex.IsThrottled)
            {
                return false;
            }
        }
    }

    private static YouTubeMusicDownloaderOptions Validate(YouTubeMusicDownloaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxParallelLookups, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxParallelDownloads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxParallelLookupsAndDownloads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SearchResultCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFullExtractions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReviewCandidateCount, 1);
        return options;
    }

    public void Dispose()
    {
        DisposeLookups();
        toolResolver?.Dispose();
        denoResolver?.Dispose();
        TrySaveCache();
    }

    /// <summary>
    /// Downloads the latest yt-dlp and Deno (its JavaScript runtime) if they're newer than the cached ones, and deletes
    /// old cached versions. YouTube changes often break old versions of yt-dlp, so call this when the app starts.
    /// Failures are ignored for a tool that has a cached version (e.g. when offline), and always for Deno, which
    /// yt-dlp can still work without.
    /// </summary>
    public async Task UpdateToolAsync(CancellationToken cancellationToken)
    {
        if (toolResolver is null) return;

        toolResolver.PurgeOldVersions();
        denoResolver?.PurgeOldVersions();
        try
        {
            var legacyCache = Path.Join(Path.GetFullPath(options.ToolCacheDirectory), LegacyYtDlpCacheFolderName);
            if (Directory.Exists(legacyCache)) Directory.Delete(legacyCache, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var denoUpdate = denoResolver is null ? Task.CompletedTask : UpdateDenoAsync(denoResolver, cancellationToken);
        try
        {
            await toolResolver.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && toolResolver.CachedVersion is not null)
        {
        }

        await denoUpdate.ConfigureAwait(false);

        static async Task UpdateDenoAsync(ToolResolver resolver, CancellationToken cancellationToken)
        {
            try
            {
                await resolver.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Checks that yt-dlp and its JavaScript runtime work by extracting a well-known YouTube Music song. Downloads the
    /// tools first if needed.
    /// </summary>
    public async Task<ToolCheckResult> CheckToolsAsync(CancellationToken cancellationToken)
    {
        if (toolResolver is null) throw new InvalidOperationException("This downloader has no tools to check.");

        using var lease = await toolResolver.LeaseToolAsync(cancellationToken).ConfigureAwait(false);
        using var denoLease = await TryLeaseDenoAsync(cancellationToken).ConfigureAwait(false);
        var ytDlp = new YtDlp(lease.FilePath, options.ExtraYtDlpArguments, denoLease?.FilePath);
        var (video, messages) = options.Traffic is { } traffic
            ? await traffic.SendAsync(YouTubeTraffic.ExtractRequests, ct => ytDlp.ExtractWithMessagesAsync(ToolCheckVideoUrl, ct), cancellationToken).ConfigureAwait(false)
            : await ytDlp.ExtractWithMessagesAsync(ToolCheckVideoUrl, cancellationToken).ConfigureAwait(false);

        return new ToolCheckResult(
            lease.Version,
            denoLease?.Version,
            messages,
            video?.Formats.Where(f => f.IsAudioOnly).ToList() ?? [],
            video is null ? null : AudioFormatSelector.SelectBestAac(video.Formats, options.MinimumBitrateKbps).Format);
    }

    /// <summary>
    /// Finds each track on YouTube Music. Tracks with a remembered result (see <see cref="GetCachedResolution"/>)
    /// are returned without looking them up; the others are looked up ahead of background lookups, in the shared
    /// pool of <see cref="YouTubeMusicDownloaderOptions.MaxParallelLookups"/>. Results are in the same order as
    /// <paramref name="tracks"/>. A track whose lookup fails (e.g. offline) comes back <see
    /// cref="TrackResolutionState.LookupFailed"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Thrown if canceled. Lookups that already started still finish and are remembered.
    /// </exception>
    public async Task<IReadOnlyList<TrackResolution>> ResolveTracksAsync(
        IReadOnlyList<ExportedTrack> tracks,
        IProgress<TrackResolutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var results = new TrackResolution[tracks.Count];
        var lookups = new List<Task>();
        for (var i = 0; i < tracks.Count; i++)
        {
            if (GetCachedResolution(tracks[i]) is { } cached)
            {
                results[i] = cached;
                progress?.Report(new(tracks[i], TrackResolutionStage.Completed, cached));
            }
            else
            {
                progress?.Report(new(tracks[i], TrackResolutionStage.Queued));
                var index = i;
                lookups.Add(LookUpSharedAsync(tracks[i], progress, bypassCache: false, cancellationToken)
                    .ContinueWith(t => results[index] = t.Result, cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            }
        }

        await Task.WhenAll(lookups).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    /// Resolves a track to a video the user chose: one of the review candidates' <see
    /// cref="YouTubeVideoInfo.Url"/>s, or a pasted YouTube or YouTube Music link. The video is checked first: the
    /// link must be to a single available video with acceptable AAC audio. The choice is remembered.
    /// </summary>
    public async Task<ManualChoiceResult> ChooseVideoAsync(ExportedTrack track, string urlOrVideoId, CancellationToken cancellationToken)
    {
        if (!YouTubeUrls.TryGetVideoId(urlOrVideoId, out var videoId, out var error))
            return new(null, error);

        // Rather than wait out a cool-down while the user waits.
        if (options.Traffic?.BusyText is { } busy)
            return new(null, busy) { IsThrottled = true };

        var (source, lease) = await acquireSourceAsync(cancellationToken).ConfigureAwait(false);
        using (lease)
        {
            IReadOnlyList<ResolutionCandidate> extracted;
            try
            {
                extracted = await ExtractCandidatesAsync(source, TrackQuery.FromExportedTrack(track), [YouTubeUrls.GetMusicWatchUrl(videoId)], cancellationToken).ConfigureAwait(false);
            }
            catch (YtDlpException ex) when (ex.IsThrottled)
            {
                return new(null, YouTubeBusyMessage) { IsThrottled = true };
            }
            catch (YtDlpException ex)
            {
                return new(null, $"YouTube couldn't load that video. {ex.Message}");
            }

            if (extracted is not [var candidate])
                return new(null, "YouTube couldn't load that video.");

            if (!candidate.HasAcceptableAudio)
                return new(null, candidate.FormatProblem);

            var resolution = Resolved(track, candidate, ResolutionSource.Manual);
            cache.Save();
            return new(resolution, null);
        }
    }

    /// <summary>
    /// Remembers that the user doesn't want this track downloaded. Use <see cref="Forget"/> to undo.
    /// </summary>
    public TrackResolution Skip(ExportedTrack track)
    {
        cache.Set(track, new TrackResolutionCacheEntry { Source = ResolutionSource.Skipped, ResolvedAt = DateTimeOffset.UtcNow });
        cache.Save();
        return new TrackResolution(track, TrackResolutionState.Skipped, null, [], null, FromCache: false);
    }

    /// <summary>
    /// Forgets how a track was resolved or that it was skipped, so that the next <see cref="ResolveTracksAsync"/>
    /// looks it up again.
    /// </summary>
    /// <returns>Whether anything was remembered for the track.</returns>
    public bool Forget(ExportedTrack track)
    {
        var removed = cache.Remove(track);
        cache.Save();
        return removed;
    }

    /// <summary>
    /// Downloads resolved tracks in parallel (<see cref="YouTubeMusicDownloaderOptions.MaxParallelDownloads"/> at a
    /// time). A track that can't be downloaded doesn't stop the others; its result has a <see
    /// cref="TrackDownloadResult.FailureReason"/>. Results are in the same order as <paramref name="requests"/>.
    /// </summary>
    /// <param name="progress">
    /// Called as each track moves through its stages. It's called on thread-pool threads, and can be called for
    /// several tracks at once, so it must be thread-safe and return quickly.
    /// </param>
    /// <exception cref="OperationCanceledException">Thrown if canceled. Partially written files are deleted.</exception>
    /// <exception cref="HttpRequestException">Thrown if yt-dlp isn't cached and can't be downloaded.</exception>
    public async Task<IReadOnlyList<TrackDownloadResult>> DownloadTracksAsync(
        IReadOnlyList<TrackDownloadRequest> requests,
        Action<TrackDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) return [];

        foreach (var request in requests)
            progress?.Invoke(new(request, TrackDownloadStage.Queued));

        var results = new TrackDownloadResult[requests.Count];
        var (source, lease) = await acquireSourceAsync(cancellationToken).ConfigureAwait(false);
        using (lease)
        {
            try
            {
                await RunThrottledAsync(Enumerable.Range(0, requests.Count), options.MaxParallelDownloads, async i =>
                {
                    results[i] = await DownloadTrackAsync(source, requests[i], progress, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cache.Save();
            }
        }

        return results;
    }

    public async Task<TrackDownloadResult> DownloadTrackAsync(
        TrackDownloadRequest request,
        Action<TrackDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = await DownloadTracksAsync([request], progress, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    private static async Task RunThrottledAsync(IEnumerable<int> indexes, int maxParallel, Func<int, Task> action, CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(maxParallel);

        var tasks = indexes.Select(async i =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action(i).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>
    /// Returns what's remembered for a track without looking it up: a resolution, a skip, a review result with its
    /// candidates (until <see cref="YouTubeMusicDownloaderOptions.ReviewCacheLifetime"/> passes), or a lookup failure
    /// from the last few minutes of this run. Returns <see langword="null"/> if the track needs to be looked up.
    /// </summary>
    public TrackResolution? GetCachedResolution(ExportedTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (cache.TryGet(track) is not { } entry) return null;

        var age = DateTimeOffset.UtcNow - entry.ResolvedAt;
        switch (entry.State)
        {
            case TrackResolutionState.NeedsReview or TrackResolutionState.Unavailable when age < options.ReviewCacheLifetime:
                return new TrackResolution(track, entry.State.Value, null, entry.Candidates?.Select(c => c.ToCandidate()).ToList() ?? [], entry.Reason, FromCache: true);
            case TrackResolutionState.LookupFailed when age < LookupFailureLifetime:
                return new TrackResolution(track, TrackResolutionState.LookupFailed, null, [], entry.Reason, FromCache: true) { IsThrottled = entry.IsThrottled };
            case not null:
                return null;
        }

        return entry.Source == ResolutionSource.Skipped
            ? new TrackResolution(track, TrackResolutionState.Skipped, null, [], null, FromCache: true)
            : entry.ToResolvedVideo() is { } video
                ? new TrackResolution(track, TrackResolutionState.Resolved, video, [], null, FromCache: true)
                : null;
    }

    /// <summary>
    /// Remembers a lookup result that isn't a resolution: review results until <see
    /// cref="YouTubeMusicDownloaderOptions.ReviewCacheLifetime"/>, and lookup failures for this run only.
    /// </summary>
    private void RememberUnresolved(TrackResolution resolution)
    {
        if (resolution.State is TrackResolutionState.NeedsReview or TrackResolutionState.Unavailable or TrackResolutionState.LookupFailed)
            cache.Set(resolution.Track, TrackResolutionCacheEntry.ForUnresolved(resolution, DateTimeOffset.UtcNow));
    }

    private TrackResolution Resolved(ExportedTrack track, ResolutionCandidate candidate, ResolutionSource source)
    {
        var now = DateTimeOffset.UtcNow;
        var video = ResolvedVideo.From(candidate, source, now);
        cache.Set(track, TrackResolutionCacheEntry.From(video));

        return new TrackResolution(track, TrackResolutionState.Resolved, video, [], null, FromCache: false)
        {
            Details = candidate,
            DetailsExtractedAt = now,
        };
    }

    private async Task<TrackResolution> LookUpAsync(IYouTubeMusicSource source, ExportedTrack track, CancellationToken cancellationToken)
    {
        var query = TrackQuery.FromExportedTrack(track);
        if (string.IsNullOrWhiteSpace(query.Title))
            return new TrackResolution(track, TrackResolutionState.NeedsReview, null, [], "The track has no title (it may have been removed from Spotify).", FromCache: false);

        try
        {
            var extracted = new List<ResolutionCandidate>();
            var flatResults = new List<ScoredCandidate>();

            foreach (var section in (YouTubeMusicSearchSection[])[YouTubeMusicSearchSection.Songs, YouTubeMusicSearchSection.Videos])
            {
                var flat = await source.SearchAsync(query.SearchText, section, options.SearchResultCount, cancellationToken).ConfigureAwait(false);
                flatResults.AddRange(flat.Select(v => new ScoredCandidate(v, TrackMatcher.ScoreFlat(query, v))));

                // Flat results only have titles, so verify the most plausible ones with full metadata: the best one
                // first, and then a few more to compare if it isn't a confident match.
                var plausible = TrackMatcher.RankFlat(query, flat);
                var extractedThisSection = 0;
                var compared = false;
                while (extractedThisSection < options.MaxFullExtractions)
                {
                    var best = PickWinner(extracted);
                    var remaining = plausible.Where(c => !extracted.Any(e => e.Video.Id == c.Video.Id));

                    if (best is { Score.Total: >= ConfidentScore })
                    {
                        // If the playlist's version matters but this match's isn't known, check the results known to
                        // be one version or the other, so that an ambiguous pair is caught rather than guessed.
                        if (best.Score.Explicit != ExplicitMatch.Unknown || query.Explicit is null || compared) break;
                        compared = true;
                        remaining = remaining.Where(c => c.Score.Total >= GoodScore && c.Score.Explicit != ExplicitMatch.Unknown);
                    }
                    else if (best is { Score.Total: >= GoodScore })
                    {
                        // A good match can only be beaten by another result with an equally good title. One round of
                        // comparison is enough.
                        if (compared) break;
                        compared = true;
                        remaining = remaining.Where(c => c.Score.Total >= GoodScore);
                    }

                    var batch = remaining.Take(extractedThisSection == 0 ? 1 : Math.Min(2, options.MaxFullExtractions - extractedThisSection)).ToList();
                    if (batch.Count == 0) break;

                    extractedThisSection += batch.Count;
                    extracted.AddRange(await TryExtractCandidatesAsync(source, query, batch.Select(c => c.Video), cancellationToken).ConfigureAwait(false));
                }

                if (PickWinner(extracted) is { } winner)
                {
                    // If the playlist's explicit or clean version matters and the best match's version is unknown
                    // while the other version is present, they can't be told apart: ask instead of guessing.
                    var ambiguous = winner.Score.Explicit == ExplicitMatch.Unknown
                        && extracted.Any(c => c.Score.IsAcceptable && c.Score.Explicit == ExplicitMatch.Mismatch);
                    if (!ambiguous) return Resolved(track, winner, ResolutionSource.Automatic);
                }
            }

            // Offer enough candidates to choose from, including those whose titles didn't look right.
            var missing = options.ReviewCandidateCount - extracted.Count;
            if (missing > 0)
            {
                var more = flatResults
                    .Where(f => !extracted.Any(e => e.Video.Id == f.Video.Id))
                    .DistinctBy(f => f.Video.Id)
                    .OrderByDescending(f => f.Score.Total)
                    .ThenBy(f => f.Video.SearchRank ?? int.MaxValue)
                    .Take(missing)
                    .Select(f => f.Video);

                extracted.AddRange(await TryExtractCandidatesAsync(source, query, more, cancellationToken).ConfigureAwait(false));
            }

            var candidates = extracted
                .OrderByDescending(c => c.Score.IsAcceptable)
                .ThenByDescending(c => c.Score.Total)
                .Take(options.ReviewCandidateCount)
                // The info JSON is large and isn't needed: choosing a candidate extracts it again.
                .Select(c => c with { Video = c.Video with { InfoJson = null } })
                .ToList();

            if (candidates.FirstOrDefault(c => c.Score.IsAcceptable && !c.HasAcceptableAudio && c.Score.Explicit != ExplicitMatch.Mismatch) is { } unusable)
                return new TrackResolution(track, TrackResolutionState.Unavailable, null, candidates, unusable.FormatProblem, FromCache: false);

            string reason;
            if (query.Explicit is { } wantExplicit && candidates.Any(c => c.Score.IsAcceptable && c.Score.Explicit == ExplicitMatch.Mismatch))
            {
                var (wanted, other) = wantExplicit ? ("explicit", "clean") : ("clean", "explicit");
                reason = candidates.Any(c => c.Score.IsAcceptable && c.Score.Explicit == ExplicitMatch.Unknown)
                    ? $"YouTube Music has both versions, and it isn't clear which one is the {wanted} version in your playlist."
                    : $"YouTube Music only seems to have the {other} version; your playlist has the {wanted} one.";
            }
            else
            {
                reason = candidates is [var closest, ..]
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"No confident match on YouTube Music. The closest result was \"{closest.Video.Track ?? closest.Video.Title}\" by {string.Join(", ", closest.Video.Artists.DefaultIfEmpty(closest.Video.Channel ?? "unknown"))} ({ExportedTrack.FormatDuration(closest.Video.Duration)}).")
                    : "YouTube Music returned no results for this track.";
            }

            return new TrackResolution(track, TrackResolutionState.NeedsReview, null, candidates, reason, FromCache: false);
        }
        catch (YtDlpException ex) when (ex.IsThrottled)
        {
            // Not a review result: nothing was learned about the track. It's remembered only briefly, in memory.
            return new TrackResolution(track, TrackResolutionState.LookupFailed, null, [], YouTubeBusyMessage, FromCache: false) { IsThrottled = true };
        }
        catch (Exception ex) when (ex is YtDlpException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new TrackResolution(track, TrackResolutionState.LookupFailed, null, [], $"Couldn't search YouTube Music. {ex.Message}", FromCache: false);
        }
    }

    /// <summary>What to tell the user when YouTube is refusing requests from this connection.</summary>
    public const string YouTubeBusyMessage = "YouTube is busy (too many requests from this connection). Continuing later.";

    /// <summary>
    /// The best acceptable candidate with usable audio that isn't the wrong explicit or clean version, preferring one
    /// known to be the right version.
    /// </summary>
    private static ResolutionCandidate? PickWinner(IEnumerable<ResolutionCandidate> candidates)
    {
        return candidates
            .Where(c => c.Score.IsAcceptable && c.HasAcceptableAudio && c.Score.Explicit != ExplicitMatch.Mismatch)
            .OrderByDescending(c => c.Score.Explicit == ExplicitMatch.Match)
            .ThenByDescending(c => c.Score.Total)
            .FirstOrDefault();
    }

    /// <summary>
    /// Like <see cref="ExtractCandidatesAsync"/>, but extracts the videos in parallel and leaves out videos that can't
    /// be extracted (e.g. region-locked) rather than failing.
    /// </summary>
    private async Task<IReadOnlyList<ResolutionCandidate>> TryExtractCandidatesAsync(IYouTubeMusicSource source, TrackQuery query, IEnumerable<YouTubeVideoInfo> videos, CancellationToken cancellationToken)
    {
        var tasks = videos.DistinctBy(v => v.Id).Select(async video =>
        {
            try
            {
                var extracted = await ExtractCandidatesAsync(source, query, [video.Url], cancellationToken).ConfigureAwait(false);

                // Keep the search rank, which a single-video extraction doesn't have.
                return extracted
                    .Select(c =>
                    {
                        var ranked = c.Video with { SearchRank = video.SearchRank, IsExplicit = video.IsExplicit ?? c.Video.IsExplicit };
                        return c with { Video = ranked, Score = TrackMatcher.Score(query, ranked) };
                    })
                    .ToList();
            }
            catch (YtDlpException ex) when (!ex.IsThrottled)
            {
                return [];
            }
        });

        return (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(c => c).ToList();
    }

    private async Task<IReadOnlyList<ResolutionCandidate>> ExtractCandidatesAsync(IYouTubeMusicSource source, TrackQuery query, IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        var videos = await source.ExtractAsync(urls, cancellationToken).ConfigureAwait(false);

        return videos
            .Select(video =>
            {
                var selection = AudioFormatSelector.SelectBestAac(video.Formats, options.MinimumBitrateKbps);
                return new ResolutionCandidate(video, TrackMatcher.Score(query, video), selection.Format, selection.FailureReason);
            })
            .ToList();
    }

    private async Task<TrackDownloadResult> DownloadTrackAsync(
        IYouTubeMusicSource source,
        TrackDownloadRequest request,
        Action<TrackDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        YouTubeFormat? format = null;

        TrackDownloadResult Fail(string reason, bool needsReview = false, bool throttled = false)
        {
            if (needsReview) cache.Remove(request.Track);
            progress?.Invoke(new(request, TrackDownloadStage.Failed));
            return new TrackDownloadResult(request, format, null, reason, needsReview) { IsThrottled = throttled };
        }

        var resolution = request.Resolution;
        if (resolution.ResolveWhenDownloading)
        {
            progress?.Invoke(new(request, TrackDownloadStage.Searching));
            resolution = await LookUpSharedAsync(request.Track, null, bypassCache: false, cancellationToken).ConfigureAwait(false);
            if (resolution.State == TrackResolutionState.Skipped)
                return Fail("The track was skipped.");
            if (resolution.State != TrackResolutionState.Resolved)
                return Fail(resolution.Reason ?? "No confident match on YouTube Music.");
        }

        if (resolution is not { State: TrackResolutionState.Resolved, Video: { } video })
            return Fail("The track hasn't been resolved to a YouTube video.");

        if (!Path.IsPathFullyQualified(request.DestinationPath))
            return Fail("The destination path is not fully qualified.");

        var intermediatePath = $"{request.DestinationPath}.{Path.GetRandomFileName()}.download.m4a";
        var succeeded = false;
        var writingDestination = false;
        await lookupAndDownloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var details = resolution.Details is { } fresh && DateTimeOffset.UtcNow - resolution.DetailsExtractedAt < DetailsLifetime
                ? fresh
                : null;

            if (details is null)
            {
                progress?.Invoke(new(request, TrackDownloadStage.Preparing));
                try
                {
                    details = (await ExtractCandidatesAsync(source, TrackQuery.FromExportedTrack(request.Track), [video.Url], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                }
                catch (YtDlpException ex) when (ex.IsThrottled)
                {
                    return Fail(YouTubeBusyMessage, throttled: true);
                }
                catch (YtDlpException ex) when (ex.IsVideoGone)
                {
                    return Fail($"The YouTube video is no longer available. {ex.Message}", needsReview: true);
                }
                catch (YtDlpException ex)
                {
                    // Offline, rate-limited, or a bot check: keep the match (especially a manual pick) and try again later.
                    return Fail($"Couldn't reach YouTube; try again later. {ex.Message}");
                }

                if (details is null)
                    return Fail("Couldn't reach YouTube; try again later.");
            }

            if (details.Format is null)
            {
                // The video exists but has no acceptable audio right now. That can be temporary (yt-dlp sees fewer
                // formats when YouTube changes), so don't throw away a choice the user made; an automatic match is
                // cheap to look up again.
                return video.Source == ResolutionSource.Manual
                    ? Fail($"{details.FormatProblem ?? "The YouTube video has no acceptable audio right now."} Your choice is kept; try again later or choose another video.")
                    : Fail(details.FormatProblem ?? "The YouTube video no longer has acceptable audio.", needsReview: true);
            }

            format = details.Format;
            progress?.Invoke(new(request, TrackDownloadStage.Downloading, 0));

            // If the chosen format disappears before downloading, fall back to any good AAC format.
            var formatSelector = string.Create(CultureInfo.InvariantCulture,
                $"{format.FormatId}/bestaudio[acodec^=mp4a][abr>={options.MinimumBitrateKbps}][protocol=https]");

            await source.DownloadFormatAsync(
                details.Video,
                formatSelector,
                intermediatePath,
                progress is null ? null : new InlineProgress<double>(fraction => progress(new(request, TrackDownloadStage.Downloading, fraction))),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new(request, TrackDownloadStage.Remuxing));

            var downloaded = M4aRemuxer.ReadAudioInfo(intermediatePath);
            if (!downloaded.IsAac)
                return Fail($"YouTube sent {downloaded.Codec} audio instead of AAC.");

            writingDestination = true;
            var audio = M4aRemuxer.RemuxToProgressive(intermediatePath, request.DestinationPath);

            succeeded = true;
            var result = new TrackDownloadResult(request, format, audio, null) { CoverArtUrl = YouTubeCoverArt.SelectUrl(details.Video.Thumbnails) };
            progress?.Invoke(new(request, TrackDownloadStage.Succeeded, 1) { Result = result });
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (YtDlpException ex) when (ex.IsThrottled)
        {
            return Fail(YouTubeBusyMessage, throttled: true);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            lookupAndDownloadSlots.Release();
            TryDelete(intermediatePath);
            if (writingDestination && !succeeded) TryDelete(request.DestinationPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class CompositeDisposable(params IDisposable?[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items) item?.Dispose();
        }
    }

    // Unlike Progress<T>, reports synchronously so that they can't arrive after the stage has moved on.
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Sends each call through <see cref="YouTubeTraffic"/>, charged its estimated requests.</summary>
    private sealed class PacedSource(IYouTubeMusicSource source, YouTubeTraffic traffic) : IYouTubeMusicSource
    {
        public Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string searchText, YouTubeMusicSearchSection section, int maxResults, CancellationToken cancellationToken)
            => traffic.SendAsync(YouTubeTraffic.SearchRequests, ct => source.SearchAsync(searchText, section, maxResults, ct), cancellationToken);

        public Task<IReadOnlyList<YouTubeVideoInfo>> ExtractAsync(IEnumerable<string> videoUrls, CancellationToken cancellationToken)
        {
            var urls = videoUrls.ToList();
            return traffic.SendAsync(YouTubeTraffic.ExtractRequests * Math.Max(1, urls.Count), ct => source.ExtractAsync(urls, ct), cancellationToken);
        }

        public Task DownloadFormatAsync(YouTubeVideoInfo video, string formatSelector, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            // Without the extraction's info JSON, yt-dlp extracts the video again first.
            var requests = video.IsFullyExtracted && video.InfoJson is not null
                ? YouTubeTraffic.DownloadRequests
                : YouTubeTraffic.DownloadRequests + YouTubeTraffic.ExtractRequests;
            return traffic.SendAsync(requests, ct => source.DownloadFormatAsync(video, formatSelector, outputPath, progress, ct), cancellationToken);
        }
    }

    private sealed class YtDlpSource(YtDlp ytDlp) : IYouTubeMusicSource
    {
        public Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string searchText, YouTubeMusicSearchSection section, int maxResults, CancellationToken cancellationToken)
            => ytDlp.SearchYouTubeMusicAsync(searchText, section, maxResults, cancellationToken);

        public Task<IReadOnlyList<YouTubeVideoInfo>> ExtractAsync(IEnumerable<string> videoUrls, CancellationToken cancellationToken)
            => ytDlp.ExtractVideosAsync(videoUrls, cancellationToken);

        public Task DownloadFormatAsync(YouTubeVideoInfo video, string formatSelector, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
            => ytDlp.DownloadFormatAsync(video, formatSelector, outputPath, progress, cancellationToken);
    }
}

internal interface IYouTubeMusicSource
{
    /// <summary>Returns flat search results, which have only an ID, title, and search rank.</summary>
    Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string searchText, YouTubeMusicSearchSection section, int maxResults, CancellationToken cancellationToken);

    /// <summary>Returns full metadata, leaving out videos that can't be extracted.</summary>
    Task<IReadOnlyList<YouTubeVideoInfo>> ExtractAsync(IEnumerable<string> videoUrls, CancellationToken cancellationToken);

    Task DownloadFormatAsync(YouTubeVideoInfo video, string formatSelector, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken);
}
