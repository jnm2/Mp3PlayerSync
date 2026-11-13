using System.Globalization;
using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Placement;

/// <param name="Number">The number prefix, or null when the name has none.</param>
/// <param name="Digits">How many digits the number was written with.</param>
/// <param name="Name">The rest of the name, after "&lt;nn&gt;. ".</param>
public readonly record struct NumberedFileName(int? Number, int Digits, string Name);

/// <summary>
/// The "&lt;nn&gt;. " prefix of file names in album and playlist folders.
/// </summary>
/// <remarks>
/// Numbers have 2 digits, or more when a folder reaches 100 (3 digits), 1000 (4 digits), … entries.
/// While renumbering, the executor parks files under a temporary "~mps-xxxxxx " prefix (keeping the audio extension,
/// so a rescan still sees them); names with that prefix are read as if it weren't there, so a re-run finishes the job.
/// </remarks>
public static partial class TrackNumbering
{
    public const string TemporaryPrefixStart = "~mps-";

    public static int WidthFor(int maxNumber) => Math.Max(2, Math.Max(0, maxNumber).ToString(CultureInfo.InvariantCulture).Length);

    public static string Format(int number, int width) => number.ToString("D" + width, CultureInfo.InvariantCulture);

    /// <summary>Splits a file name into its number prefix (if any) and the rest, ignoring a temporary prefix.</summary>
    public static NumberedFileName Parse(string fileName)
    {
        var name = StripTemporaryPrefix(fileName);
        var match = NumberPrefix().Match(name);
        return match.Success && int.TryParse(match.Groups["number"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? new(number, match.Groups["number"].Length, match.Groups["rest"].Value)
            : new(null, 0, name);
    }

    /// <summary>The file name with its number prefix replaced (or added) and any temporary prefix removed.</summary>
    public static string WithNumber(string fileName, int number, int width) =>
        $"{Format(number, width)}. {Parse(fileName).Name}";

    public static bool HasTemporaryPrefix(string fileName) => TemporaryPrefix().IsMatch(fileName);

    public static string StripTemporaryPrefix(string fileName) => TemporaryPrefix().Replace(fileName, "", 1);

    internal static string AddTemporaryPrefix(string fileName) =>
        $"{TemporaryPrefixStart}{Random.Shared.Next(0x1000000):x6} {StripTemporaryPrefix(fileName)}";

    [GeneratedRegex(@"^(?<number>[0-9]+)\. (?<rest>.+)$", RegexOptions.Singleline)]
    private static partial Regex NumberPrefix();

    [GeneratedRegex(@"^~mps-[0-9a-f]{6} ")]
    private static partial Regex TemporaryPrefix();
}
