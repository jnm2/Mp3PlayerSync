using System.Globalization;
using System.Text.Json;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// The parts of a yt-dlp info JSON object (from <c>-j</c>/<c>-J</c>) that matching and format selection need.
/// </summary>
public sealed record YouTubeVideoInfo(
    string Id,
    string Url,
    string Title,
    string? Track,
    IReadOnlyList<string> Artists,
    string? Album,
    string? Channel,
    TimeSpan? Duration,
    int? SearchRank,
    IReadOnlyList<YouTubeFormat> Formats)
{
    /// <summary>
    /// The JSON this was parsed from, which yt-dlp can download from (<c>--load-info-json</c>) without extracting the
    /// video again.
    /// </summary>
    public string? InfoJson { get; init; }

    /// <summary>
    /// Whether YouTube Music shows its explicit badge on this song in search results, or null if unknown (yt-dlp's
    /// info JSON doesn't include it; it's read from the search page).
    /// </summary>
    public bool? IsExplicit { get; init; }

    /// <summary>
    /// Whether this has full metadata, as opposed to a flat search result, which only has an ID and title.
    /// </summary>
    public bool IsFullyExtracted => Formats.Count > 0;

    public IReadOnlyList<YouTubeThumbnail> Thumbnails { get; init; } = [];

    /// <summary>
    /// Parses one yt-dlp info JSON object.
    /// </summary>
    public static YouTubeVideoInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement) with { InfoJson = json };
    }

    /// <summary>
    /// Parses yt-dlp <c>-j</c> output, which is one info JSON object per line.
    /// </summary>
    public static IReadOnlyList<YouTubeVideoInfo> ParseLines(IEnumerable<string> lines)
    {
        var results = new List<YouTubeVideoInfo>();
        foreach (var line in lines)
        {
            if (!line.StartsWith('{')) continue;
            results.Add(Parse(line));
        }

        return results;
    }

    public static YouTubeVideoInfo Parse(JsonElement element)
    {
        var id = GetString(element, "id") ?? throw new InvalidDataException("The yt-dlp info JSON has no id.");

        var artists = GetStringArray(element, "artists");
        if (artists.Count == 0 && GetString(element, "artist") is { } artist)
            artists = artist.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (artists.Count == 0)
            artists = GetStringArray(element, "creators");

        var formats = new List<YouTubeFormat>();
        if (element.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                if (GetString(format, "format_id") is not { } formatId) continue;

                formats.Add(new YouTubeFormat(
                    FormatId: formatId,
                    Extension: GetString(format, "ext"),
                    AudioCodec: GetString(format, "acodec"),
                    VideoCodec: GetString(format, "vcodec"),
                    AudioBitrate: GetDouble(format, "abr"),
                    SampleRate: GetDouble(format, "asr") is { } asr ? (int)asr : null,
                    Protocol: GetString(format, "protocol"),
                    FormatNote: GetString(format, "format_note"),
                    LanguagePreference: GetDouble(format, "language_preference") is { } preference ? (int)preference : null,
                    HasDrm: element.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True
                        || format.TryGetProperty("has_drm", out drm) && drm.ValueKind == JsonValueKind.True));
            }
        }

        return new YouTubeVideoInfo(
            Id: id,
            Url: YouTubeUrls.GetMusicWatchUrl(id),
            Title: GetString(element, "title") ?? "",
            Track: GetString(element, "track"),
            Artists: artists,
            Album: GetString(element, "album"),
            Channel: GetString(element, "channel") ?? GetString(element, "uploader"),
            Duration: GetDouble(element, "duration") is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            SearchRank: GetDouble(element, "playlist_index") is { } index ? (int)index : null,
            Formats: formats)
        {
            Thumbnails = GetThumbnails(element),
        };
    }

    private static IReadOnlyList<YouTubeThumbnail> GetThumbnails(JsonElement element)
    {
        if (!element.TryGetProperty("thumbnails", out var value) || value.ValueKind != JsonValueKind.Array) return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => GetString(item, "url") is { } url
                ? new YouTubeThumbnail(url, GetDouble(item, "width") is { } width ? (int)width : null, GetDouble(item, "height") is { } height ? (int)height : null)
                : null)
            .OfType<YouTubeThumbnail>()
            .ToList();
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .ToList();
    }
}

/// <summary>
/// One entry of yt-dlp's <c>formats</c> list.
/// </summary>
/// <param name="AudioBitrate">Average audio bitrate in kbps.</param>
public sealed record YouTubeFormat(
    string FormatId,
    string? Extension,
    string? AudioCodec,
    string? VideoCodec,
    double? AudioBitrate,
    int? SampleRate,
    string? Protocol,
    string? FormatNote,
    int? LanguagePreference,
    bool HasDrm)
{
    public bool IsAudioOnly => VideoCodec is "none" && AudioCodec is { } codec && codec != "none";

    public bool IsAac => AudioCodec is { } codec && codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase);

    public bool IsOpus => AudioCodec is { } codec && codec.Contains("opus", StringComparison.OrdinalIgnoreCase);

    /// <summary>YouTube's dynamic-range-compressed variants, which have a squashed dynamic range.</summary>
    public bool IsDrc => FormatId.Contains("drc", StringComparison.OrdinalIgnoreCase)
        || (FormatNote?.Contains("DRC", StringComparison.OrdinalIgnoreCase) ?? false);

    public override string ToString() => $"{FormatId} ({AudioCodec}, {AudioBitrate?.ToString("0", CultureInfo.InvariantCulture) ?? "?"} kbps)";
}
