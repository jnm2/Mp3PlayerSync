namespace Mp3PlayerSync.Core.Download;

public enum TrackResolutionState
{
    /// <summary>A confident automatic match, a manual choice, or either of those remembered from before.</summary>
    Resolved,

    /// <summary>No confident match. The user should pick a candidate, paste a URL, or skip the track.</summary>
    NeedsReview,

    /// <summary>
    /// A confident match whose audio can't be used (no AAC, or only low-quality AAC). Like <see cref="NeedsReview"/>,
    /// the user should pick another candidate, paste a URL, or skip the track.
    /// </summary>
    Unavailable,

    /// <summary>The user chose not to download this track.</summary>
    Skipped,

    /// <summary>
    /// Searching failed, e.g. because there is no internet connection or YouTube changed in a way that needs a newer
    /// yt-dlp. Nothing is remembered; resolve the track again later.
    /// </summary>
    LookupFailed,
}

public enum ResolutionSource
{
    Automatic,
    Manual,
    Skipped,
}

/// <summary>
/// A YouTube Music result with full metadata, as offered for review.
/// </summary>
/// <param name="Format">The AAC format that would be downloaded, or <see langword="null"/> if there is none.</param>
/// <param name="FormatProblem">Why there is no acceptable format, if there isn't one.</param>
public sealed record ResolutionCandidate(YouTubeVideoInfo Video, MatchScore Score, YouTubeFormat? Format, string? FormatProblem)
{
    public bool HasAcceptableAudio => Format is not null;
}

/// <summary>
/// The YouTube video a track resolved to.
/// </summary>
/// <param name="Score">The match score when chosen automatically or from a candidate list.</param>
public sealed record ResolvedVideo(
    string VideoId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    TimeSpan? Duration,
    double? Score,
    ResolutionSource Source,
    DateTimeOffset ResolvedAt)
{
    public string Url => YouTubeUrls.GetMusicWatchUrl(VideoId);

    internal static ResolvedVideo From(ResolutionCandidate candidate, ResolutionSource source, DateTimeOffset resolvedAt) => new(
        candidate.Video.Id,
        candidate.Video.Track ?? candidate.Video.Title,
        candidate.Video.Artists.Count > 0 ? candidate.Video.Artists : candidate.Video.Channel is { } channel ? [channel] : [],
        candidate.Video.Album,
        candidate.Video.Duration,
        candidate.Score.Total,
        source,
        resolvedAt);
}

/// <param name="Video">The video to download, when <see cref="State"/> is <see cref="TrackResolutionState.Resolved"/>.</param>
/// <param name="Candidates">
/// The best YouTube Music results with full metadata, best first, when the track needs review.
/// </param>
/// <param name="Reason">Why the track needs review or is unavailable.</param>
/// <param name="FromCache">Whether this was remembered from before rather than looked up.</param>
public sealed record TrackResolution(
    ExportedTrack Track,
    TrackResolutionState State,
    ResolvedVideo? Video,
    IReadOnlyList<ResolutionCandidate> Candidates,
    string? Reason,
    bool FromCache)
{
    public bool NeedsReview => State is TrackResolutionState.NeedsReview or TrackResolutionState.Unavailable;

    /// <summary>
    /// Freshly extracted details of <see cref="Video"/>, which let the download skip extracting the video again.
    /// </summary>
    internal ResolutionCandidate? Details { get; init; }

    internal DateTimeOffset DetailsExtractedAt { get; init; }

    /// <summary>Set for download requests made without resolving first.</summary>
    internal bool ResolveWhenDownloading { get; init; }

    /// <summary>
    /// For <see cref="TrackResolutionState.LookupFailed"/>: whether YouTube refused the lookup because this connection
    /// made too many requests (429, bot check). Stop all YouTube traffic for a while, then look the track up again.
    /// </summary>
    public bool IsThrottled { get; init; }
}

public enum TrackResolutionStage
{
    Queued,
    Searching,
    Completed,
}

/// <param name="Resolution">The result, when <see cref="Stage"/> is <see cref="TrackResolutionStage.Completed"/>.</param>
public sealed record TrackResolutionProgress(ExportedTrack Track, TrackResolutionStage Stage, TrackResolution? Resolution = null);

/// <summary>
/// The result of applying a user's choice. Either <see cref="Resolution"/> or <see cref="Error"/> is set.
/// </summary>
public sealed record ManualChoiceResult(TrackResolution? Resolution, string? Error)
{
    public bool Succeeded => Resolution is not null;

    /// <summary>Whether the video couldn't be checked because YouTube is refusing this connection's requests.</summary>
    public bool IsThrottled { get; init; }
}
