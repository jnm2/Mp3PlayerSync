using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Tagging;
using Mp3PlayerSync.EndToEnd.Harness;
using Mp3PlayerSync.EndToEnd.Launcher;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// The weekly live check (TEST-4, run by .github\workflows\live-check.yml). It signs in to the Spotify test account
/// through the app on the real exportify.net, and confirms that the playlist list and an export still carry every piece
/// of metadata the app needs, as the app's own code reads them. It doesn't check which songs the account has.
/// </summary>
/// <remarks>
/// <para>The account's email address and password come from <c>SPOTIFY_TEST_USERNAME</c> and
/// <c>SPOTIFY_TEST_PASSWORD</c>. Without them, the check goes as far as Spotify's sign-in page and stops there, which is
/// a dry run, and reports itself as inconclusive.</para>
/// <para>The outcome depends on where things stop. While the page is on Spotify's sign-in pages, anything that stops
/// the sign-in is inconclusive: a captcha, a login code, a rejected password, or a page the check can't find its way
/// around. A site that is down is inconclusive too. Once Spotify has sent the page back to exportify.net, anything
/// missing means the app no longer works with exportify.net, so it's a failure. So is the app never getting to
/// Spotify's sign-in at all.</para>
/// </remarks>
[Category(TestCategories.Live)]
[Explicit]
[ClassDataSource<RunContext>(Shared = SharedType.PerClass)]
public sealed partial class LiveTests(RunContext run)
{
    private static readonly Uri ExportifyHome = new("https://exportify.net/");
    private static readonly Uri SpotifyAccounts = new("https://accounts.spotify.com/");

    /// <summary>The columns of exportify.net's export that the app can't do without, and what it uses each one for.</summary>
    private static readonly (string Column, string UsedFor, Func<ExportedTrack, bool> IsGood)[] RequiredColumns =
    [
        ("Track URI", "telling songs apart, for the checkboxes and the match cache", t => SpotifyTrackUri().IsMatch(t.Uri)),
        ("Track Name", "finding the song, its title tag and the track list", t => t.Title.Length > 0),
        ("Artist Name(s)", "finding the song, its artist tag and the track list", t => t.Artists.Count > 0 && t.Artists.All(a => a.Length > 0)),
        ("Album Name", "finding the song, its album tag and the track list", t => t.Album.Length > 0),
        ("Duration (ms)", "finding the song and the Time column", t => t.Duration > TimeSpan.Zero),
        ("ISRC", "matching the song exactly, its ISRC tag and the match cache", t => Isrc().IsMatch(t.Isrc)),
        ("Release Date", "the year tag and grouping saved albums", t => ReleaseDate().IsMatch(t.ReleaseDate)),
        ("Added At", "ordering saved albums and the newest songs", t => t.AddedAt is not null),
        ("Explicit", "telling explicit and clean versions apart", t => t.Explicit is not null),
    ];

    /// <summary>Columns the app writes to tags when they're there, but can do without.</summary>
    private static readonly string[] OptionalColumns = ["Genres", "Record Label", "Key", "Mode", "Tempo"];

    [Test]
    [Timeout(20 * 60_000)]
    public async Task Exportify_still_gives_the_app_the_metadata_it_needs(CancellationToken cancellationToken)
    {
        var credentials = Credentials.TakeFromEnvironment();
        await CheckIsUpAsync(ExportifyHome, "exportify.net", credentials, cancellationToken);
        await CheckIsUpAsync(SpotifyAccounts, "Spotify's sign-in", credentials, cancellationToken);

        // No removable drive can be assumed, so the player is a folder, as if chosen with "Other folder" last time.
        // Nothing is ever synced to it.
        const string playerName = "Live check player";
        run.WriteSettings(new { Player = new { FolderPath = run.CreateFolder(playerName) } });
        var tracksFile = Path.Combine(run.DataDirectory, "Tracks.jsonl");
        await using var app = await AppUnderTest.StartAsync(
            run.Automation, new HostLaunch(run.DataDirectory, null, null) { RecordTracksTo = tracksFile }, cancellationToken);

        var players = await Wait.UntilAsync(() => app.FindById("PlayerGridView"), "the MP3 player page", showing: app, cancellationToken: cancellationToken);
        var player = await Wait.UntilAsync(
            () => players.FindFirstDescendant(c => c.ByControlType(ControlType.ListItem).And(c.ByName(playerName))),
            $"the '{playerName}' player", showing: app, cancellationToken: cancellationToken);
        player.Patterns.Invoke.Pattern.Invoke();

        await SignInAsync(app, credentials, cancellationToken);

        // From here on, Spotify has sent the page back to exportify.net, so anything missing is a failure.
        await CheckLibraryAndExportAsync(app, tracksFile, cancellationToken);
    }

