using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mp3PlayerSync.Core.Sync;

/// <param name="Path">The cached .m4a: the download as remuxed, before tagging. Copy it; don't change it.</param>
/// <param name="FormatId">The YouTube format it was downloaded in.</param>
/// <param name="CoverArtUrl">The cover art of the YouTube match, for tagging.</param>
public sealed record CachedDownload(string VideoId, string FormatId, string Path, string? CoverArtUrl);

/// <summary>
/// Finished downloads, kept on the PC (never the player) until they've been copied to the player, so a sync that is
/// cancelled, closed or crashes resumes without downloading them again.
/// </summary>
/// <remarks>
/// Entries are keyed by YouTube video ID and format: "&lt;video ID&gt;.&lt;format ID&gt;.m4a", with a small JSON file
/// beside it. Entries are added with a copy to a temporary name and a rename, so a crash never leaves a partial one.
/// <see cref="Prune"/> removes entries older than <see cref="MaxAge"/>, then the oldest ones until the cache fits
/// <see cref="MaxBytes"/>.
/// </remarks>
public sealed class DownloadCache(string directory)
{
    private const string TemporarySuffix = ".tmp";

    public string Directory { get; } = System.IO.Path.GetFullPath(directory);

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    public long MaxBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>The cached download of a video, in whatever format it was downloaded.</summary>
    public CachedDownload? TryGet(string videoId)
    {
        if (!IsValidVideoId(videoId) || !System.IO.Directory.Exists(Directory)) return null;

        foreach (var audio in System.IO.Directory.EnumerateFiles(Directory, videoId + ".*.m4a"))
        {
            var formatId = System.IO.Path.GetFileNameWithoutExtension(audio)[(videoId.Length + 1)..];
            var info = ReadInfo(InfoPath(videoId, formatId));
            if (info is null) continue;
            return new CachedDownload(videoId, formatId, audio, info.CoverArtUrl);
        }

        return null;
    }

    /// <summary>Copies a finished, untagged download into the cache. Never throws for I/O problems: the cache is best effort.</summary>
    public void Add(string videoId, string formatId, string? coverArtUrl, string downloadPath)
    {
        if (!IsValidVideoId(videoId) || !IsValidFormatId(formatId)) return;

        var audio = AudioPath(videoId, formatId);
        var temporary = audio + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(downloadPath, temporary);
            File.WriteAllText(InfoPath(videoId, formatId), JsonSerializer.Serialize(new Info(coverArtUrl, DateTimeOffset.UtcNow), DownloadCacheJsonContext.Default.Info));
            File.Move(temporary, audio, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Forgets a video's cached downloads, e.g. once it's on the player.</summary>
    public void Remove(string videoId)
    {
        if (!IsValidVideoId(videoId) || !System.IO.Directory.Exists(Directory)) return;

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, videoId + ".*"))
            TryDelete(file);
    }

    /// <summary>Removes leftovers, entries older than <see cref="MaxAge"/>, then the oldest entries until the cache fits <see cref="MaxBytes"/>.</summary>
    public void Prune(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(Directory)) return;

        var entries = new List<(string VideoId, DateTimeOffset SavedAt, long Size)>();
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles())
        {
            if (file.Name.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase))
            {
                // A copy cut short by a crash. A running sync's copy is too new to touch.
                if (now - file.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(file.FullName);
                continue;
            }

            if (!file.Name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = System.IO.Path.GetFileNameWithoutExtension(file.Name).Split('.', 2);
            var info = parts.Length == 2 ? ReadInfo(InfoPath(parts[0], parts[1])) : null;
            if (info is null)
            {
                TryDelete(file.FullName);
                continue;
            }

            entries.Add((parts[0], info.SavedAt, file.Length));
        }

        var total = entries.Sum(e => e.Size);
        foreach (var entry in entries.OrderBy(e => e.SavedAt))
        {
            if (now - entry.SavedAt <= MaxAge && total <= MaxBytes) break;
            Remove(entry.VideoId);
            total -= entry.Size;
        }

        // Info files whose audio is gone.
        foreach (var info in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (!File.Exists(System.IO.Path.ChangeExtension(info, ".m4a"))) TryDelete(info);
        }
    }

    private string AudioPath(string videoId, string formatId) => System.IO.Path.Combine(Directory, $"{videoId}.{formatId}.m4a");

    private string InfoPath(string videoId, string formatId) => System.IO.Path.Combine(Directory, $"{videoId}.{formatId}.json");

    private static Info? ReadInfo(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), DownloadCacheJsonContext.Default.Info) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // YouTube video IDs are 11 characters of [A-Za-z0-9_-]; anything else must not reach a file name.
    private static bool IsValidVideoId(string videoId) =>
        videoId.Length is > 0 and <= 64 && videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool IsValidFormatId(string formatId) =>
        formatId.Length is > 0 and <= 64 && formatId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed record Info(string? CoverArtUrl, DateTimeOffset SavedAt);

    /// <summary>The format ID used when the downloader didn't say which format it downloaded.</summary>
    public static string FormatIdOrUnknown(string? formatId) =>
        formatId is { Length: > 0 } id && IsValidFormatId(id) ? id : "unknown";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DownloadCache.Info))]
internal sealed partial class DownloadCacheJsonContext : JsonSerializerContext;
