using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using Mp3PlayerSync.MockExportify;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>What the tests do in the app, again and again: wait for it, find things in it, and go from page to page.</summary>
public static partial class AppSteps
{
    private static CancellationToken Token => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    /// <summary>Waits until <paramref name="probe"/> finds something in the app, watching it meanwhile.</summary>
    public static Task<T> UntilAsync<T>(this AppUnderTest app, Func<T?> probe, string what, double seconds = 30) where T : class =>
        Wait.UntilAsync(probe, what, TimeSpan.FromSeconds(seconds), app, Token);

    /// <summary>Waits until <paramref name="condition"/> holds in the app, watching it meanwhile.</summary>
    public static Task UntilAsync(this AppUnderTest app, Func<bool> condition, string what, double seconds = 30) =>
        Wait.UntilAsync(condition, what, TimeSpan.FromSeconds(seconds), app, Token);

    /// <summary>
    /// Waits a short while for <paramref name="condition"/>, such as a text that's still being filled in, and says whether
    /// it came to hold, rather than failing.
    /// </summary>
    public static async Task<bool> SoonAsync(this AppUnderTest app, Func<bool> condition, double seconds = 5)
    {
        try
        {
            await app.UntilAsync(condition, "", seconds);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static string Name(this AutomationElement element) => element.Properties.Name.ValueOrDefault ?? "";

    public static AutomationElement? FindNamed(this AutomationElement root, ControlType type, string name) =>
        root.FindFirstDescendant(c => c.ByControlType(type).And(c.ByName(name)));

    public static AutomationElement[] Items(this AutomationElement list) => list.FindAllChildren(c => c.ByControlType(ControlType.ListItem));

    /// <summary>The names of everything in <paramref name="element"/>, such as a track row's cells.</summary>
    public static string[] Texts(this AutomationElement element) => [.. element.FindAllDescendants().Select(Name)];

    /// <summary>The list item called <paramref name="name"/>, realized first if it's outside the viewport.</summary>
    public static AutomationElement? FindListItem(this AutomationElement list, string name)
    {
        if (list.FindNamed(ControlType.ListItem, name) is { } item) return item;
        var virtualized = list.Patterns.ItemContainer.Pattern.FindItemByProperty(null, list.Automation.PropertyLibrary.Element.Name, name);
        virtualized?.Patterns.VirtualizedItem.PatternOrDefault?.Realize();
        return virtualized;
    }

/// <summary>
    /// The first element under <paramref name="root"/> that <paramref name="match"/> picks, searched breadth first in UI
    /// Automation's raw view, which has the Win32 controls of a dialog on a test desktop that the other views leave out.
    /// </summary>
    public static AutomationElement? FindRaw(this AutomationElement root, Func<AutomationElement, bool> match)
    {
        var walker = root.Automation.TreeWalkerFactory.GetRawViewWalker();
        var queue = new Queue<AutomationElement>([root]);
        while (queue.TryDequeue(out var element))
        {
            try
            {
                if (!ReferenceEquals(element, root) && match(element)) return element;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child)) queue.Enqueue(child);
        }
        return null;
    }

    public static void Invoke(this AutomationElement element) => element.Patterns.Invoke.Pattern.Invoke();

    public static ToggleState ToggleState(this AutomationElement checkBox) => checkBox.Patterns.Toggle.Pattern.ToggleState.Value;

    public static async Task ClickAsync(this AppUnderTest app, string automationId) =>
        (await app.UntilAsync(() => app.FindById(automationId), automationId)).Invoke();

    /// <summary>Whether <paramref name="text"/> matches <paramref name="pattern"/>, where * stands for anything, ignoring case.</summary>
    public static bool Like(string? text, string pattern) =>
        text is not null && Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Selects a library tab, such as "All".</summary>
    public static async Task SelectFilterAsync(this AppUnderTest app, string name)
    {
        // Only the tabs' AutomationIds end in FilterItem: library tiles can be selected too, and a playlist could be called "All".
        var tab = await app.UntilAsync(
            () => app.Window.FindAllDescendants(c => c.ByName(name)).FirstOrDefault(e => e.Properties.AutomationId.ValueOrDefault?.EndsWith("FilterItem", StringComparison.Ordinal) == true),
            $"selector tab '{name}'");
        tab.Patterns.SelectionItem.Pattern.Select();
    }

    /// <summary>
    /// Waits for the library to show <paramref name="expectedSummary"/>, calling <paramref name="sawSignIn"/> whenever the
    /// sign-in screen shows meanwhile.
    /// </summary>
    public static Task WaitLibraryAsync(this AppUnderTest app, string expectedSummary, Action? sawSignIn = null) =>
        app.UntilAsync(() =>
        {
            if (app.FindById("SignInTitle") is not null) sawSignIn?.Invoke();
            return Like(app.Text("LibrarySummaryText"), expectedSummary) && app.FindById("LibraryBusyText") is null;
        }, $"library summary like '{expectedSummary}'", 60);

    /// <summary>Opens a playlist or album from the library.</summary>
    public static async Task OpenLibraryItemAsync(this AppUnderTest app, string name)
    {
        var list = await app.UntilAsync(() => app.FindById("LibraryGridView"), "the library list");
        // Scrolling recycles item containers, so the element found before may now stand for another item, and the grid can
        // be filled again after the scroll (such as when an export shows a playlist is an album), which leaves the tile out
        // of view. So until it's in view, it's found and scrolled to again.
        var item = await app.UntilAsync(() =>
        {
            if (list.FindListItem(name) is not { } found) return null;
            if (!found.Properties.IsOffscreen.ValueOrDefault && found.Name() == name) return found;
            found.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            return null;
        }, $"'{name}' scrolled into view", 20);
        item.Invoke();
        await app.UntilAsync(() => Regex.IsMatch(app.Text("PlaylistTitle") ?? "", "^Spotify (playlist|album): " + Regex.Escape(name) + "$"),
            $"the page for '{name}'");
    }

    /// <summary>Waits until a playlist's or album's page shows its contents, summed up as <paramref name="expectedSummary"/>.</summary>
    public static async Task WaitContentsAsync(this AppUnderTest app, Checks checks, string name, string expectedSummary, double seconds = 60)
    {
        var started = TimeProvider.System.GetTimestamp();
        var summary = await app.UntilAsync(
            () => app.Text("SummaryText") is { } s && Like(s, expectedSummary) && app.FindById("PlaylistBusyText") is null ? s : null,
            $"'{name}' contents like '{expectedSummary}'", seconds);
        checks.Passed($"'{name}' shows '{summary}' after {(int)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds} ms");
    }

    /// <summary>Clicks Refresh and waits until the app has asked Spotify again: the busy text alone can come and go between polls.</summary>
    public static async Task ClickRefreshAsync(this AppUnderTest app, MockExportifyServer mock, string requestPattern, string what)
    {
        var before = mock.CountRequests(requestPattern);
        await app.ClickAsync("RefreshPlaylistButton");
        await app.UntilAsync(() => mock.CountRequests(requestPattern) > before, $"{what} to ask Spotify again");
    }

    /// <summary>
    /// A tool's exe, matching <paramref name="exe"/>, in its version folder (<c>v…</c>) under the app's
    /// <paramref name="tools"/> folder, or null while there's none. The app moves each version into place only once it's
    /// complete. yt-dlp's exe is named after the architecture, such as <c>yt-dlp_arm64.exe</c>, except on x64.
    /// </summary>
    public static string? Tool(string tools, string folder, string exe) =>
        Directory.Exists(Path.Combine(tools, folder))
            ? Directory.GetDirectories(Path.Combine(tools, folder), "v*").SelectMany(v => Directory.GetFiles(v, exe)).FirstOrDefault()
            : null;

    /// <summary>TEXT-1: a page mentions neither the browser nor Exportify.</summary>
    public static void CheckNoBrowserText(this AppUnderTest app, Checks checks, string page)
    {
        var mentions = BrowserOrExportify().Matches(app.AllText()).Select(m => m.Value).ToList();
        checks.Check($"[TEXT-1] The {page} mentions neither the browser nor Exportify", () => mentions.ShouldBeEmpty());
    }

    /// <summary>
    /// Chooses the player on the player page, which comes first on every start, with the player chosen last time
    /// pre-selected. With <paramref name="checkPage"/>, also checks the page and the window's title, as each run does once.
    /// </summary>
    public static async Task ChoosePlayerAsync(this AppUnderTest app, Checks? checks, string playerName, bool checkPage = false)
    {
        var grid = await app.UntilAsync(() => app.FindById("PlayerGridView"), "the MP3 player page");
        checks?.Check("MP3 player page comes first", () => app.Text("PlayerTitle").ShouldBe("Select an MP3 player to sync"));
        var tile = await app.UntilAsync(
            () => grid.FindListItem(playerName) is { } t && t.Patterns.SelectionItem.Pattern.IsSelected.Value ? t : null,
            $"the '{playerName}' player, pre-selected", 10);
        checks?.Passed("Player chosen last time is pre-selected");
        checks?.Check("Nothing else shows behind the player page", () => app.ShouldSatisfyAllConditions(
            () => app.FindById("SignInTitle").ShouldBeNull(),
            () => app.FindById("LibraryGridView").ShouldBeNull()));
        if (checks is not null && checkPage)
        {
            // The title can reach UI Automation a moment after the window does, so wait for it rather than read it once.
            await app.SoonAsync(() => app.Window.Name() == "MP3 Player Sync", 10);
            checks.Check("[TEXT-3] The window is called 'MP3 Player Sync'", () => app.Window.Name().ShouldBe("MP3 Player Sync"));
            var refresh = app.Window.FindAllDescendants(c => c.ByControlType(ControlType.Button))
                .Where(b => b.Name().Contains("Refresh", StringComparison.OrdinalIgnoreCase) || (b.Properties.AutomationId.ValueOrDefault ?? "").Contains("Refresh", StringComparison.OrdinalIgnoreCase))
                .Select(Name).ToList();
            checks.Check("[PLAYER-5] The player page has no Refresh button", () => refresh.ShouldBeEmpty());
            app.CheckNoBrowserText(checks, "player page");
        }
        WindowPictures.SaveScreenshot(app, "player");
        tile.Invoke();
        await app.UntilAsync(() => app.FindById("PlayerGridView") is null, "the player page to close");
    }

    [GeneratedRegex("(?im)^.*(browser|exportify).*$")]
    private static partial Regex BrowserOrExportify();
}