    /// <summary>
    /// Checks what the app shows from the playlist list and the Saved Albums export, then opens Liked Songs, which
    /// exports it, and checks every column the app read from it.
    /// </summary>
    private static async Task CheckLibraryAndExportAsync(AppUnderTest app, string tracksFile, CancellationToken cancellationToken)
    {
        await Wait.UntilAsync(
            () => app.Text("LibrarySummaryText") is { Length: > 0 } && app.FindById("LibraryBusyText") is null,
            "the library to finish loading", TimeSpan.FromMinutes(2), app, cancellationToken);

        SelectTab(app, "Saved albums");
        var album = await Wait.UntilAsync(
            () => Tiles(app).FirstOrDefault(tile => Texts(tile).Any(text => text.StartsWith("Saved ", StringComparison.Ordinal))),
            "a saved album that says when it was saved (from the Saved Albums export's Added At)", TimeSpan.FromMinutes(2), app, cancellationToken);
        var albumTexts = Texts(album);
        albumTexts.ShouldSatisfyAllConditions(
            () => albumTexts.ShouldContain(text => ArtistAndYear().IsMatch(text), $"The saved album '{NameOf(album)}' should show its artist and year (from the Saved Albums export's Artist Name(s) and Release Date), but it shows: {string.Join(" | ", albumTexts)}"),
            () => TrackCount(albumTexts).ShouldNotBeNull($"The saved album '{NameOf(album)}' should show how many tracks it has, but it shows: {string.Join(" | ", albumTexts)}").ShouldBeGreaterThan(0));

        SelectTab(app, "All");
        var likedSongs = await Wait.UntilAsync(() => Tiles(app).FirstOrDefault(), "the first tile on the All tab", showing: app, cancellationToken: cancellationToken);
        var likedSongsTexts = Texts(likedSongs);
        var likedSongsCount = TrackCount(likedSongsTexts);
        likedSongs.ShouldSatisfyAllConditions(
            () => NameOf(likedSongs).ShouldBe("Liked Songs", "Liked Songs should be first, which needs its row's link and name"),
            () => likedSongsCount.ShouldNotBeNull($"Liked Songs should show its track count (the playlist table's Tracks column), but it shows: {string.Join(" | ", likedSongsTexts)}").ShouldBeGreaterThan(0));

        likedSongs.Patterns.Invoke.Pattern.Invoke();
        await Wait.UntilAsync(() => app.Text("PlaylistTitle") is { } title && LikedSongsTitle().IsMatch(title), "the Liked Songs page", showing: app, cancellationToken: cancellationToken);
        var summary = await Wait.UntilAsync(
            () => app.Text("SummaryText") is { } text && TrackCountSummary().IsMatch(text) ? text : null,
            "the exported Liked Songs' summary", TimeSpan.FromMinutes(2), app, cancellationToken);
        var tracks = await Wait.UntilAsync(() => LastRecordedBatch(tracksFile), "the exported songs to be looked for", showing: app, cancellationToken: cancellationToken);

        CheckColumns(tracks, summary, likedSongsCount!.Value);
    }

