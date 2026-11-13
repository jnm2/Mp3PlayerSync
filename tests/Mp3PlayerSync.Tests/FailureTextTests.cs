using System.Text.RegularExpressions;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

// TEXT-1: whatever goes wrong on the page, the app says it in its own words.
public partial class FailureTextTests
{
    // What exportify.js puts in its #error element, as innerText.
    private static readonly string[] PageErrors =
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
    public void No_message_names_the_browser_or_Exportify(
        [ValueSource(nameof(PageErrors))] string pageError,
        [Values(null, "busy", "notListed", "noButton", "noCsv", "readFailed", "sessionEnded", "unknown")] string? code)
    {
        string[] messages =
        [
            FailureText.ForPageError(pageError)!,
            FailureText.ForExport(code, pageError),
            FailureText.ForExport(code, null),
        ];

        Assert.That(messages, Has.None.Match("(?i)browser|exportify"));
        Assert.That(messages, Has.All.Not.Empty);
    }

    [TestCase("The server returned an HTTP 429 response.", FailureText.Busy)]
    [TestCase("The server returned an HTTP 401 response.", FailureText.SessionEnded)]
    [TestCase("The server returned an HTTP 403 response.\nThe server returned an HTTP 403 response.", "Spotify refused some requests (HTTP 403).")]
    [TestCase("Exportify has encountered a bad gateway while querying endpoint https://api.spotify.com/v1/me!", "Spotify is having trouble right now (HTTP 502). Try again in a moment.")]
    [TestCase("Couldn't export Mix. Encountered TypeError: oops", FailureText.TryAgain)]
    public void Page_errors_are_said_plainly(string pageError, string expected)
    {
        Assert.That(FailureText.ForPageError(pageError), Is.EqualTo(expected));
    }

    [Test]
    public void No_page_error_is_no_message()
    {
        Assert.That(FailureText.ForPageError("  "), Is.Null);
        Assert.That(FailureText.ForExport("noCsv", ""), Is.EqualTo(FailureText.TryAgain));
    }

    [TestCase("notListed", "The server returned an HTTP 500 response.", FailureText.NotListed)]
    [TestCase("busy", null, FailureText.StillLoading)]
    [TestCase("noCsv", "The server returned an HTTP 429 response.", FailureText.Busy)]
    public void Export_failures_are_worded_by_code(string code, string? pageError, string expected)
    {
        Assert.That(FailureText.ForExport(code, pageError), Is.EqualTo(expected));
    }

    [Test]
    public void The_bridge_only_sends_codes_the_app_knows()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Mp3PlayerSync.slnx"))) root = root.Parent!;
        var bridge = File.ReadAllText(Path.Combine(root.FullName, "src", "Mp3PlayerSync", "ExportifyBridge.js"));
        var sent = BridgeCode().Matches(bridge).Select(m => m.Groups[1].Value).Distinct().ToList();
        var known = typeof(FailureText.Codes).GetFields().Select(f => (string)f.GetValue(null)!).ToList();

        Assert.That(sent, Is.Not.Empty);
        Assert.That(sent, Is.SubsetOf(known));
    }

    [GeneratedRegex(@"(?:fail\(|code: )'(\w+)'")]
    private static partial Regex BridgeCode();
}
