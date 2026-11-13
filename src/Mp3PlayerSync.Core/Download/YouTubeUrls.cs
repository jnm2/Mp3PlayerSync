using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Download;

public static partial class YouTubeUrls
{
    public static string GetMusicWatchUrl(string videoId) => "https://music.youtube.com/watch?v=" + Uri.EscapeDataString(videoId);

    /// <summary>
    /// Gets the video ID from a YouTube or YouTube Music video URL (watch, youtu.be, shorts, embed, live) or a bare
    /// video ID. Playlist and channel URLs are rejected.
    /// </summary>
    public static bool TryGetVideoId(string input, out string videoId, out string? error)
    {
        videoId = "";
        error = null;
        var text = input.Trim();

        if (VideoIdRegex().IsMatch(text))
        {
            videoId = text;
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "That isn't a YouTube or YouTube Music link.";
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];

        string? candidate = null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host == "youtu.be")
        {
            candidate = segments.FirstOrDefault();
        }
        else if (host is "youtube.com" or "music.youtube.com" or "youtube-nocookie.com")
        {
            if (segments is ["watch"])
                candidate = GetQueryParameter(uri, "v");
            else if (segments is ["shorts" or "embed" or "live" or "v", var id, ..])
                candidate = id;
        }
        else
        {
            error = "That isn't a YouTube or YouTube Music link.";
            return false;
        }

        if (candidate is null || !VideoIdRegex().IsMatch(candidate))
        {
            error = GetQueryParameter(uri, "list") is not null || segments is ["playlist", ..] or ["browse", ..] or ["channel", ..]
                ? "That link is to a playlist, album, or channel. Paste a link to a single song or video."
                : "That link isn't to a YouTube video.";
            return false;
        }

        videoId = candidate;
        return true;
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            if (key == name) return separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();
}
