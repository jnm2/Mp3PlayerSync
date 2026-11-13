using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// Remembers which YouTube video each Spotify track resolved to (or that it was skipped), so that it isn't looked up
/// again. Keyed by Spotify track URI, falling back to ISRC. Thread-safe.
/// </summary>
internal sealed class TrackResolutionCache
{
    private readonly string? path;
    private readonly Lock lockObject = new();
    private readonly List<TrackResolutionCacheEntry> entries;
    private bool isDirty;

    private TrackResolutionCache(string? path, List<TrackResolutionCacheEntry> entries)
    {
        this.path = path;
        this.entries = entries;
    }

    /// <summary>An in-memory cache that is never saved.</summary>
    public static TrackResolutionCache CreateInMemory() => new(null, []);

    /// <summary>
    /// Loads the cache file, or starts empty if it doesn't exist or can't be read.
    /// </summary>
    public static TrackResolutionCache Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize(stream, TrackResolutionCacheJsonContext.Default.TrackResolutionCacheFile);
            return new(path, file?.Entries.Where(e => e.TrackUri is { Length: > 0 } || e.Isrc is { Length: > 0 }).ToList() ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(path, []);
        }
    }

    public TrackResolutionCacheEntry? TryGet(ExportedTrack track)
    {
        lock (lockObject) return Find(track);
    }

    public void Set(ExportedTrack track, TrackResolutionCacheEntry entry)
    {
        if (!IsCacheable(track)) return;

        lock (lockObject)
        {
            RemoveCore(track);
            entries.Add(entry with { TrackUri = NullIfEmpty(track.Uri), Isrc = NullIfEmpty(track.Isrc) });
            isDirty = true;
        }
    }

    public bool Remove(ExportedTrack track)
    {
        lock (lockObject)
        {
            var removed = RemoveCore(track);
            isDirty |= removed;
            return removed;
        }
    }

    public void Save()
    {
        if (path is null) return;

        lock (lockObject)
        {
            if (!isDirty) return;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new TrackResolutionCacheFile { Entries = entries.Where(e => !e.IsTransient).ToList() }, TrackResolutionCacheJsonContext.Default.TrackResolutionCacheFile));
            File.Move(temporary, path, overwrite: true);
            isDirty = false;
        }
    }

    public static bool IsCacheable(ExportedTrack track) => track.Uri.Length > 0 || track.Isrc.Length > 0;

    private TrackResolutionCacheEntry? Find(ExportedTrack track)
    {
        if (track.Uri.Length > 0 && entries.FindLast(e => e.TrackUri == track.Uri) is { } byUri)
            return byUri;

        // The same recording can have several Spotify URIs (e.g. on an album and a compilation).
        if (track.Isrc.Length > 0 && entries.FindLast(e => string.Equals(e.Isrc, track.Isrc, StringComparison.OrdinalIgnoreCase)) is { } byIsrc)
            return byIsrc;

        return null;
    }

    private bool RemoveCore(ExportedTrack track)
    {
        var removed = false;
        while (Find(track) is { } entry)
        {
            entries.Remove(entry);
            removed = true;
        }

        return removed;
    }

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;
}

internal sealed record TrackResolutionCacheEntry
{
    public string? TrackUri { get; init; }
    public string? Isrc { get; init; }

    /// <summary>
    /// <see cref="TrackResolutionState.NeedsReview"/>, <see cref="TrackResolutionState.Unavailable"/>, or <see
    /// cref="TrackResolutionState.LookupFailed"/>; null for a resolved or skipped track (see <see cref="Source"/>).
    /// </summary>
    public TrackResolutionState? State { get; init; }

    /// <summary>Null when <see cref="Source"/> is <see cref="ResolutionSource.Skipped"/> or the track is unresolved.</summary>
    public string? VideoId { get; init; }

    public ResolutionSource Source { get; init; }
    public double? Score { get; init; }
    public DateTimeOffset ResolvedAt { get; init; }

