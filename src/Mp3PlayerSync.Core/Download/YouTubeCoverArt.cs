using System.Globalization;
using System.Text.RegularExpressions;
using Mp3PlayerSync.Core.Tagging;

namespace Mp3PlayerSync.Core.Download;

/// <summary>One entry of yt-dlp's <c>thumbnails</c> list. The size is only known for some.</summary>
public sealed record YouTubeThumbnail(string Url, int? Width, int? Height);

/// <summary>
/// Picks the cover art for a YouTube Music result from its thumbnails.
/// </summary>
public static partial class YouTubeCoverArt
{
    /// <summary>
    /// The square album art of a song (on Google's image CDN, asked for at <see cref="PlayerCoverArt.MaxSize"/>), or
    /// else, for a music video, a 16:9 thumbnail, which is center-cropped to a square when embedded. Null if there's
    /// neither.
    /// </summary>
    public static string? SelectUrl(IReadOnlyList<YouTubeThumbnail> thumbnails)
    {
        // Songs' album art, e.g. https://yt3.googleusercontent.com/…=w544-h544-l90-rj
        var square = thumbnails.Where(t => IsGoogleImage(t.Url) && IsSquare(t)).MaxBy(t => t.Width ?? 0);
        if (square is not null) return Resize(square.Url, PlayerCoverArt.MaxSize);

        // Video thumbnails, e.g. https://i.ytimg.com/vi/<id>/hq720.jpg. Only those with a known size: the others are
        // yt-dlp's guesses, which may not exist or be 4:3 with black bars. The smallest that's still tall enough.
        var frames = thumbnails
            .Where(t => IsVideoThumbnailJpeg(t.Url) && t is { Width: > 0, Height: > 0 } && Math.Abs((double)t.Width!.Value / t.Height!.Value - 16.0 / 9) < 0.05)
            .ToList();
        return (frames.Where(t => t.Height >= PlayerCoverArt.MaxSize).MinBy(t => t.Height) ?? frames.MaxBy(t => t.Height))?.Url;
    }

    private static bool IsGoogleImage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoThumbnailJpeg(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/vi/", StringComparison.Ordinal)
        && uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    private static bool IsSquare(YouTubeThumbnail thumbnail)
    {
        if (thumbnail is { Width: { } width, Height: { } height }) return width == height && width > 0;
        return SizeOptions().Match(thumbnail.Url) is { Success: true } match && match.Groups["w"].Value == match.Groups["h"].Value;
    }

    /// <summary>
    /// Changes the size Google's image CDN is asked for, which is in the options after the URL's last <c>=</c>.
    /// URLs without a size are left as they are.
    /// </summary>
    private static string Resize(string url, int size)
    {
        var equals = url.LastIndexOf('=');
        if (equals < 0 || url.IndexOf('/', equals) >= 0) return url;

        var options = url[(equals + 1)..].Split('-');
        for (var i = 0; i < options.Length; i++)
        {
            if (SizeOption().IsMatch(options[i]))
                options[i] = options[i][0] + size.ToString(CultureInfo.InvariantCulture);
        }

        return url[..(equals + 1)] + string.Join('-', options);
    }

    [GeneratedRegex(@"=(?:[^/=]*-)?w(?<w>\d+)-h(?<h>\d+)(?:-[^/=]*)?$")]
    private static partial Regex SizeOptions();

    [GeneratedRegex(@"^[whs]\d+$")]
    private static partial Regex SizeOption();
}
