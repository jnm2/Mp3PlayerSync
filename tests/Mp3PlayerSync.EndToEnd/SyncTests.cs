using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// Syncing a playlist: which songs it starts with and keeps across a refresh, removing a file that's no longer in the
/// playlist, cancelling, and settling uncertain matches before the songs are copied and tagged. Each test has a run of
/// its own, which starts signed in, with the player holding one Road Trip song, a file no longer in Road Trip, and a
/// Road Trip song elsewhere on the player.
/// </summary>
[Category(TestCategories.Sync)]
public sealed partial class SyncTests
{
    private const string RoadTrip = "Road Trip, Vol. 2";
    private const string Cafe1 = "cafe song 1 — Earth, Wind & Fire, Chaka Khan";
    private const string Cafe2 = "cafe song 2 — Artist 1";
    private const string Cafe3 = "cafe song 3 — Artist 2";

    [ClassDataSource<RunContext>(Shared = SharedType.None)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Songs_start_selected_unless_on_the_player_which_refreshing_keeps_then_a_removal_syncs(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        var app = await Run.SignInAsync(cancellationToken);
        await app.OpenLibraryItemAsync(RoadTrip);
        await app.WaitContentsAsync(checks, RoadTrip, "250 tracks, 21:08:45");

        // Every track starts selected except the one already on the player.
        var selectAll = await app.UntilAsync(() => app.FindById("SelectAllCheckBox"), "Select all");
        await app.UntilAsync(() => selectAll.ToggleState() == ToggleState.Indeterminate, "all but one track selected by default", 15);
        checks.Passed("Tracks not on the player are selected by default");
        var tracks = await app.UntilAsync(() => app.FindById("TracksListView"), "the track list");
        var onPlayer = await Row(app, tracks, "roadtrip song 2 — Artist 1");
        checks.Check("The track in the playlist's folder shows \"On player\"", () => onPlayer.Texts().ShouldContain("On player"));
        Layout.CheckTones(checks, [app.StatusTone(onPlayer, "On player")], ["Success"], "[STATUS-1] \"On player\" has a success icon");
        checks.Check("[STATUS-1] Narrator never reads an icon glyph", () => onPlayer.Texts().Where(text => IconGlyph().IsMatch(text)).ShouldBeEmpty());
        checks.Check("...and starts unselected", () => Box(onPlayer).ToggleState().ShouldBe(ToggleState.Off));
        var elsewhere = await Row(app, tracks, "roadtrip song 5 — Artist 4");
        checks.Check("A track elsewhere on the player is still selected for the playlist", () => Box(elsewhere).ToggleState().ShouldBe(ToggleState.On));
        var playerOnly = await Row(app, tracks, "Removed song — Gone");
        checks.Check("A file no longer in the playlist is listed as not in the playlist", () => playerOnly.Texts().ShouldContain("Not in playlist"));
        checks.Check("...and is not marked for removal", () => Box(playerOnly).ToggleState().ShouldBe(ToggleState.Off));
        checks.Check("Sync button reads 'Sync to player'", () => app.AllText().ShouldContain("Sync to player"));
        checks.Check("Summary: 'Adding 249 songs'", () => app.Text("SyncSummaryText").ShouldBe("Adding 249 songs"));

        // Refreshing keeps what the user ticked and unticked.
        Box(elsewhere).Patterns.Toggle.Pattern.Toggle();
        Box(playerOnly).Patterns.Toggle.Pattern.Toggle();
        await app.ClickRefreshAsync(Run.Mock, "^export roadtrip$", "the refresh");
        await app.WaitContentsAsync(checks, $"{RoadTrip} (refreshed with choices)", "250 tracks, *");
        tracks = await app.UntilAsync(() => app.FindById("TracksListView"), "the track list after refreshing");
        elsewhere = await Row(app, tracks, "roadtrip song 5 — Artist 4");
        playerOnly = await Row(app, tracks, "Removed song — Gone");
        var leftAlone = await Row(app, tracks, "roadtrip song 3 — Artist 2");
        checks.Check("A song the user unticked stays unticked after refreshing", () => Box(elsewhere).ToggleState().ShouldBe(ToggleState.Off));
        checks.Check("A file the user chose to remove stays chosen after refreshing", () => Box(playerOnly).ToggleState().ShouldBe(ToggleState.On));
        checks.Check("Songs the user left alone keep their defaults", () => Box(leftAlone).ToggleState().ShouldBe(ToggleState.On));
        Box(elsewhere).Patterns.Toggle.Pattern.Toggle();
        Box(playerOnly).Patterns.Toggle.Pattern.Toggle();
        selectAll = await app.UntilAsync(() => app.FindById("SelectAllCheckBox"), "Select all");
        await app.UntilAsync(() => selectAll.ToggleState() == ToggleState.Indeterminate, "select all to be indeterminate again", 5);

        var sync = await app.UntilAsync(() => app.FindById("SyncButton"), "the Sync button");
        checks.Check("Sync button is enabled with tracks selected", () => sync.IsEnabled.ShouldBeTrue());
        selectAll.Patterns.Toggle.Pattern.Toggle(); // Indeterminate to unchecked
        await app.UntilAsync(() => !sync.IsEnabled, "the Sync button to disable with nothing selected", 5);
        checks.Check("Select all clears every track", () => selectAll.ToggleState().ShouldBe(ToggleState.Off));

        // Removing the player-only file, and syncing nothing else
        checks.Check("No summary when nothing would change", () => app.Text("SyncSummaryText").ShouldBeNullOrEmpty());
        Box(playerOnly).Patterns.Toggle.Pattern.Toggle();
        await app.UntilAsync(() => sync.IsEnabled, "the Sync button to enable for a removal", 5);
        checks.Check("Summary: 'Removing 1 song'", () => app.Text("SyncSummaryText").ShouldBe("Removing 1 song"));
        sync.Invoke();
        await app.UntilAsync(() => !File.Exists(Run.RemovedFile) && tracks.FindNamed(ControlType.ListItem, "Removed song — Gone") is null,
            "the removed file to be deleted and leave the list", 20);
        checks.Passed("Syncing deletes a player-only file marked for removal");

        selectAll.Patterns.Toggle.Pattern.Toggle();
        await app.UntilAsync(() => sync.IsEnabled, "the Sync button to enable again", 5);
        checks.Passed("Select all selects every track again");

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Cancelling_during_the_downloads_leaves_the_player_as_it_was(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        var app = await Run.SignInAsync(cancellationToken);
        await app.OpenLibraryItemAsync(RoadTrip);
        await app.WaitContentsAsync(checks, RoadTrip, "250 tracks, 21:08:45");

        var filesBefore = PlayerFiles();
        var sync = await app.UntilAsync(() => app.FindById("SyncButton") is { IsEnabled: true } button ? button : null, "the Sync button before cancelling", 15);
        sync.Invoke();
        await app.UntilAsync(() => app.AllText().Contains("Downloading", StringComparison.OrdinalIgnoreCase), "the downloads to start");
        checks.Check("The Sync button becomes Cancel sync", () => app.AllText().ShouldContain("Cancel sync"));
        checks.Check("Progress: 'Syncing N of 249 songs'", () => app.Text("SyncProgressText").ShouldNotBeNull().ShouldMatch(@"^Syncing \d+ of 249 songs$"));
        checks.Check("The playlist cannot be left while syncing", () => app.FindById("BackButton").ShouldNotBeNull().IsEnabled.ShouldBeFalse());
        sync.Invoke();
        await app.UntilAsync(() => app.AllText().Contains("Sync to player", StringComparison.OrdinalIgnoreCase), "the sync to stop", 15);
        checks.Check("Cancelling says nothing on the player changed", () => app.AllText().ShouldMatch(@"(?is)Sync cancelled\..*Nothing on the player changed\."));
        checks.Check("Cancelling during the downloads leaves the player's files as they were", () => PlayerFiles().ShouldBe(filesBefore, ignoreOrder: true));
        checks.Check("No song is left looking busy after cancelling", () => app.AllText().ShouldNotMatch("(?im)^(Downloading|Waiting|Saving)"));
        await app.UntilAsync(() => app.Text("SyncSummaryText") == "Adding 249 songs", "the summary after cancelling", 10);
        checks.Passed("Every song is still to be added after cancelling");

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Syncing_finds_every_song_first_has_the_user_settle_uncertain_matches_then_copies_and_tags_them(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        var app = await Run.SignInAsync(cancellationToken);
        await app.OpenLibraryItemAsync("Café ☕ \"Quotes\"");
        await app.WaitContentsAsync(checks, "Café", "3 tracks, *");
        var sync = await app.UntilAsync(() => app.FindById("SyncButton") is { IsEnabled: true } button ? button : null, "the Sync button for Café", 15);
        checks.Check("Café summary: 'Adding 3 songs'", () => app.Text("SyncSummaryText").ShouldBe("Adding 3 songs"));
        var tracks = await app.UntilAsync(() => app.FindById("TracksListView"), "Café's track list");
        await app.UntilAsync(() =>
            tracks.FindListItem(Cafe1)?.Texts().Contains("Source found") == true &&
            tracks.FindListItem(Cafe2)?.Texts().Contains("Needs review") == true &&
            tracks.FindListItem(Cafe3)?.Texts().Contains("Couldn't search") == true,
            "the songs to be looked for before syncing", 15);
        checks.Passed("Songs are looked for as soon as the playlist opens");
        Layout.CheckTones(checks,
            [app.StatusTone(tracks.FindListItem(Cafe1)!, "Source found"), app.StatusTone(tracks.FindListItem(Cafe2)!, "Needs review"), app.StatusTone(tracks.FindListItem(Cafe3)!, "Couldn't search")],
            ["None", "Caution", "Critical"],
            "[STATUS-1] Source found has no icon, Needs review a caution icon, Couldn't search a critical icon");
        WindowPictures.SaveScreenshot(app, "status-icons");
        checks.Check("Review button: 'Review 2 songs'", () => app.Text("ReviewMatchesButton").ShouldBe("Review 2 songs"));
        await app.ClickAsync("ReviewMatchesButton");
        var review = await app.UntilAsync(() => app.FindById("ReviewListView"), "the review before syncing", 15);
        checks.Check("Review title: 'Choose a match for 2 songs'", () => app.Text("ReviewTitle").ShouldBe("Choose a match for 2 songs"));
        checks.Check("Continue waits until every song is settled", () => app.FindById("ReviewContinueButton").ShouldNotBeNull().IsEnabled.ShouldBeFalse());
        checks.Check("The track list makes way for the review", () => app.ShouldSatisfyAllConditions(
            () => app.FindById("TracksListView").ShouldBeNull(),
            () => app.FindById("SyncButton").ShouldBeNull()));

        var song2 = await app.UntilAsync(() => review.FindListItem(Cafe2), "cafe song 2 under review", 10);
        checks.Check("A result without usable audio says why", () => ItemText(song2).ShouldContain("Only Opus audio is available."));
        var unusable = await InItem(app, song2, ControlType.Button, "Use cafe song 2 (Remix) for cafe song 2");
        checks.Check("...and cannot be used", () => unusable.IsEnabled.ShouldBeFalse());
        await InItem(app, song2, ControlType.Hyperlink, "Open cafe song 2 (Live) on YouTube Music");
        checks.Passed("Each result links to YouTube Music");
        (await InItem(app, song2, ControlType.Edit, "Link to cafe song 2")).Patterns.Value.Pattern.SetValue("https://example.com/song");
        (await InItem(app, song2, ControlType.Button, "Use the link for cafe song 2")).Invoke();
        await app.UntilAsync(() => ItemText(song2).Contains("isn't a YouTube or YouTube Music link", StringComparison.OrdinalIgnoreCase), "the bad link error", 10);
        checks.Passed("A link that is not to YouTube is rejected with a message");
        (await InItem(app, song2, ControlType.Button, "Use cafe song 2 (Live) for cafe song 2")).Invoke();
        await app.UntilAsync(() => ItemText(song2).Contains("Using cafe song 2", StringComparison.OrdinalIgnoreCase), "cafe song 2 to be settled", 10);
        checks.Passed("Choosing a result settles the song");

        // Backing out keeps the choice; syncing then only asks about what's still unsettled.
        await app.ClickAsync("ReviewCancelButton");
        tracks = await app.UntilAsync(() => app.FindById("TracksListView"), "the track list after the review", 10);
        await app.UntilAsync(() => tracks.FindListItem(Cafe2)?.Texts().Contains("Source found") == true, "the chosen match in the list", 10);
        checks.Check("A choice made before backing out is kept: 'Review 1 song'", () => app.Text("ReviewMatchesButton").ShouldBe("Review 1 song"));
        await app.ClickAsync("SyncButton");
        review = await app.UntilAsync(() => app.FindById("ReviewListView"), "the review step when syncing", 15);
        checks.Check("Syncing only asks about the unsettled song: 'Choose a match for 1 song'", () => app.Text("ReviewTitle").ShouldBe("Choose a match for 1 song"));

        var song3 = await app.UntilAsync(() => review.FindListItem(Cafe3), "cafe song 3 under review", 10);
        checks.Check("A failed search says so", () => ItemText(song3).ShouldContain("Couldn't search YouTube Music"));
        await InItem(app, song3, ControlType.Button, "Search again for cafe song 3");
        checks.Passed("...and offers to search again");
        (await InItem(app, song3, ControlType.Button, "Skip cafe song 3")).Invoke();
        await app.UntilAsync(() => app.Text("ReviewTitle") == "Ready to sync", "every song to be settled", 10);
        (await app.UntilAsync(() => app.FindById("ReviewContinueButton") is { IsEnabled: true } button ? button : null, "Continue to enable", 5)).Invoke();

        // cafe song 1 matched automatically, but its video has gone by the time it downloads.
        await app.UntilAsync(() => app.Text("ReviewTitle") == "Choose a match for 1 song", "the vanished video to come back for review", 20);
        review = await app.UntilAsync(() => app.FindById("ReviewListView"), "the review of the vanished video", 10);
        var song1 = await app.UntilAsync(() => review.FindListItem(Cafe1), "cafe song 1 under review", 10);
        checks.Passed("A song whose video has gone goes back for review");
        (await InItem(app, song1, ControlType.Button, "Use cafe song 1 (Live) for cafe song 1")).Invoke();
        (await app.UntilAsync(() => app.FindById("ReviewContinueButton") is { IsEnabled: true } button ? button : null, "Continue to enable again", 10)).Invoke();

        tracks = await app.UntilAsync(() => app.FindById("TracksListView"), "the track list after syncing", 20);
        await app.UntilAsync(() =>
            tracks.FindListItem(Cafe1)?.Texts().Contains("On player") == true && tracks.FindListItem(Cafe2)?.Texts().Contains("On player") == true,
            "both songs on the player");
        var cafeFiles = Directory.GetFiles(Run.PlayerFolder, "*.m4a", SearchOption.AllDirectories)
            .Where(file => Path.GetDirectoryName(file)!.Contains("Caf", StringComparison.OrdinalIgnoreCase)).ToList();
        checks.Check("Both settled songs were copied into the playlist's folder", () => cafeFiles.Count.ShouldBe(2));
        // Tagged on the way (the fake audio has none of its own), as read by Windows itself
        var tags = ReadTags(cafeFiles);
        checks.Check("The copied songs are tagged", () => tags.ShouldSatisfyAllConditions(
            () => tags.Count.ShouldBe(2),
            () => tags.ShouldAllBe(tag => tag.Title.StartsWith("cafe song ", StringComparison.OrdinalIgnoreCase) && tag.Artist.Length > 0)));
        // The fake YouTube Music's matches have 640-pixel PNG covers on the mock, which the app fetches and converts with
        // Windows' own imaging, trimmed as it's released.
        Console.WriteLine($"  The app fetched {Run.Mock.CountRequests("^album cover$")} album cover(s) from the mock");
        var covers = cafeFiles.Select(Cover).ToList();
        checks.Check("[TAG-3] The copied songs have their album's cover as a 500×500 baseline JPEG",
            () => covers.ShouldBe(["500×500 baseline JPEG", "500×500 baseline JPEG"]));
        var result = string.Join(' ', app.AllText().Split('\n').Where(line => SyncResult().IsMatch(line)));
        checks.Check("The sync says what it did", () => result.ShouldMatch(@"(?is)Synced 2 songs to Folder \(.*\).*1 song skipped\."));

        var skipped = await Row(app, tracks, Cafe3);
        checks.Check("The skipped song shows as skipped and unselected", () => skipped.ShouldSatisfyAllConditions(
            () => skipped.Texts().ShouldContain("Skipped"),
            () => Box(skipped).ToggleState().ShouldBe(ToggleState.Off)));
        Layout.CheckTones(checks, [app.StatusTone(skipped, "Skipped")], ["Neutral"], "[STATUS-1] Skipped has a neutral icon");
        (await InItem(app, skipped, ControlType.Hyperlink, "Undo skipping cafe song 3")).Invoke();
        await app.UntilAsync(() => Box(skipped).ToggleState() == ToggleState.On, "the undone skip to be selected again", 5);
        checks.Check("Undoing a skip selects the song again", () => skipped.Texts().ShouldNotContain("Skipped"));

        // Deleting a synced song's file on the player is noticed, like any other change on the player.
        var synced = await Row(app, tracks, Cafe2);
        var syncedFile = cafeFiles.First(file => Path.GetFileName(file).Contains("cafe song 2", StringComparison.OrdinalIgnoreCase));
        // The shell's property handler, or an antivirus scan of the new file, can still have it open for a moment.
        await app.UntilAsync(() => TryDelete(syncedFile), "the synced file to be deleted", 5);
        await app.UntilAsync(() => !synced.Texts().Contains("On player") && Box(synced).ToggleState() == ToggleState.On, "the deleted song to be back to adding", 20);
        checks.Passed("A synced song deleted from the player goes back to being added");
        WindowPictures.SaveScreenshot(app, "synced");

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    /// <summary>A track row, realized first if it's outside the viewport.</summary>
    private static Task<AutomationElement> Row(AppUnderTest app, AutomationElement tracks, string name) =>
        app.UntilAsync(() => tracks.FindListItem(name), $"the row '{name}'", 10);

    /// <summary>A row's check box: whether to sync the song, or for a file no longer in the playlist, whether to remove it.</summary>
    private static AutomationElement Box(AutomationElement row) =>
        row.FindFirstDescendant(c => c.ByControlType(ControlType.CheckBox)) ?? throw new InvalidOperationException($"The row '{row.Name()}' has no check box.");

    private static Task<AutomationElement> InItem(AppUnderTest app, AutomationElement item, ControlType type, string name) =>
        app.UntilAsync(() => item.FindNamed(type, name), $"'{name}'", 10);

    private static string ItemText(AutomationElement item) =>
        string.Join('\n', item.FindAllDescendants(c => c.ByControlType(ControlType.Text)).Select(AppSteps.Name));

    private List<string> PlayerFiles() => [.. Directory.EnumerateFiles(Run.PlayerFolder, "*", SearchOption.AllDirectories)];

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Tag(string Title, string Artist);

    /// <summary>
    /// The songs' title and artists, as Windows reads them (Shell.Application, on an STA thread of its own as the shell
    /// expects), and let go of again, so the shell doesn't keep the files open.
    /// </summary>
    private static List<Tag> ReadTags(List<string> files)
    {
        List<Tag> tags = [];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ReadOn(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Windows couldn't read the songs' tags.", failure);
        foreach (var tag in tags) Console.WriteLine($"  Tags: '{tag.Title}' by '{tag.Artist}'");
        return tags;

        void ReadOn()
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
            try
            {
                foreach (var file in files)
                {
                    dynamic folder = shell.Namespace(Path.GetDirectoryName(file));
                    dynamic item = folder.ParseName(Path.GetFileName(file));
                    var artists = item.ExtendedProperty("System.Music.Artist") is object[] names ? names.Cast<string>() : [];
                    tags.Add(new Tag((string?)item.ExtendedProperty("System.Title") ?? "", string.Join("; ", artists)));
                    Marshal.FinalReleaseComObject(item);
                    Marshal.FinalReleaseComObject(folder);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    /// <summary>A song's embedded cover, such as "500×500 baseline JPEG", or "no cover".</summary>
    private static string Cover(string file)
    {
        using var song = TagLib.File.Create(file);
        if (song.Tag.Pictures is not [var picture, ..]) return "no cover";
        var data = picture.Data.Data;
        using var image = System.Drawing.Image.FromStream(new MemoryStream(data));
        if (!image.RawFormat.Equals(System.Drawing.Imaging.ImageFormat.Jpeg)) return $"{image.Width}×{image.Height} {picture.MimeType}";
        // A JPEG's frame header says how it's encoded: SOF0 (FF C0) is baseline, SOF2 (FF C2) progressive. Entropy-coded
        // data never has FF followed by either.
        var kind = HasMarker(data, 0xC2) ? "progressive" : HasMarker(data, 0xC0) ? "baseline" : "other";
        return $"{image.Width}×{image.Height} {kind} JPEG";
    }

    private static bool HasMarker(byte[] data, byte marker)
    {
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == marker) return true;
        }
        return false;
    }

    /// <summary>A character from Unicode's private use area, where Segoe Fluent Icons keeps its glyphs.</summary>
    [GeneratedRegex(@"[\uE000-\uF8FF]")]
    private static partial Regex IconGlyph();

    [GeneratedRegex("Synced|Sync cancelled|Updated|Nothing was synced|skipped|couldn't", RegexOptions.IgnoreCase)]
    private static partial Regex SyncResult();
}
