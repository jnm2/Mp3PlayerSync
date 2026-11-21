using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// Signing in and looking around: the library, its tabs and heading, a playlist's page and how it lays out at every
/// width, and saved albums and album pages. Each test has a run of its own, which starts signed out.
/// </summary>
[Category(TestCategories.Browsing)]
public sealed partial class BrowsingTests
{
    private const string RoadTrip = "Road Trip, Vol. 2";

    [ClassDataSource<RunContext>(Shared = SharedType.None)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Signing_in_shows_the_library_with_its_tabs_which_fit_the_narrowest_window(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        Run.SetUpPlayer();
        // The mock's automatic sign-in waits until the test has seen the sign-in screen, which would otherwise be up for
        // only a moment.
        Run.Mock.SignInHeld = true;
        var app = await Run.StartAppAsync("?autoSignIn", cancellationToken);
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName, checkPage: true);
        var sawLibraryEarly = false;
        await app.UntilAsync(() =>
        {
            if (app.FindById("LibraryGridView") is not null) sawLibraryEarly = true;
            return app.FindById("SignInTitle") is not null;
        }, "the sign-in screen", 60);
        checks.Check("No library page before signing in", () => sawLibraryEarly.ShouldBeFalse());
        WindowPictures.SaveScreenshot(app, "signin");
        var signInText = $"{app.Text("SignInTitle")} {app.Text("SignInText")}";
        checks.Check("Sign-in text mentions neither browser nor Exportify", () => signInText.ShouldNotMatch("(?i)browser|exportify"));
        Run.Mock.SignInHeld = false;

        await app.WaitLibraryAsync("40 playlists");
        checks.Check("Sign-in screen is hidden after signing in", () => app.FindById("SignInTitle").ShouldBeNull());
        // The summary changes in the same call that fills the grid, before its tiles are laid out.
        var first = await app.UntilAsync(() => app.FindById("LibraryGridView")?.Items().FirstOrDefault()?.Name(), "the first tile in the library", 10);
        checks.Check("Liked Songs is first", () => first.ShouldBe("Liked Songs"));
        var firstTab = app.Window.FindAllDescendants(c => c.ByName("Created playlists")).First(e => e.Patterns.SelectionItem.IsSupported);
        // Through UI Automation's control view: the raw view leaves out the first tab while it's selected.
        var walker = app.Window.Automation.TreeWalkerFactory.GetControlViewWalker();
        List<AutomationElement> siblings = [];
        for (var tab = walker.GetFirstChild(walker.GetParent(firstTab)); tab is not null; tab = walker.GetNextSibling(tab)) siblings.Add(tab);
        var tabs = siblings.Where(e => e.Patterns.SelectionItem.IsSupported).OrderBy(e => e.BoundingRectangle.Left).Select(AppSteps.Name).ToList();
        checks.Check("[GRID-6] Tabs", () => tabs.ShouldBe(["Created playlists", "Saved playlists", "Saved albums", "All"]));
        checks.Check("[TEXT-4] Sign-out button", () => app.Text("SignOutButton").ShouldBe("Sign out of Spotify"));
        app.CheckNoBrowserText(checks, "library page");

        WindowPictures.SaveScreenshot(app, "library");
        app.CheckHeading(checks, "ChangePlayerButton", "SelectedPlayerTitle", "Player heading");
        app.CheckSpacing(checks, "Playlists page",
            ["ChangePlayerButton", "SelectedPlayerTitle", "LibraryTitle", "FilterBar", "RefreshLibraryButton", "SignOutButton", "LibrarySummaryText"],
            [("RefreshLibraryButton", "SignOutButton")]);

        // The window can't be made so narrow that a tab, Refresh or Sign out is cut off.
        string[] toolbar = ["AllFilterItem", "RefreshLibraryButton", "SignOutButton"];
        await app.ResizeAsync(400, 900, toolbar, "at the narrowest window");
        List<AutomationElement> tabItems = [];
        foreach (var id in (string[])["CreatedFilterItem", "SavedPlaylistsFilterItem", "SavedAlbumsFilterItem", "AllFilterItem"])
            tabItems.Add(await app.UntilAsync(() => app.FindById(id), id, 10));
        var clipped = tabItems.Where(t => t.Properties.IsOffscreen.ValueOrDefault || t.BoundingRectangle.Width <= 0).Select(AppSteps.Name).ToList();
        var refresh = app.Bounds("RefreshLibraryButton");
        var signOut = app.Bounds("SignOutButton");
        var window = app.Window.BoundingRectangle;
        Console.WriteLine($"  The narrowest window is {window.Width} px wide");
        checks.Check("At the narrowest window the tabs, Refresh and Sign out all fit", () => window.ShouldSatisfyAllConditions(
            () => clipped.ShouldBeEmpty(),
            () => tabItems.Max(t => t.BoundingRectangle.Right).ShouldBeLessThanOrEqualTo(refresh.Left),
            () => signOut.Right.ShouldBeLessThanOrEqualTo(window.Right - 8)));
        WindowPictures.SaveScreenshot(app, "library-narrowest");
        await app.ResizeAsync(1280, 900, toolbar, "at 1280 px");

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task A_playlist_page_shows_its_songs_with_the_columns_lined_up_at_every_width(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        var app = await Run.SignInAsync(cancellationToken);
        var libraryHeading = app.HeadingLayout("ChangePlayerButton", "LibraryTitle");

        // TEXT-15: one line counts the songs found while they're looked for, then goes away. The fake finder would be done
        // in a second or two, so it's held after its first few songs until the line has been checked.
        var holdFinding = Path.Combine(Run.DataDirectory, "HoldFinding.txt");
        File.WriteAllText(holdFinding, "");
        try
        {
            await app.OpenLibraryItemAsync(RoadTrip);
            await app.WaitContentsAsync(checks, RoadTrip, "250 tracks, 21:08:45");
            var findingText = await app.UntilAsync(() => app.Text("FindingProgressText") is { } text && FindingLine().IsMatch(text) ? text : null, "the finding progress line");
            WindowPictures.SaveScreenshot(app, "finding-progress");
            checks.Check("[TEXT-15] One line counts the songs being looked for", () => findingText.ShouldMatch(@"^Finding sources… \d+ of 2\d\d$"));
        }
        finally
        {
            File.Delete(holdFinding);
        }
        await app.UntilAsync(() => app.FindById("FindingProgressText") is null, "the finding progress line to go away");
        checks.Passed("[TEXT-15] The finding progress line goes away when finding finishes");
        var allText = app.AllText();
        checks.Check("The export's warning (HTTP 403) is surfaced", () => allText.ShouldMatch("(?is)Some details could not be loaded.*403"));
        checks.Check("[TEXT-1] The warning is in the app's words", () => allText.ShouldSatisfyAllConditions(
            () => allText.ShouldContain("Spotify refused some requests (HTTP 403)"),
            () => allText.ShouldNotMatch("(?i)browser|exportify")));
        var firstRow = app.FindById("TracksListView")?.Items().FirstOrDefault();
        checks.Check("First track row", () => firstRow.ShouldNotBeNull().Name().ShouldBe("roadtrip song 1 — Earth, Wind & Fire, Chaka Khan"));
        checks.Check("[TEXT-5] Contents banner", () => app.Text("ContentsBannerTitle").ShouldBe("Choose songs to sync"));

        // Above the track list, only the heading (RichTextBlock, named "Spotify playlist: …") carries the name, and only it has art.
        var listTop = app.Bounds("TracksListView").Top;
        var bannerBottom = app.Bounds("ContentsBannerTitle").Bottom;
        var repeats = app.Window.FindAllDescendants(c => c.ByName(RoadTrip)).Where(e => e.BoundingRectangle.Bottom <= listTop);
        // Images are art, not icons or a busy ring's animation (which UI Automation also calls an image).
        var hero = app.Window.FindAllDescendants(c => c.ByControlType(ControlType.Image))
            .Where(e => e.BoundingRectangle is { IsEmpty: false, Width: >= 32, Height: >= 32 } r && r.Top >= bannerBottom && r.Bottom <= listTop);
        var underBanner = repeats.Concat(hero).Select(Describe).ToList();
        Console.WriteLine($"  The banner's bottom is at {bannerBottom} and the list's top at {listTop}");
        checks.Check("[CONTENTS-2] No hero art or repeated name under the banner", () => underBanner.ShouldBeEmpty());
        app.CheckNoBrowserText(checks, "contents page");
        WindowPictures.SaveScreenshot(app, "playlist");
        app.CheckHeading(checks, "BackButton", "PlaylistTitle", "Playlist heading");
        Layout.CheckSameHeadingLayout(checks, libraryHeading, app.HeadingLayout("BackButton", "ContentsBannerTitle"));

        // The header's columns line up with the rows' at every width, and nothing is cut off.
        var row = app.FindById("TracksListView")?.Items().FirstOrDefault().ShouldNotBeNull();
        string[] headers = ["TitleHeader", "ArtistsHeader", "AlbumHeader", "NumberHeader", "TimeHeader", "SyncButton"];
        foreach (var width in (int[])[800, 1200, 1900])
        {
            await app.ResizeAsync(width, 900, headers, $"at {width} px", () => Cell(row!, "roadtrip song 1"));
            var time = row!.FindAllDescendants().Select(AppSteps.Name).FirstOrDefault(name => Duration().IsMatch(name)) ?? "";
            (string Header, string Cell, bool LeftEdge)[] edges =
            [
                ("TitleHeader", "roadtrip song 1", true),
                ("ArtistsHeader", "Earth, Wind & Fire, Chaka Khan", true),
                ("AlbumHeader", "Album 0", true), // The first track's album in the mock's test library
                ("NumberHeader", "1", false),
                ("TimeHeader", time, false),
            ];
            var off = new List<string>();
            foreach (var edge in edges)
            {
                var header = app.Bounds(edge.Header);
                var cell = Cell(row, edge.Cell);
                var delta = edge.LeftEdge ? cell.Left - header.Left : cell.Right - header.Right;
                if (Math.Abs(delta) > 1) off.Add($"{edge.Header} off by {delta}");
            }
            checks.Check($"At {width} px the columns line up with the header", () => off.ShouldBeEmpty());
            var windowRight = app.Window.BoundingRectangle.Right;
            var clipped = ((string[])["SyncButton", "RefreshPlaylistButton", "SyncSummaryText", "OpenPlayerFolderButton", "RemoveHeader"])
                .Where(id => app.Bounds(id) is var bounds && (bounds.IsEmpty || bounds.Right > windowRight - 8)).ToList();
            checks.Check($"At {width} px nothing in the header is cut off", () => clipped.ShouldBeEmpty());
            app.CheckPlayerBesideHeading(checks, width);
            app.CheckSpacing(checks, $"At {width} px the playlist's header",
                ["BackButton", "PlaylistTitle", "ContentsPlayerText", "OpenPlayerFolderButton", "ContentsBannerTitle", "SummaryText", "RefreshPlaylistButton",
                    "SyncSummaryText", "SyncButton", "SelectAllCheckBox", "NumberHeader", "TitleHeader", "ArtistsHeader", "AlbumHeader", "TimeHeader",
                    "AddedHeader", "StatusHeader", "RemoveHeader"],
                [("ContentsPlayerText", "OpenPlayerFolderButton"), ("RefreshPlaylistButton", "SyncSummaryText")]);
            if (width == 1200)
            {
                var cut = app.TruncatedCells(app.Bounds("StatusHeader").Left);
                checks.Check("At 1200 px no status is cut off", () => cut.ShouldBeEmpty());
            }
            if (width == 1900)
            {
                var cut = app.TruncatedCells();
                checks.Check("At 1900 px no cell is cut off", () => cut.ShouldBeEmpty());
            }
            WindowPictures.SaveScreenshot(app, $"playlist-{width}");
        }
        await app.ResizeAsync(1200, 900, headers, "at 1200 px");

        // The contents page names the player: in full once it's filled in, just after the page opens.
        var player = $"Player: Folder ({Run.PlayerFolder})";
        await app.SoonAsync(() => app.Text("ContentsPlayerText") == player);
        checks.Check("The contents page names the player", () => app.Text("ContentsPlayerText").ShouldBe(player));
        CheckContentsLayout(app, checks);
        checks.Check("The playlist offers to open its folder on the player", () => app.FindById("OpenPlayerFolderButton").ShouldNotBeNull());
        checks.Check("The player is changed from the player page, not the playlist", () => app.FindById("ContentsChangePlayerButton").ShouldBeNull());

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Saved_albums_list_album_like_playlists_then_saved_albums_whose_pages_leave_out_the_album(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        var app = await Run.SignInAsync(cancellationToken);
        await app.SelectFilterAsync("Saved playlists");
        await app.WaitLibraryAsync("19 playlists");

        await app.SelectFilterAsync("Saved albums");
        await app.WaitLibraryAsync("5 albums");
        var albums = await app.UntilAsync(
            () => app.FindById("LibraryGridView")?.Items().Select(AppSteps.Name).ToList() is { Count: >= 5 } names ? names : null,
            "the saved albums' tiles", 10);
        checks.Check("Saved albums: album-like playlists, then saved albums newest first",
            () => albums.ShouldBe(["Playlist 5", "Playlist 14", "Mock Album One", "Big Album", "Old Album"]));
        WindowPictures.SaveScreenshot(app, "saved-albums");
        await app.OpenLibraryItemAsync("Big Album");
        await app.WaitContentsAsync(checks, "Big Album", "Prolific Band · 2010 — 60 tracks, 3:29:30");
        checks.Check("Album pages leave out the Album column", () => app.ShouldSatisfyAllConditions(
            () => app.FindById("AlbumHeader").ShouldBeNull(),
            () => app.FindById("ArtistsHeader").ShouldNotBeNull()));
        // The summary already matches, so wait until the refresh has asked for the Saved Albums export again.
        await app.ClickRefreshAsync(Run.Mock, "^export albums$", "the album refresh");
        await app.WaitContentsAsync(checks, "Big Album (refreshed)", "*60 tracks, *");
        await app.ClickAsync("BackButton");

        // Playlist 5's cover looks like an album, but its export shows four albums.
        await OpenThenGoBackAsync(app, checks, "Playlist 5", "6 tracks, *");
        await app.WaitLibraryAsync("4 albums");
        checks.Passed("Opening Playlist 5 moved it out of Saved albums");
        await OpenThenGoBackAsync(app, checks, "Playlist 14", "1 track, *");
        await app.WaitLibraryAsync("4 albums");
        checks.Passed("Playlist 14, a single album, stays in Saved albums");
        await app.SelectFilterAsync("Created playlists");
        await app.WaitLibraryAsync("41 playlists");

        await app.SelectFilterAsync("All");
        await app.WaitLibraryAsync("64 playlists and albums");
        await app.OpenLibraryItemAsync("Broken");
        await app.UntilAsync(() => AppSteps.Like(app.AllText(), "*Could not load*Broken*"), "the failure for Broken");
        checks.Passed("Failed export is reported");
        var failure = app.AllText();
        checks.Check("[TEXT-1] The failure is in the app's words", () => failure.ShouldNotMatch("(?i)browser|exportify"));
        checks.Check("A playlist that did not load offers Try again, and nothing to sync", () => app.ShouldSatisfyAllConditions(
            () => app.FindById("TryAgainButton").ShouldNotBeNull(),
            () => app.FindById("SyncButton").ShouldBeNull(),
            () => app.Text("SyncSummaryText").ShouldBeNullOrEmpty()));
        await app.ClickAsync("BackButton");
        await OpenThenGoBackAsync(app, checks, "Liked Songs", "7 tracks, *");
        await OpenThenGoBackAsync(app, checks, "Playlist 60", "5 tracks, *");

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    private static async Task OpenThenGoBackAsync(AppUnderTest app, Checks checks, string name, string expectedSummary)
    {
        await app.OpenLibraryItemAsync(name);
        await app.WaitContentsAsync(checks, name, expectedSummary);
        await app.ClickAsync("BackButton");
    }

    /// <summary>
    /// CONTENTS-1: the heading with the Player line right-justified beside it and Open folder under that; the banner; then
    /// the count on the left and Refresh, the sync summary and Sync to player on the right. (The Player line can sit
    /// beside the banner's line, which ALIGN-3 keeps where it is on the other pages.)
    /// </summary>
    private static void CheckContentsLayout(AppUnderTest app, Checks checks)
    {
        var title = app.Bounds("PlaylistTitle");
        var player = app.Bounds("ContentsPlayerText");
        var openFolder = app.Bounds("OpenPlayerFolderButton");
        var banner = app.Bounds("ContentsBannerTitle");
        var summary = app.Bounds("SummaryText");
        var refresh = app.Bounds("RefreshPlaylistButton");
        var syncSummary = app.Bounds("SyncSummaryText");
        var sync = app.Bounds("SyncButton");
        var misplaced = new List<string>();
        if (!(player.Top < title.Bottom && player.Bottom > title.Top)) misplaced.Add("the Player line is not beside the heading");
        if (Math.Abs(player.Right - sync.Right) > 24) misplaced.Add($"the Player line is not right-justified (right edge {player.Right}, Sync to player's {sync.Right})");
        if (openFolder.Top < player.Bottom - 1) misplaced.Add("Open folder is not under the Player line");
        if (banner.Top < title.Bottom - 1) misplaced.Add("the banner is not under the heading");
        if (summary.Top < Math.Max(banner.Bottom, openFolder.Bottom) - 1) misplaced.Add("the toolbar is not under the banner and Open folder");
        if (!(summary.Right <= refresh.Left && refresh.Right <= syncSummary.Left && syncSummary.Right <= sync.Left))
            misplaced.Add("the toolbar is not count, Refresh, summary, Sync to player");
        checks.Check("[CONTENTS-1] Contents page layout", () => misplaced.ShouldBeEmpty());
    }

    /// <summary>Where the text <paramref name="text"/> is in a track row, or an empty rectangle if it isn't there.</summary>
    private static System.Drawing.Rectangle Cell(AutomationElement row, string text) =>
        row.FindFirstDescendant(c => c.ByName(text).And(c.ByControlType(ControlType.Text)))?.BoundingRectangle ?? System.Drawing.Rectangle.Empty;

    private static string Describe(AutomationElement element)
    {
        var bounds = element.BoundingRectangle;
        return $"{element.Properties.ControlType.ValueOrDefault} '{element.Name()}' {element.Properties.ClassName.ValueOrDefault} " +
            $"#{element.Properties.AutomationId.ValueOrDefault} at {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}";
    }

    [GeneratedRegex(@"^Finding sources… \d+ of \d+$")]
    private static partial Regex FindingLine();

    [GeneratedRegex(@"^\d+:\d\d$")]
    private static partial Regex Duration();
}
