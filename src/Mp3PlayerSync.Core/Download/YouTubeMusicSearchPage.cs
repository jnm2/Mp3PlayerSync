using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// Reads YouTube Music search responses (as saved by yt-dlp's <c>--write-pages</c>) for what yt-dlp's own output
/// leaves out.
/// </summary>
internal static partial class YouTubeMusicSearchPage
{
    /// <summary>
    /// Returns whether each song in the search response has YouTube Music's explicit badge. Songs the response
    /// doesn't describe (or that can't be read because the format changed) are left out, so they count as unknown.
    /// </summary>
    public static IEnumerable<(string VideoId, bool IsExplicit)> ReadExplicitBadges(string page)
    {
        // Each result is a musicResponsiveListItemRenderer; shelves and cards separate groups of them.
        var parts = RendererRegex().Split(page);
        foreach (var part in parts.Skip(1))
        {
            if (!part.StartsWith("ResponsiveListItem", StringComparison.Ordinal)) continue;

            var videoId = VideoIdRegex().Match(part);
            if (!videoId.Success) continue;

            yield return (videoId.Groups[1].Value, part.Contains("MUSIC_EXPLICIT_BADGE", StringComparison.Ordinal));
        }
    }

    [GeneratedRegex("\"music(?=(?:ResponsiveListItem|CardShelf|Shelf)Renderer\")")]
    private static partial Regex RendererRegex();

    [GeneratedRegex("\"videoId\"\\s*:\\s*\"([A-Za-z0-9_-]{11})\"")]
    private static partial Regex VideoIdRegex();
}
