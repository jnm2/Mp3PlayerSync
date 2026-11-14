using System.Text.RegularExpressions;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

// TEXT-1: whatever goes wrong on the page, the app says it in its own words.
public partial class FailureTextTests
{
    // What exportify.js puts in its #error element, as innerText.
    private static string[] PageErrors() =>
    [
        "The server returned an HTTP 403 response.",
        "The server returned an HTTP 429 response.",
        "The server returned an HTTP 401 response.",
        "The server returned an HTTP 500 response.\nThe server returned an HTTP 500 response.",
        "Exportify has encountered a bad gateway while querying endpoint https://api.spotify.com/v1/me/playlists!\n"
            + "Retries are implemented, so your download may still succeed. But I would be interested to hear about this.",
        "Couldn't export Road Trip. Encountered TypeError: Cannot read properties of undefined (reading 'items')\n"
            + "csvData@https://exportify.net/exportify.js:235:18\nPlease make an issue on GitHub at https://github.com/pavelkomarov/exportify/issues",
        "Couldn't export Road Trip with id 37i9dQZF1DXcBWIGoYBM5M. Encountered ReferenceError: x is not defined",
        "Something in the browser went wrong.",
    ];

    [Test]
    [MatrixDataSource]
    public async Task No_message_names_the_browser_or_Exportify(
        [MatrixMethod<FailureTextTests>(nameof(PageErrors))] string pageError,
        [Matrix(null, "busy", "notListed", "noButton", "noCsv", "readFailed", "sessionEnded", "unknown")] string? code)
    {
        string[] messages =
        [
            FailureText.ForPageError(pageError)!,
            FailureText.ForExport(code, pageError),
            FailureText.ForExport(code, null),
        ];

        await Assert.That(messages).DoesNotContain(message => Regex.IsMatch(message, "(?i)browser|exportify"));
        await Assert.That(messages).All(message => !string.IsNullOrEmpty(message));
    }

    [Test]
    [Arguments("The server returned an HTTP 429 response.", FailureText.Busy)]
    [Arguments("The server returned an HTTP 401 response.", FailureText.SessionEnded)]
    [Arguments("The server returned an HTTP 403 response.\nThe server returned an HTTP 403 response.", "Spotify refused some requests (HTTP 403).")]
    [Arguments("Exportify has encountered a bad gateway while querying endpoint https://api.spotify.com/v1/me!", "Spotify is having trouble right now (HTTP 502). Try again in a moment.")]
    [Arguments("Couldn't export Mix. Encountered TypeError: oops", FailureText.TryAgain)]
    public async Task Page_errors_are_said_plainly(string pageError, string expected)
    {
        await Assert.That(FailureText.ForPageError(pageError)).IsEqualTo(expected);
    }

    [Test]
    public async Task No_page_error_is_no_message()
    {
        await Assert.That(FailureText.ForPageError("  ")).IsNull();
        await Assert.That(FailureText.ForExport("noCsv", "")).IsEqualTo(FailureText.TryAgain);
    }

    [Test]
    [Arguments("notListed", "The server returned an HTTP 500 response.", FailureText.NotListed)]
    [Arguments("busy", null, FailureText.StillLoading)]
    [Arguments("noCsv", "The server returned an HTTP 429 response.", FailureText.Busy)]
    public async Task Export_failures_are_worded_by_code(string code, string? pageError, string expected)
    {
        await Assert.That(FailureText.ForExport(code, pageError)).IsEqualTo(expected);
    }

    [Test]
    public async Task The_bridge_only_sends_codes_the_app_knows()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Mp3PlayerSync.slnx"))) root = root.Parent!;
        var bridge = File.ReadAllText(Path.Combine(root.FullName, "src", "Mp3PlayerSync", "ExportifyBridge.js"));
        var sent = BridgeCode().Matches(bridge).Select(m => m.Groups[1].Value).Distinct().ToList();
        var known = typeof(FailureText.Codes).GetFields().Select(f => (string)f.GetValue(null)!).ToList();

        await Assert.That(sent).IsNotEmpty();
        await Assert.That(sent.Except(known)).IsEmpty();
    }

    [GeneratedRegex(@"(?:fail\(|code: )'(\w+)'")]
    private static partial Regex BridgeCode();
}
