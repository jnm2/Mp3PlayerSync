using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Scanning;

/// <summary>
/// Turns titles, artists and albums into keys that compare equal across Spotify's and taggers' spelling differences:
/// case, punctuation, diacritics, "&amp;" versus "and", featured-artist credits and suffixes like " - Remastered 2011".
/// </summary>
public static partial class TrackNormalizer
{
    private static readonly Dictionary<char, string> LetterFolds = new()
    {
        ['ø'] = "o", ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe", ['đ'] = "d", ['ð'] = "d",
        ['ł'] = "l", ['þ'] = "th", ['ı'] = "i", ['ħ'] = "h", ['ŧ'] = "t",
    };

    /// <summary>
    /// Lowercase words separated by single spaces, without diacritics or punctuation. Apostrophes are removed
    /// ("Don't" becomes "dont") and "&amp;" becomes "and".
    /// </summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        // Compatibility decomposition also folds full-width letters and ligatures.
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark:
                    continue;
            }

            if (c is '\'' or '’' or '‘' or 'ʼ' or '`') continue;

            if (c == '&')
            {
                Append("and");
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                var lower = char.ToLowerInvariant(c);
                Append(LetterFolds.TryGetValue(lower, out var folded) ? folded : lower.ToString());
            }
            else
            {
                pendingSpace = builder.Length > 0;
            }
        }

        return builder.ToString();

        void Append(string value)
        {
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(value);
        }
    }

    /// <summary>
    /// The normalized text without spaces, so that "R.E.M." equals "REM".
    /// </summary>
    public static string Key(string? text) => NormalizeText(text).Replace(" ", "");

    /// <summary>
    /// Removes featured-artist credits and version suffixes that don't make a different recording, such as
    /// " - Remastered 2011", " (2009 Remaster)" and " [feat. Someone]". Live versions, remixes and edits are kept.
    /// </summary>
    /// <param name="featuredArtists">The artists credited in the title with feat., ft., featuring or with.</param>
    public static string StripTitleDecorations(string? title, out IReadOnlyList<string> featuredArtists) =>
        StripTitleDecorations(title, out featuredArtists, out _);

    /// <param name="isExplicit">
    /// <see langword="true"/> or <see langword="false"/> when the title is marked as the explicit or clean version,
    /// for example " (Explicit)" or " [Clean Version]"; otherwise <see langword="null"/>.
    /// </param>
    public static string StripTitleDecorations(string? title, out IReadOnlyList<string> featuredArtists, out bool? isExplicit)
    {
        var featured = new List<string>();
        featuredArtists = featured;
        isExplicit = null;
        if (string.IsNullOrWhiteSpace(title)) return "";

        var result = BracketedFeature().Replace(title, match =>
        {
            featured.AddRange(SplitArtists(match.Groups["artists"].Value));
            return " ";
        });

        while (true)
        {
            var trimmed = result.TrimEnd();

            if (TrailingSegment().Match(trimmed) is { Success: true } segment && segment.Index > 0)
            {
                var text = segment.Groups["text"].Value;
                if (FeatureCredit().Match(text) is { Success: true } feature)
                {
                    featured.AddRange(SplitArtists(feature.Groups["artists"].Value));
                    result = trimmed[..segment.Index];
                    continue;
                }

                var advisory = AdvisoryMarker().Match(text);
                if (advisory.Success || VersionWords().IsMatch(text))
                {
                    if (advisory.Success)
                        isExplicit ??= advisory.Groups["word"].Value.ToLowerInvariant() is "explicit" or "dirty" or "uncensored";
                    result = trimmed[..segment.Index];
                    continue;
                }
            }

            if (UnbracketedFeature().Match(trimmed) is { Success: true } unbracketed && unbracketed.Index > 0)
            {
                featured.AddRange(SplitArtists(unbracketed.Groups["artists"].Value));
                result = trimmed[..unbracketed.Index];
                continue;
            }

            return trimmed.Trim();
        }
    }

    /// <summary>
    /// The key for comparing titles: decorations stripped, then <see cref="Key"/>.
    /// </summary>
    public static string TitleKey(string? title) => Key(StripTitleDecorations(title, out _));

    /// <summary>
    /// Whether a title or album name is marked as the explicit version (<see langword="true"/>) or the clean version
    /// (<see langword="false"/>), for example "Song (Explicit)" or "Album [Clean]". <see langword="null"/> when
    /// it isn't marked.
    /// </summary>
    public static bool? ExplicitMarker(string? titleOrAlbum)
    {
        StripTitleDecorations(titleOrAlbum, out _, out var isExplicit);
        return isExplicit;
    }

    /// <summary>
    /// The key for titles that differ only in a trailing " - …" or bracketed part, such as "Song" and
    /// "Song - From the Movie". Matching on this is only safe together with a duration check.
    /// </summary>
    public static string BaseTitleKey(string? title)
    {
        var stripped = StripTitleDecorations(title, out _);
        while (TrailingSegment().Match(stripped) is { Success: true, Index: > 0 } segment)
            stripped = stripped[..segment.Index].TrimEnd();

        return Key(stripped);
    }

    public static string AlbumKey(string? album) => TitleKey(album);

    /// <summary>
    /// The key for one artist name, ignoring a leading "The".
    /// </summary>
    public static string ArtistKey(string? artist)
    {
        var normalized = NormalizeText(artist);
        if (normalized.StartsWith("the ", StringComparison.Ordinal)) normalized = normalized[4..];
        return normalized.Replace(" ", "");
    }

    /// <summary>
    /// Splits a credit like "A feat. B &amp; C" or "A; B" into names. The whole credit is not included.
    /// </summary>
    public static IReadOnlyList<string> SplitArtists(string? artists)
    {
        if (string.IsNullOrWhiteSpace(artists)) return [];
        return ArtistSeparator().Split(artists)
            .Select(a => a.Trim().Trim('(', ')', '[', ']').Trim())
            .Where(a => a.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Keys for every way of reading the given artist credits: each credit whole and each name in it. Two tracks
    /// share an artist when their key sets intersect, which handles "A feat. B" against ["A", "B"] and
    /// "Earth, Wind &amp; Fire" against itself.
    /// </summary>
    public static HashSet<string> ArtistKeys(IEnumerable<string> artists)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<string>();
        foreach (var artist in artists)
        {
            all.Add(artist);
            Add(artist);
            foreach (var name in SplitArtists(artist)) Add(name);
        }

        // "AC/DC" may come back from a tag as ["AC", "DC"].
        if (all.Count > 1) Add(string.Join("/", all));
        return keys;

        void Add(string name)
        {
            var key = ArtistKey(name);
            if (key.Length > 0) keys.Add(key);
        }
    }

    public static string IsrcKey(string? isrc) =>
        string.IsNullOrWhiteSpace(isrc) ? "" : string.Concat(isrc.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private const string FeatureWords = @"feat\.?|ft\.?|featuring|with";

    [GeneratedRegex(@"\s*[\(\[]\s*(?:" + FeatureWords + @")\s+(?<artists>[^\)\]]+)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedFeature();

    [GeneratedRegex(@"^(?:" + FeatureWords + @")\s+(?<artists>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureCredit();

    // "with" isn't a credit outside brackets: "Dancing with Myself".
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+(?<artists>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnbracketedFeature();

    [GeneratedRegex(@"(?:\s+[-–—]\s+(?<text>[^-–—]+)|\s*\((?<text>[^\(\)]*)\)|\s*\[(?<text>[^\[\]]*)\])$")]
    private static partial Regex TrailingSegment();

    [GeneratedRegex(@"\b(?:re-?master(?:ed|ing)?|bonus\s+track|deluxe|anniversary|expanded|special\s+edition|(?:single|album|original|lp|stereo|mono)\s+(?:version|mix)|stereo|mono)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWords();

    // The advisory word must end the segment ("Explicit", "Deluxe Clean Version", "Clean Edit"), so that
    // "(Clean Bandit Remix)" stays a different recording.
    [GeneratedRegex(@"(?:^|\s)(?<word>explicit|clean|dirty|censored|uncensored)(?:\s+(?:album\s+)?(?:version|edit|lyrics))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AdvisoryMarker();

    [GeneratedRegex(@"\s*(?:;|/|,|\s&\s|\s+(?:feat\.?|ft\.?|featuring|with|vs\.?|x|and)\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();
}
