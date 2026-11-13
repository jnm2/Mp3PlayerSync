using System.Collections.Concurrent;
using System.Net;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Core.Tagging;

public sealed record AlbumArt(byte[] Data, string MimeType)
{
    /// <summary>Recognizes JPEG and PNG data; returns null for anything else.</summary>
    public static AlbumArt? FromBytes(byte[] data)
    {
        if (data is [0xFF, 0xD8, 0xFF, ..]) return new AlbumArt(data, "image/jpeg");
        if (data is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..]) return new AlbumArt(data, "image/png");
        return null;
    }
}

public interface IAlbumArtFetcher
{
    /// <summary>Downloads cover art. Returns null when the URL isn't usable or the data isn't an image.</summary>
    Task<AlbumArt?> FetchAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads cover art from Spotify's image CDN (i.scdn.co) or YouTube's (Google's image CDN and i.ytimg.com). Never
/// calls the Spotify Web API. Results are cached per URL, since every track of an album shares one image.
/// </summary>
/// <param name="converter">
/// Turns art that isn't already <see cref="PlayerCoverArt"/> into it; without one, art is embedded as downloaded.
/// </param>
/// <param name="youTubeTraffic">Paces requests to YouTube's image CDNs with the app's other YouTube requests.</param>
public sealed class HttpAlbumArtFetcher(HttpClient httpClient, IAlbumArtConverter? converter = null, YouTubeTraffic? youTubeTraffic = null) : IAlbumArtFetcher
{
    private const int MaxBytes = 10 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Lazy<Task<AlbumArt?>>> cache = new(StringComparer.Ordinal);

    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("i.scdn.co", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    public Task<AlbumArt?> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(url)) return Task.FromResult<AlbumArt?>(null);

        var lazy = cache.GetOrAdd(url, u => new Lazy<Task<AlbumArt?>>(() => DownloadAsync(u)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private async Task<AlbumArt?> DownloadAsync(string url)
    {
        AlbumArt? art = null;
        try
        {
            var data = youTubeTraffic is not null && IsYouTube(url)
                ? await youTubeTraffic.SendAsync(YouTubeTraffic.CoverArtRequests, _ => GetAsync(url), CancellationToken.None).ConfigureAwait(false)
                : await GetAsync(url).ConfigureAwait(false);
            if (data is not null)
            {
                art = AlbumArt.FromBytes(data);
                if (art is not null && converter is not null && !PlayerCoverArt.IsReady(data))
                    art = await converter.ConvertAsync(data, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }

        // Only successes are cached, so a transient failure is retried by the album's next track.
        if (art is null) cache.TryRemove(url, out _);
        return art;
    }

    private static bool IsYouTube(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <returns>The image's bytes, or null if it's too large or not there.</returns>
    /// <exception cref="HttpRequestException">Thrown for 403 and 429, which can mean YouTube is pushing back.</exception>
    private async Task<byte[]?> GetAsync(string url)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"The image request was refused ({(int)response.StatusCode}).", null, response.StatusCode);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxBytes) return null;

        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return data.Length <= MaxBytes ? data : null;
    }
}
