using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// Spotify's session while the app is used: tokens that run out or are refused are renewed without the user seeing it
/// (SIGNIN-3), and signing out shows the sign-in page, whose links open outside the app (SIGNIN-4). Each test has a run
/// of its own, which starts signed in with Big Album opened last.
/// </summary>
[Category(TestCategories.Session)]
public sealed class SessionTests
{
    private const string BigAlbum = "Spotify album: Big Album";
    private const string SignInPage = "Spotify login (mock)";

    [ClassDataSource<RunContext>(Shared = SharedType.None)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task A_token_that_runs_out_while_the_app_is_open_is_renewed_in_the_background(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Big Album", cancellationToken);
        var app = await Run.StartAppAsync("?expireWhenAsked", cancellationToken);
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName);
        await app.UntilAsync(() => app.Text("PlaylistTitle") == BigAlbum, "the last opened album to reopen", 60);
        await app.WaitContentsAsync(checks, "Big Album (restored)", "*60 tracks, *");

        // The token runs out now, while the test watches; the mock then refuses it, so only a renewed one works. The
        // watch lasts until the renewal has asked Spotify for a new token, and a few seconds after.
        var renewals = Run.Mock.CountRequests("^token$");
        Run.Mock.Expire();
        List<string> interrupted = [];
        TimeSpan? renewed = null;
        var stopwatch = Stopwatch.StartNew();
        while (renewed is { } at ? (stopwatch.Elapsed - at).TotalSeconds < 5 * Wait.Scale : stopwatch.Elapsed.TotalSeconds <= 60 * Wait.Scale)
        {
            app.Watch();
            if (app.FindById("SignInTitle") is not null) interrupted.Add("sign-in");
            if (app.FindById("LoadingText") is not null) interrupted.Add("loading");
            if (app.Text("PlaylistTitle") is var title && title != BigAlbum) interrupted.Add($"left the album ('{title}')");
            if (renewed is null && Run.Mock.CountRequests("^token$") > renewals) renewed = stopwatch.Elapsed;
            await Task.Delay(Wait.PollInterval, cancellationToken);
        }
        if (renewed is { } after) Console.WriteLine($"  The token was renewed after {(int)after.TotalMilliseconds} ms");
        checks.Check("The token that ran out is renewed in the background", () => renewed.ShouldNotBeNull());
        checks.Check("The album stays on screen while the token is renewed", () => interrupted.Distinct().ShouldBeEmpty());

        await app.ClickRefreshAsync(Run.Mock, "^export albums$", "the refresh");
        await app.WaitContentsAsync(checks, "Big Album (after renewal)", "*60 tracks, *");
        checks.Check("Exports work with the renewed token", () => app.AllText().ShouldNotContain("Could not load"));

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task A_token_Spotify_stops_accepting_during_an_export_is_renewed_then_the_export_is_retried(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Big Album", cancellationToken);
        var app = await Run.StartAppAsync("?expireOnExport", cancellationToken);
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName);
        var sawSignIn = await WaitForBigAlbumAsync(app, "the album after renewing the token mid-export");
        checks.Check("A token rejected mid-export is renewed without showing the sign-in screen", () => sawSignIn.ShouldBeFalse());
        checks.Check("The interrupted export is retried after renewing", () => app.AllText().ShouldNotContain("Could not load"));

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task An_expired_session_is_renewed_then_signing_out_shows_the_sign_in_page(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Big Album", cancellationToken);
        var app = await Run.StartAppAsync("?expireToken", cancellationToken);
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName);
        var sawSignIn = await WaitForBigAlbumAsync(app, "the saved album after renewing the session");
        checks.Check("Expired token is renewed without showing the sign-in screen", () => sawSignIn.ShouldBeFalse());
        checks.Passed("The last opened saved album reopened");

        await app.ClickAsync("BackButton");
        var signInPageLoads = Run.Mock.CountRequests("^sign-in page$");
        await app.ClickAsync("SignOutButton");
        await app.UntilAsync(() => app.FindById("SignInTitle"), "the sign-in screen after signing out");
        checks.Passed("Signing out shows the sign-in screen");

        // Links to elsewhere open outside the app; the sign-in page stays, and signing in still works. The mock logs
        // serving the page, which tells a page that never loaded from one UI Automation can't see.
        await app.UntilAsync(() => Run.Mock.CountRequests("^sign-in page$") > signInPageLoads, "Spotify's sign-in page to load", 60);
        AutomationElement page;
        try
        {
            page = await app.UntilAsync(() => app.FindWebPage(SignInPage), "Spotify's sign-in page in UI Automation", 60);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"  The page loaded; the sign-in button instead of the page is {(app.FindById("SignInPromptButton") is null ? "not " : "")}showing");
            throw;
        }
        var opened = Path.Combine(Run.DataDirectory, "OpenedExternally.txt");
        foreach (var (link, url) in ((string, string)[])[("Sign up for Spotify", "https://www.spotify.com/signup"), ("Help", "https://support.spotify.com/")])
        {
            (await app.UntilAsync(() => page.FindNamed(ControlType.Hyperlink, link), $"the '{link}' link", 10)).Invoke();
            await app.UntilAsync(() => ReadLines(opened).Contains(url), $"'{link}' to open outside the app", 10);
            // Asks the page found above, whose name is its title: it changes if the WebView goes elsewhere. Looking it up
            // again through the window doesn't work once the click has put focus inside the WebView.
            await app.SoonAsync(() => NameOf(page) == SignInPage && app.FindById("SignInTitle") is not null, 10);
            checks.Check($"Clicking '{link}' opens it outside the app and stays on the sign-in page", () => app.ShouldSatisfyAllConditions(
                () => NameOf(page).ShouldBe(SignInPage),
                () => app.FindById("SignInTitle").ShouldNotBeNull()));
        }

        (await app.UntilAsync(() => page.FindNamed(ControlType.Button, "Sign in"), "the page's 'Sign in' button", 10)).Invoke();
        await app.WaitLibraryAsync("*playlists*");
        checks.Check("Signing in on the page returns to the library", () => app.FindById("SignInTitle").ShouldBeNull());

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    /// <summary>Waits for Big Album to reopen with all its tracks, and says whether the sign-in screen showed meanwhile.</summary>
    private static async Task<bool> WaitForBigAlbumAsync(AppUnderTest app, string what)
    {
        var sawSignIn = false;
        await app.UntilAsync(() =>
        {
            if (app.FindById("SignInTitle") is not null) sawSignIn = true;
            return app.Text("PlaylistTitle") == BigAlbum && AppSteps.Like(app.Text("SummaryText"), "*60 tracks*");
        }, what, 90);
        return sawSignIn;
    }

    private static string? NameOf(AutomationElement element)
    {
        try
        {
            return element.Properties.Name.ValueOrDefault;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The lines of a file the app may be writing to at the time, or none yet.</summary>
    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or IOException)
        {
            return [];
        }
    }
}