    /// <summary>
    /// Signs in on Spotify's pages in the app's window, as a person would, until the library shows. Only reached once
    /// the app has got to Spotify's sign-in by itself.
    /// </summary>
    private async Task SignInAsync(AppUnderTest app, Credentials? credentials, CancellationToken cancellationToken)
    {
        // The app clicks exportify's "Get started" by itself (SIGNIN-1). If it never gets to Spotify, the app is broken.
        await Wait.UntilAsync(() => SpotifyPage(app), "Spotify's sign-in page in the window", TimeSpan.FromMinutes(2), app, cancellationToken);

        if (credentials is null)
        {
            await Wait.UntilAsync(() => SpotifyPage(app) is { } page ? UsernameField(page.Document) : null,
                "the email field on Spotify's sign-in page", TimeSpan.FromMinutes(1), app, cancellationToken);
            throw Inconclusive(null, "This was a dry run. SPOTIFY_TEST_USERNAME and SPOTIFY_TEST_PASSWORD aren't set, so the check stopped at Spotify's sign-in page once it found the email field.");
        }

        var presses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sinceLastPress = Stopwatch.StartNew();
        var limit = TimeSpan.FromMinutes(4) * Wait.Scale;
        var elapsed = Stopwatch.StartNew();
        string? lastSpotifyText = null;
        while (!IsShowingLibrary(app))
        {
            var page = TryRead(() => SpotifyPage(app));
            if (elapsed.Elapsed > limit)
            {
                if (page is not null)
                    throw Inconclusive(credentials, $"The sign-in was still on Spotify's pages after {limit.TotalMinutes:0} minutes. The page showed: {Snippet(lastSpotifyText)}");
                throw new TimeoutException($"Spotify sent the page back, but the library didn't show within {limit.TotalMinutes:0} minutes. The window showed: {app.TryReadAllText()}");
            }

            if (page is not null && TryRead(() => PageText(page.Document)) is { } text)
            {
                lastSpotifyText = text;
                if (page.Url.Host.Equals("challenge.spotify.com", StringComparison.OrdinalIgnoreCase) || Captcha().IsMatch(text))
                    throw Inconclusive(credentials, $"Spotify asked for a captcha. The page showed: {Snippet(text)}");
                if (WrongPassword().IsMatch(text))
                    throw Inconclusive(credentials, $"Spotify didn't accept the email address and password. The page showed: {Snippet(text)}");
                if (Blocked().IsMatch(text))
                    throw Inconclusive(credentials, $"Spotify has blocked the sign-in. Check the test account's mailbox. The page showed: {Snippet(text)}");

                if (sinceLastPress.Elapsed >= TimeSpan.FromSeconds(3) && TryRead(() => NextStep(page.Document, credentials)) is { } step)
                {
                    if (presses.TryGetValue(step.Name, out var count) && count >= 3)
                        throw Inconclusive(credentials, $"Spotify's page didn't move on after the check pressed '{step.Name}' three times. The page showed: {Snippet(text)}");
                    presses[step.Name] = count + 1;
                    TryAct(step.Act);
                    sinceLastPress.Restart();
                }
                else if (sinceLastPress.Elapsed >= TimeSpan.FromSeconds(30) && CodePrompt().IsMatch(text))
                {
                    throw Inconclusive(credentials, $"Spotify asked for a login code and offered no way to use the password. Check the test account's mailbox. The page showed: {Snippet(text)}");
                }
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    /// <summary>What to do next on Spotify's page, if the check knows: agree, choose the password, or fill in and go on.</summary>
    private static SignInStep? NextStep(AutomationElement page, Credentials credentials)
    {
        var buttons = page.FindAllDescendants(c => c.ByControlType(ControlType.Button).Or(c.ByControlType(ControlType.Hyperlink)))
            .Where(b => b.Properties.IsEnabled.ValueOrDefault && b.Patterns.Invoke.IsSupported)
            .ToList();
        AutomationElement? Button(Regex name) => buttons.FirstOrDefault(b => name.IsMatch(NameOf(b)));

        // Exportify asks for the account's consent until it has agreed once.
        if (Button(AgreeButton()) is { } agree) return new(NameOf(agree), () => agree.Patterns.Invoke.Pattern.Invoke());
        // Spotify may offer a code by email first, with the password as the other way in.
        if (Button(PasswordOption()) is { } usePassword) return new(NameOf(usePassword), () => usePassword.Patterns.Invoke.Pattern.Invoke());

        var username = UsernameField(page);
        var password = page.FindAllDescendants(c => c.ByControlType(ControlType.Edit)).FirstOrDefault(e => e.Properties.IsPassword.ValueOrDefault);
        if ((username is null && password is null) || Button(GoOnButton()) is not { } goOn) return null;
        return new(NameOf(goOn), () =>
        {
            username?.Patterns.Value.Pattern.SetValue(credentials.Username);
            password?.Patterns.Value.Pattern.SetValue(credentials.Password);
            goOn.Patterns.Invoke.Pattern.Invoke();
        });
    }

    private sealed record SignInStep(string Name, Action Act);

    private static AutomationElement? UsernameField(AutomationElement page) =>
        page.FindAllDescendants(c => c.ByControlType(ControlType.Edit)).FirstOrDefault(e =>
            !e.Properties.IsPassword.ValueOrDefault
            && (e.Properties.AutomationId.ValueOrDefault == "login-username" || EmailOrUsername().IsMatch(NameOf(e))));

    /// <summary>The page in the app's window when it's one of Spotify's, with its address.</summary>
    private WebPage? SpotifyPage(AppUnderTest app)
    {
        foreach (var inputSite in NativeWindows.Children(app.Window.Properties.NativeWindowHandle.Value, "InputSiteWindowClass"))
        {
            var document = run.Automation.FromHandle(inputSite).FindFirstDescendant(c => c.ByControlType(ControlType.Document));
            // A web page's value is its address.
            if (document?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault is { } address
                && Uri.TryCreate(address, UriKind.Absolute, out var url)
                && url.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase))
            {
                return new(document, url);
            }
        }
        return null;
    }

    private sealed record WebPage(AutomationElement Document, Uri Url);

    private static string PageText(AutomationElement page) =>
        string.Join(" ", page.FindAllDescendants(c => c.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault).Where(n => !string.IsNullOrWhiteSpace(n)));

    // FlaUI's Name throws for an element without a name.
    private static string NameOf(AutomationElement element) => element.Properties.Name.ValueOrDefault ?? "";

    private static bool IsShowingLibrary(AppUnderTest app) => TryRead(() => app.FindById("LibraryGridView")) is not null;

    /// <summary>The result of a look at the app, or null when the element went away while it was read.</summary>
    private static T? TryRead<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception exception) when (IsGone(exception))
        {
            return null;
        }
    }

