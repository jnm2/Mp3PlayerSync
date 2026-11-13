using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// What to look for on YouTube Music.
/// </summary>
/// <param name="Explicit">Whether the playlist has the explicit version (true) or the clean one (false), if known.</param>
public sealed record TrackQuery(string Title, IReadOnlyList<string> Artists, string? Album, TimeSpan? Duration, bool? Explicit = null)
{
    public static TrackQuery FromExportedTrack(ExportedTrack track) =>
        new(track.Title, track.Artists, track.Album is { Length: > 0 } album ? album : null, track.Duration,
            track.Explicit ?? TrackMatcher.GetExplicitMarker(track.Title) ?? (track.Album is { Length: > 0 } a ? TrackMatcher.GetExplicitMarker(a) : null));

    /// <summary>
    /// The text to type into the YouTube Music search box.
    /// </summary>
    public string SearchText
    {
        get
        {
            // "Song - Remastered 2011" finds the same results as "Song", and sometimes better ones.
            var title = Regex.Replace(Title, @"\s+-\s+[^-]*\bremaster(ed)?\b.*$", "", RegexOptions.IgnoreCase);
            return Artists.Count > 0 ? $"{title} {string.Join(" ", Artists)}" : title;
        }
    }
}

/// <param name="Total">The overall score; higher is better.</param>
/// <param name="IsAcceptable">Whether the candidate is a confident enough match to download.</param>
public sealed record MatchScore(double Total, double Title, double Artist, double Duration, double Album, int VersionMismatches, bool IsAcceptable)
{
    /// <summary>Whether the candidate is the same explicit or clean version as the playlist's track.</summary>
    public ExplicitMatch Explicit { get; init; }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Total:0.00} (title {Title:0.00}, artist {Artist:0.00}, duration {Duration:0.00}, album {Album:0.00}, version mismatches {VersionMismatches}, explicit {Explicit})");
}

public enum ExplicitMatch
{
    /// <summary>The playlist's version or the candidate's isn't known.</summary>
    Unknown,

    Match,

    /// <summary>The candidate is the explicit version and the playlist has the clean one, or vice versa.</summary>
    Mismatch,
}

public sealed record ScoredCandidate(YouTubeVideoInfo Video, MatchScore Score);

/// <summary>
/// Scores YouTube Music search results against a track by title, artist and duration closeness.
/// </summary>
public static partial class TrackMatcher
{
    /// <summary>
    /// Words that make a recording a different version of a song. A candidate whose title has one of these words
    /// when the track's doesn't (or vice versa) is very likely the wrong recording.
    /// </summary>
    private static readonly string[] VersionKeywords =
    [
        "live", "remix", "acoustic", "instrumental", "karaoke", "cover", "demo", "sped up", "slowed", "reverb",
        "nightcore", "8d", "reprise", "unplugged", "a cappella", "acapella", "orchestral", "piano version", "lofi", "lo fi",
    ];

    public static IReadOnlyList<ScoredCandidate> Rank(TrackQuery query, IEnumerable<YouTubeVideoInfo> candidates)
    {
        return candidates
            .Select(c => new ScoredCandidate(c, Score(query, c)))
            .OrderByDescending(c => c.Score.IsAcceptable)
            .ThenByDescending(c => c.Score.Total)
            .ToList();
    }

