using System.Buffers;
using System.Text;

namespace Mp3PlayerSync.Core.Placement;

/// <summary>One component of a relative path on the player.</summary>
/// <param name="Text">The part that may be shortened to fit length limits. Must already be sanitized.</param>
/// <param name="Suffix">A part that is never shortened, such as " (1982)" or ".m4a".</param>
/// <param name="IsFixed">An existing folder name, which is used exactly as is.</param>
public readonly record struct PathPiece(string Text, string Suffix = "", bool IsFixed = false);

/// <summary>
/// Makes file and folder names that are valid on FAT32 and exFAT and that fit Windows path length limits.
/// </summary>
public static class PlayerPathNames
{
    public const int MaxComponentLength = 255;

    /// <summary>Leaves room under Windows' 259-character path limit for a drive root like "E:\".</summary>
    public const int DefaultMaxRelativePathLength = 240;

    public const char Separator = '\\';

    // Shortening never takes a name below this, so names stay recognizable.
    private const int MinShortenedLength = 8;

    private static readonly SearchValues<char> InvalidChars = SearchValues.Create("\"*/:<>?\\|");

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         .. Enumerable.Range(1, 9).Select(i => $"COM{i}"),
         .. Enumerable.Range(1, 9).Select(i => $"LPT{i}")],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces characters FAT32/exFAT reject with '_', trims leading spaces and trailing dots and spaces,
    /// avoids Windows reserved device names, and limits the length to <see cref="MaxComponentLength"/>.
    /// </summary>
    public static string SanitizeComponent(string name, string fallback = "_")
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            builder.Append(
                char.IsWhiteSpace(c) ? ' '
                : char.IsControl(c) || InvalidChars.Contains(c) ? '_'
                : c);
        }

        var result = TrimEnds(builder.ToString());
        if (result.Length == 0) result = fallback;

        if (IsReservedName(result))
            result = "_" + result;

        return Shorten(result, MaxComponentLength);
    }

    public static bool IsReservedName(string name)
    {
        var dot = name.IndexOf('.');
        return ReservedNames.Contains((dot < 0 ? name : name[..dot]).TrimEnd());
    }

    /// <summary>
    /// Joins pieces into a relative path, shortening the longest non-fixed pieces until the path fits <paramref name="maxLength"/>.
    /// </summary>
    public static string Join(IReadOnlyList<PathPiece> pieces, int maxLength = DefaultMaxRelativePathLength)
    {
        var texts = pieces.Select(p => p.IsFixed ? p.Text : Shorten(p.Text, MaxComponentLength - p.Suffix.Length)).ToArray();

        int Total() => texts.Select((text, i) => text.Length + pieces[i].Suffix.Length).Sum() + pieces.Count - 1;

        while (Total() > maxLength)
        {
            var candidates = Enumerable.Range(0, texts.Length)
                .Where(i => !pieces[i].IsFixed && texts[i].Length > MinShortenedLength)
                .ToList();
            if (candidates.Count == 0) break;

            var longest = candidates.MaxBy(i => texts[i].Length);
            var nextLongest = candidates.Where(i => i != longest).Select(i => texts[i].Length).DefaultIfEmpty(0).Max();
            var target = Math.Max(MinShortenedLength, Math.Max(nextLongest, texts[longest].Length - (Total() - maxLength)));
            if (target >= texts[longest].Length) target = texts[longest].Length - 1;

            texts[longest] = Shorten(texts[longest], target);
        }

        return string.Join(Separator, texts.Select((text, i) => text + pieces[i].Suffix));
    }

    /// <summary>Normalizes separators to '\' and removes leading and trailing separators.</summary>
    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('/', Separator).Trim(Separator);

    /// <summary>The folder part of a relative path, or "" for a file at the player's root.</summary>
    public static string GetFolder(string relativePath)
    {
        var index = relativePath.LastIndexOf(Separator);
        return index < 0 ? "" : relativePath[..index];
    }

    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;

        var cut = Math.Max(0, maxLength);
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;

        var result = TrimEnds(text[..cut]);
        return result.Length > 0 ? result : "_";
    }

    private static string TrimEnds(string text) => text.TrimStart(' ').TrimEnd('.', ' ');
}