    /// <summary>Does something on the page, unless the element went away first: the next look at the page decides again.</summary>
    private static void TryAct(Action act)
    {
        try { act(); }
        catch (Exception exception) when (IsGone(exception)) { }
    }

    private static bool IsGone(Exception exception) =>
        exception is FlaUI.Core.Exceptions.ElementNotAvailableException or System.Runtime.InteropServices.COMException or TimeoutException;

    private static void SelectTab(AppUnderTest app, string name)
    {
        // Only the tabs' AutomationIds end in FilterItem: a library tile could be called "All".
        var tab = app.Window.FindAllDescendants(c => c.ByName(name))
            .FirstOrDefault(e => e.Properties.AutomationId.ValueOrDefault?.EndsWith("FilterItem", StringComparison.Ordinal) == true)
            .ShouldNotBeNull($"The library should have a '{name}' tab");
        tab.Patterns.SelectionItem.Pattern.Select();
    }

    private static AutomationElement[] Tiles(AppUnderTest app) =>
        app.FindById("LibraryGridView")?.FindAllChildren(c => c.ByControlType(ControlType.ListItem)) ?? [];

    private static string[] Texts(AutomationElement element) =>
        element.FindAllDescendants(c => c.ByControlType(ControlType.Text)).Select(t => t.Properties.Name.ValueOrDefault).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToArray();