    /// <summary>
    /// Orders flat search results, which only have a title, by how plausible they are, leaving out those whose title
    /// doesn't match or that are a different version (live, remix, etc.). Plausible results still need to be
    /// verified with full metadata using <see cref="Score"/>.
    /// </summary>
    public static IReadOnlyList<ScoredCandidate> RankFlat(TrackQuery query, IEnumerable<YouTubeVideoInfo> candidates)
    {
        return candidates
            .Select(c => new ScoredCandidate(c, ScoreFlat(query, c)))
            .Where(c => c.Score.IsAcceptable)
            .OrderByDescending(c => c.Score.Total >= 0.9 ? 1 : c.Score.Total)
            // Of equally good titles, verify the playlist's explicit or clean version first.
            .ThenBy(c => c.Score.Explicit switch { ExplicitMatch.Match => 0, ExplicitMatch.Unknown => 1, _ => 2 })
            .ThenBy(c => c.Video.SearchRank ?? int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Whether the candidate is the playlist's explicit or clean version, judged by YouTube Music's explicit badge or,
    /// failing that, an "(Explicit)", "(Clean)" or "(Edited)" marker in its title or album.
    /// </summary>
    public static ExplicitMatch GetExplicitMatch(TrackQuery query, YouTubeVideoInfo candidate)
    {
        if (query.Explicit is not { } wanted) return ExplicitMatch.Unknown;

        var isExplicit = candidate.IsExplicit
            ?? GetExplicitMarker(candidate.Track ?? candidate.Title)
            ?? GetExplicitMarker(candidate.Title)
            ?? (candidate.Album is { } album ? GetExplicitMarker(album) : null);

        return isExplicit is not { } actual ? ExplicitMatch.Unknown
            : actual == wanted ? ExplicitMatch.Match
            : ExplicitMatch.Mismatch;
    }

    /// <summary>
    /// True for an "(Explicit)" marker, false for "(Clean)" or "(Edited)", or null if there's neither.
    /// </summary>
    public static bool? GetExplicitMarker(string text) =>
        ExplicitMarkerRegex().IsMatch(text) ? true
        : CleanMarkerRegex().IsMatch(text) ? false
        : null;

    [GeneratedRegex(@"[\(\[]\s*explicit\b|\bexplicit\s+(version|edit|lyrics)\b|\s-\s*explicit\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitMarkerRegex();

    [GeneratedRegex(@"[\(\[]\s*(clean|edited)\b|\b(clean|edited)\s+(version|edit|lyrics)\b|\s-\s*(clean|edited)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CleanMarkerRegex();

    /// <summary>
    /// Scores a flat search result by title alone. Artist and duration aren't known, so they aren't scored.
    /// </summary>
    public static MatchScore ScoreFlat(TrackQuery query, YouTubeVideoInfo candidate)
    {
        var queryCore = CoreTitle(query.Title);
        var title = Math.Max(
            Similarity(queryCore, CoreTitle(candidate.Title)),
            Similarity(Normalize(query.Title), Normalize(candidate.Title)));

        // Video titles are often "Artist - Title (Official Video)".
        if (title < 0.8 && ContainsWords(Normalize(candidate.Title), queryCore))
            title = 0.8;

        var queryVersions = GetVersionKeywords(query.Title);
        var candidateVersions = GetVersionKeywords(candidate.Title);
        var versionMismatches = queryVersions.Count + candidateVersions.Count - (2 * queryVersions.Intersect(candidateVersions).Count());

        var total = title - (0.25 * versionMismatches);
        return new MatchScore(total, title, Artist: 0, Duration: 0, Album: 0, versionMismatches, IsAcceptable: title >= 0.6 && versionMismatches == 0)
        {
            Explicit = GetExplicitMatch(query, candidate),
        };
    }

    public static MatchScore Score(TrackQuery query, YouTubeVideoInfo candidate)
    {
        var candidateTitle = candidate.Track ?? candidate.Title;

        var title = Math.Max(
            Similarity(CoreTitle(query.Title), CoreTitle(candidateTitle)),
            Similarity(Normalize(query.Title), Normalize(candidateTitle)));

        var artist = ScoreArtists(query.Artists, candidate);
        var duration = ScoreDuration(query.Duration, candidate.Duration);

        var album = query.Album is { } expectedAlbum && candidate.Album is { } candidateAlbum
            && Similarity(CoreTitle(expectedAlbum), CoreTitle(candidateAlbum)) >= 0.8 ? 1.0 : 0.0;

        var queryVersions = GetVersionKeywords(query.Title);
        var candidateVersions = GetVersionKeywords(candidateTitle);
        // Also look at the album and video title, e.g. "Live at Wembley" albums with plain track titles.
        if (candidate.Album is { } albumName && !(query.Album is { } queryAlbum && GetVersionKeywords(queryAlbum).Count > 0))
            candidateVersions.UnionWith(GetVersionKeywords(albumName).Where(k => k == "live"));
        if (candidate.Track is not null)
            candidateVersions.UnionWith(GetVersionKeywords(candidate.Title));
        var versionMismatches = queryVersions.Count + candidateVersions.Count - (2 * queryVersions.Intersect(candidateVersions).Count());

        var rankPenalty = candidate.SearchRank is { } rank ? Math.Min(rank - 1, 10) * 0.005 : 0;

        // The wrong explicit or clean version ranks below the right one, but isn't rejected outright here: the lookup
        // decides whether to send the track to review instead.
        var explicitMatch = GetExplicitMatch(query, candidate);
        var explicitPenalty = explicitMatch == ExplicitMatch.Mismatch ? 0.1 : 0;

        var total = (0.45 * title) + (0.30 * artist) + (0.20 * duration) + (0.05 * album) - (0.25 * versionMismatches) - rankPenalty - explicitPenalty;

        // A different edit or performance of the same song is usually more than a few seconds longer or shorter.
        var durationIsClose = query.Duration is null || candidate.Duration is null || duration >= 0.8;

        var isAcceptable =
            title >= 0.6
            && artist >= 0.5
            && durationIsClose
            && versionMismatches == 0
            && total >= 0.6;

        return new MatchScore(total, title, artist, duration, album, versionMismatches, isAcceptable) { Explicit = explicitMatch };
    }

    private static double ScoreArtists(IReadOnlyList<string> expected, YouTubeVideoInfo candidate)
    {
        if (expected.Count == 0) return 0.5;

        var names = new List<string>(candidate.Artists.Select(Normalize));
        if (candidate.Channel is { } channel)
            names.Add(Normalize(TopicSuffixRegex().Replace(channel, "")));

        // Featured artists are often only in the title.
        var titleText = Normalize($"{candidate.Track} {candidate.Title}");

        bool IsMatch(string artist)
        {
            var normalized = Normalize(artist);
            if (normalized.Length == 0) return false;

            return names.Any(n => n.Length > 0 && (n == normalized || ContainsWords(n, normalized) || ContainsWords(normalized, n) || Similarity(n, normalized) >= 0.85))
                || ContainsWords(titleText, normalized);
        }

        var primary = IsMatch(expected[0]) ? 1.0 : 0.0;
        if (expected.Count == 1) return primary;

        var others = expected.Skip(1).Count(IsMatch) / (double)(expected.Count - 1);
        return (0.7 * primary) + (0.3 * others);
    }

    private static double ScoreDuration(TimeSpan? expected, TimeSpan? actual)
    {
        if (expected is null || actual is null) return 0.5;

        var difference = Math.Abs((expected.Value - actual.Value).TotalSeconds);
        return difference switch
        {
            <= 3 => 1,
            <= 7 => 0.8,
            <= 15 => 0.5,
            <= 30 => 0.2,
            _ => 0,
        };
    }

    private static HashSet<string> GetVersionKeywords(string text)
    {
        var normalized = $" {Normalize(text)} ";
        var found = new HashSet<string>();
        foreach (var keyword in VersionKeywords)
        {
            if (normalized.Contains($" {Normalize(keyword)} ", StringComparison.Ordinal)
                || (keyword is "remix" && normalized.Contains(" remixed ", StringComparison.Ordinal)))
            {
                found.Add(keyword);
            }
        }

        return found;
    }

    /// <summary>
    /// The title without bracketed parts, " - " suffixes, and featured-artist credits, normalized.
    /// </summary>
    internal static string CoreTitle(string title)
    {
        var core = BracketsRegex().Replace(title, " ");
        var dash = core.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) core = core[..dash];
        core = FeaturingRegex().Replace(core, " ");
        var normalized = Normalize(core);
        return normalized.Length > 0 ? normalized : Normalize(title);
    }

    /// <summary>
    /// Lowercase, without diacritics or punctuation, with "&amp;" spelled "and" and whitespace collapsed.
    /// </summary>
    internal static string Normalize(string text)
    {
        var decomposed = text.Replace("&", " and ").Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;

        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (c is '\'' or '\u2019')
            {
                // "Don't" and "Dont" should match.
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Sørensen–Dice coefficient of character bigrams, from 0 to 1.
    /// </summary>
    internal static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length < 2 || b.Length < 2) return 0;

        var bigrams = new Dictionary<(char, char), int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var bigram = (a[i], a[i + 1]);
            bigrams[bigram] = bigrams.GetValueOrDefault(bigram) + 1;
        }

        var matches = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var bigram = (b[i], b[i + 1]);
            if (bigrams.TryGetValue(bigram, out var count) && count > 0)
            {
                bigrams[bigram] = count - 1;
                matches++;
            }
        }

        return 2.0 * matches / (a.Length - 1 + b.Length - 1);
    }

    private static bool ContainsWords(string text, string words)
    {
        return words.Length > 0 && $" {text} ".Contains($" {words} ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"\s+(feat\.?|ft\.|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingRegex();

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicSuffixRegex();
}
