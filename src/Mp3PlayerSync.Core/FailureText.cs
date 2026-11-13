using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core;

/// <summary>
/// What the app says when loading the library, a playlist or an album fails. It never passes on the web page's own
/// wording, which names the service behind it and its internals (TEXT-1); that goes only to the debug log.
/// </summary>
public static partial class FailureText
{
    public const string TryAgain = "Spotify didn't respond as expected. Try again in a moment.";
    public const string Busy = "Spotify is busy. Try again in a moment.";
    public const string SessionEnded = "Your Spotify session ended. Try again.";
    public const string TooSlow = "Spotify took too long to respond. Try again in a moment.";
    public const string SignInTooSlow = "Spotify didn't sign in again in time. Try again in a moment.";
    public const string NotListed = "That playlist is no longer in your library.";
    public const string StillLoading = "Another playlist is still loading. Try again in a moment.";

    /// <summary>The failure codes ExportifyBridge.js sends with a failed export.</summary>
    public static class Codes
    {
        public const string Busy = "busy";
        public const string NotListed = "notListed";
        public const string NoButton = "noButton";
        public const string NoCsv = "noCsv";
        public const string ReadFailed = "readFailed";
        public const string SessionEnded = "sessionEnded";
    }

    /// <summary>A failed export, from its code and, for some codes, the page's error text.</summary>
    public static string ForExport(string? code, string? pageError) => code switch
    {
        Codes.Busy => StillLoading,
        Codes.NotListed => NotListed,
        Codes.SessionEnded => SessionEnded,
        Codes.NoButton or Codes.ReadFailed => TryAgain,
        _ => ForPageError(pageError) ?? TryAgain,
    };

    /// <summary>
    /// The page's error text, which may hold several messages, in the app's words; null when there's none.
    /// </summary>
    public static string? ForPageError(string? pageError)
    {
        if (string.IsNullOrWhiteSpace(pageError)) return null;

        var messages = HttpStatus().Matches(pageError).Select(m => ForStatus(int.Parse(m.Groups[1].Value))).ToList();
        if (pageError.Contains("bad gateway", StringComparison.OrdinalIgnoreCase))
            messages.Add(ForStatus(502));
        if (messages.Count == 0)
            messages.Add(TryAgain);
        return string.Join(" ", messages.Distinct());
    }

    private static string ForStatus(int status) => status switch
    {
        401 => SessionEnded,
        403 => "Spotify refused some requests (HTTP 403).",
        404 => "Spotify couldn't find something it was asked for (HTTP 404).",
        429 => Busy,
        >= 500 => $"Spotify is having trouble right now (HTTP {status}). Try again in a moment.",
        _ => $"Spotify returned an error (HTTP {status}).",
    };

    [GeneratedRegex(@"\bHTTP (\d{3})\b")]
    private static partial Regex HttpStatus();
}