    private static int? TrackCount(IEnumerable<string> texts) =>
        texts.Select(text => TileTrackCount().Match(text)).FirstOrDefault(m => m.Success) is { } match ? int.Parse(match.Groups[1].Value) : null;

    /// <summary>The last batch of songs the test host was asked to look for, as the app read them from the export.</summary>
    private static List<ExportedTrack>? LastRecordedBatch(string path)
    {
        try
        {
            var last = File.Exists(path) ? File.ReadLines(path).LastOrDefault(line => line.Length > 0) : null;
            return last is null ? null : JsonSerializer.Deserialize<List<ExportedTrack>>(last) is { Count: > 0 } tracks ? tracks : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null; // Still being written
        }
    }

    private static void CheckColumns(List<ExportedTrack> tracks, string summary, int listedCount)
    {
        var headers = new HashSet<string>(tracks[0].Columns.Keys, StringComparer.OrdinalIgnoreCase);
        var metadata = tracks.Select(t => (Track: t, Tags: TrackMetadata.FromExportedTrack(t))).ToList();
        string Describe(ExportedTrack t) => $"#{t.Number} '{t.Title}'";

        var report = new StringBuilder()
            .AppendLine("### Columns of the Liked Songs export")
            .AppendLine()
            .AppendLine($"The app read {tracks.Count} songs. Its summary says: {summary}.")
            .AppendLine()
            .AppendLine("| Column | Used for | Songs with a good value |")
            .AppendLine("| --- | --- | --- |");
        foreach (var (column, usedFor, isGood) in RequiredColumns)
        {
            var good = tracks.Count(isGood);
            report.AppendLine($"| {column} | {usedFor} | {(good == tracks.Count ? "✅" : "❌")} {good} of {tracks.Count} |");
        }
        foreach (var column in OptionalColumns)
        {
            var present = tracks.Count(t => t.Columns.TryGetValue(column, out var value) && value.Length > 0);
            report.AppendLine($"| {column} | optional tags | {(headers.Contains(column) ? "" : "not in the export, ")}{present} of {tracks.Count} |");
        }
        Console.WriteLine(report);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
            File.AppendAllText(stepSummary, report.ToString());

        tracks.ShouldSatisfyAllConditions(
            [
                () => summary.ShouldStartWith($"{listedCount} track", customMessage: $"The Liked Songs page should count the {listedCount} songs its tile listed"),
                .. RequiredColumns.Select(required => (Action)(() =>
                {
                    headers.ShouldContain(required.Column, $"The export should have the '{required.Column}' column, which the app uses for {required.UsedFor}");
                    var bad = tracks.Where(t => !required.IsGood(t)).Select(Describe).ToList();
                    bad.ShouldBeEmpty($"Every song should have a good '{required.Column}', which the app uses for {required.UsedFor}. {bad.Count} of {tracks.Count} don't, such as {string.Join(", ", bad.Take(3))}");
                })),
                () =>
                {
                    var bad = metadata.Where(m => m.Tags.Title.Length == 0 || m.Tags.Artists.Count == 0 || m.Tags.Album.Length == 0 || m.Tags.ReleaseDate.Length == 0 || m.Tags.Isrc.Length == 0 || m.Tags.Explicit is null)
                        .Select(m => Describe(m.Track)).ToList();
                    bad.ShouldBeEmpty($"Every song's tags should have a title, artists, album, release date, ISRC and explicit flag. {bad.Count} of {tracks.Count} don't, such as {string.Join(", ", bad.Take(3))}");
                },
            ]);
    }