    // For showing what the track resolved to without looking it up again.
    public string? Title { get; init; }
    public IReadOnlyList<string>? Artists { get; init; }
    public string? Album { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>Why the track needs review, is unavailable, or couldn't be looked up.</summary>
    public string? Reason { get; init; }

    /// <summary>The candidates offered for review.</summary>
    public IReadOnlyList<CachedResolutionCandidate>? Candidates { get; init; }

    /// <summary>Kept for this run only (lookup failures), so that the lookup is retried next run.</summary>
    [JsonIgnore]
    public bool IsTransient { get; init; }

    /// <summary>For a lookup failure: whether YouTube was throttling this connection.</summary>
    [JsonIgnore]
    public bool IsThrottled { get; init; }

    public static TrackResolutionCacheEntry ForUnresolved(TrackResolution resolution, DateTimeOffset now) => new()
    {
        State = resolution.State,
        ResolvedAt = now,
        Reason = resolution.Reason,
        Candidates = resolution.Candidates.Select(CachedResolutionCandidate.From).ToList(),
        IsTransient = resolution.State == TrackResolutionState.LookupFailed,
        IsThrottled = resolution.IsThrottled,
    };

    public static TrackResolutionCacheEntry From(ResolvedVideo video) => new()
    {
        VideoId = video.VideoId,
        Source = video.Source,
        Score = video.Score,
        ResolvedAt = video.ResolvedAt,
        Title = video.Title,
        Artists = video.Artists,
        Album = video.Album,
        DurationSeconds = video.Duration?.TotalSeconds,
    };

    public ResolvedVideo? ToResolvedVideo() => VideoId is null || Source == ResolutionSource.Skipped || State is not null
        ? null
        : new ResolvedVideo(VideoId, Title ?? "", Artists ?? [], Album, DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, Score, Source, ResolvedAt);
}

/// <summary>
/// A review candidate as remembered. Formats other than the one that would be downloaded aren't kept.
/// </summary>
internal sealed record CachedResolutionCandidate
{
    public required string VideoId { get; init; }
    public string? Title { get; init; }
    public string? Track { get; init; }
    public IReadOnlyList<string>? Artists { get; init; }
    public string? Album { get; init; }
    public string? Channel { get; init; }
    public double? DurationSeconds { get; init; }
    public int? SearchRank { get; init; }
    public bool? IsExplicit { get; init; }
    public required MatchScore Score { get; init; }
    public YouTubeFormat? Format { get; init; }
    public string? FormatProblem { get; init; }

    public static CachedResolutionCandidate From(ResolutionCandidate candidate) => new()
    {
        VideoId = candidate.Video.Id,
        Title = candidate.Video.Title,
        Track = candidate.Video.Track,
        Artists = candidate.Video.Artists,
        Album = candidate.Video.Album,
        Channel = candidate.Video.Channel,
        DurationSeconds = candidate.Video.Duration?.TotalSeconds,
        SearchRank = candidate.Video.SearchRank,
        IsExplicit = candidate.Video.IsExplicit,
        Score = candidate.Score,
        Format = candidate.Format,
        FormatProblem = candidate.FormatProblem,
    };

    public ResolutionCandidate ToCandidate() => new(
        new YouTubeVideoInfo(
            VideoId,
            YouTubeUrls.GetMusicWatchUrl(VideoId),
            Title ?? "",
            Track,
            Artists ?? [],
            Album,
            Channel,
            DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            SearchRank,
            Formats: []) { IsExplicit = IsExplicit },
        Score,
        Format,
        FormatProblem);
}

internal sealed class TrackResolutionCacheFile
{
    public int Version { get; init; } = 1;
    public List<TrackResolutionCacheEntry> Entries { get; init; } = [];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrackResolutionCacheFile))]
internal sealed partial class TrackResolutionCacheJsonContext : JsonSerializerContext;