    private static async Task CheckIsUpAsync(Uri site, string name, Credentials? credentials, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var response = await client.GetAsync(site, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw Inconclusive(credentials, $"{name} was down: {site} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Inconclusive(credentials, $"{name} was down: {site} didn't answer ({exception.Message}).");
        }
    }

    /// <summary>
    /// Ends the test as inconclusive: it couldn't check anything, and the app isn't to blame. The reason never contains
    /// the credentials.
    /// </summary>
    private static Exception Inconclusive(Credentials? credentials, string reason)
    {
        Skip.Test($"{TestOutcome.InconclusivePrefix} {credentials?.Hide(reason) ?? reason}");
        return new UnreachableException();
    }

    private static string Snippet(string? text) =>
        text is null ? "(nothing)" : text.Length > 300 ? text[..300] + "…" : text;

    /// <summary>
    /// The test account's email address and password, taken out of the environment so that neither the app nor its
    /// WebView2 inherits them. It's a class rather than a record, so that it never prints them.
    /// </summary>
    private sealed class Credentials(string username, string password)
    {
        private const string UsernameVariable = "SPOTIFY_TEST_USERNAME";
        private const string PasswordVariable = "SPOTIFY_TEST_PASSWORD";

        public string Username { get; } = username;

        public string Password { get; } = password;

        public static Credentials? TakeFromEnvironment()
        {
            var username = Environment.GetEnvironmentVariable(UsernameVariable);
            var password = Environment.GetEnvironmentVariable(PasswordVariable);
            Environment.SetEnvironmentVariable(UsernameVariable, null);
            Environment.SetEnvironmentVariable(PasswordVariable, null);
            return username is { Length: > 0 } && password is { Length: > 0 } ? new(username, password) : null;
        }

        /// <summary>The text with the email address and password blanked out.</summary>
        public string Hide(string text) =>
            text.Replace(Username, "***", StringComparison.OrdinalIgnoreCase).Replace(Password, "***", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^spotify:track:[0-9A-Za-z]{22}$")]
    private static partial Regex SpotifyTrackUri();

    [GeneratedRegex(@"^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$", RegexOptions.IgnoreCase)]
    private static partial Regex Isrc();

    [GeneratedRegex(@"^[0-9]{4}(-[0-9]{2}(-[0-9]{2})?)?$")]
    private static partial Regex ReleaseDate();

    [GeneratedRegex(@"^.+ · [0-9]{4}$")]
    private static partial Regex ArtistAndYear();

    [GeneratedRegex(@"^([0-9]+) tracks?$")]
    private static partial Regex TileTrackCount();

    [GeneratedRegex(@"^[0-9]+ tracks?\b")]
    private static partial Regex TrackCountSummary();

    [GeneratedRegex(@"^Spotify (playlist|album): Liked Songs$")]
    private static partial Regex LikedSongsTitle();

    [GeneratedRegex(@"email|user ?name", RegexOptions.IgnoreCase)]
    private static partial Regex EmailOrUsername();

    [GeneratedRegex(@"^(log ?in|continue|next|sign in)$", RegexOptions.IgnoreCase)]
    private static partial Regex GoOnButton();

    [GeneratedRegex(@"^agree$", RegexOptions.IgnoreCase)]
    private static partial Regex AgreeButton();

    [GeneratedRegex(@"^log ?in with (a )?password$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordOption();

    [GeneratedRegex(@"captcha|not a robot|verify (that )?you('| a)re (a )?human", RegexOptions.IgnoreCase)]
    private static partial Regex Captcha();

    [GeneratedRegex(@"incorrect (username|email)|wrong password|incorrect password", RegexOptions.IgnoreCase)]
    private static partial Regex WrongPassword();

    [GeneratedRegex(@"temporarily (blocked|locked)|account (has been )?(locked|disabled|suspended)|too many (attempts|requests)", RegexOptions.IgnoreCase)]
    private static partial Regex Blocked();

    [GeneratedRegex(@"[0-9]-digit code|enter (the|your) code|code (we|was) sent", RegexOptions.IgnoreCase)]
    private static partial Regex CodePrompt();
}
