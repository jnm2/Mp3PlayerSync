using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// Starting the app again: only one copy runs per data folder (APP-1), it reopens what was open without signing in
/// again, and it remembers the library tab and the player, which can be changed, also to a folder (PLAYER-6).
/// </summary>
[Category(TestCategories.Restart)]
public sealed class RestartTests
{
    private const string RoadTrip = "Spotify playlist: Road Trip, Vol. 2";

    [ClassDataSource<RunContext>(Shared = SharedType.None)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(5 * 60_000)]
    public async Task A_second_launch_on_the_same_data_folder_hands_over_but_one_on_another_runs_alongside(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        Run.SetUpPlayer();
        var app = await Run.StartAppAsync(cancellationToken: cancellationToken);

        // Only one copy per data folder: launching again hands over to the running copy and exits.
        using (var second = Run.Launch().StartProcess())
        {
            try
            {
                await second.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30) * Wait.Scale, cancellationToken);
            }
            catch (TimeoutException)
            {
                second.Kill();
            }
            var exitCode = second.HasExited ? second.ExitCode : (int?)null;
            checks.Check("A second launch on the same data folder exits by itself", () => exitCode.ShouldBe(0));
        }
        checks.Check("...and the first copy stays open", () => app.ShouldSatisfyAllConditions(
            () => app.Process.HasExited.ShouldBeFalse(),
            () => Run.Automation.GetDesktop().FindFirstChild(c => c.ByProcessId(app.Process.Id)).ShouldNotBeNull()));

        // A different data folder, like another test run's, is a separate copy.
        var other = await Run.StartAppAsync(Run.Launch() with { DataDirectory = Run.CreateFolder("Another data folder") }, cancellationToken);
        checks.Check("A launch on another data folder runs alongside", () => other.Process.HasExited.ShouldBeFalse());

        await other.CloseAsync(cancellationToken);
        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Restarting_while_signed_in_reopens_the_last_playlist_then_the_player_can_be_changed(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Road Trip, Vol. 2", cancellationToken);
        var app = await Run.StartAppAsync(cancellationToken: cancellationToken);
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName);
        var sawSignIn = false;
        await app.UntilAsync(() =>
        {
            if (app.FindById("SignInTitle") is not null) sawSignIn = true;
            return app.Text("PlaylistTitle") == RoadTrip;
        }, "the last opened playlist to reopen", 60);
        await app.WaitContentsAsync(checks, "Road Trip, Vol. 2 (restored)", "250 tracks, *");
        checks.Check("Sign-in screen is never shown when already signed in", () => sawSignIn.ShouldBeFalse());

        await app.ClickAsync("BackButton");
        await app.WaitLibraryAsync("64 playlists and albums");
        checks.Passed("Selector choice (All) was remembered");
        await CheckLibraryShowsThePlayerAsync(app, checks, "Library shows the chosen folder");
        checks.Check("Library banner: 'Select a playlist to sync'", () => app.Text("LibraryTitle").ShouldBe("Select a playlist to sync"));

        await app.ClickAsync("ChangePlayerButton");
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName);
        await app.WaitLibraryAsync("64 playlists and albums");
        checks.Passed("Changing the MP3 player returns to the library");

        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task Other_folder_opens_the_folder_dialog_in_the_players_folder_where_Select_Folder_picks_it(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Road Trip, Vol. 2", cancellationToken);
        var app = await Run.StartAppAsync(cancellationToken: cancellationToken);
        await app.ChoosePlayerAsync(checks, RunContext.PlayerName);
        await app.UntilAsync(() => app.Text("PlaylistTitle") == RoadTrip, "the last opened playlist to reopen", 60);
        await app.ClickAsync("BackButton");
        await app.WaitLibraryAsync("64 playlists and albums");

        await app.ClickAsync("ChangePlayerButton");
        var grid = await app.UntilAsync(() => app.FindById("PlayerGridView"), "the MP3 player page");
        var tile = await app.UntilAsync(() => grid.FindListItem("Other folder"), "the 'Other folder' tile", 10);
        // The dialog is modal, so the tile is invoked from another thread in case Invoke waits for it.
        var invoke = Task.Run(tile.Invoke, cancellationToken);
        // Found through Win32, and without watching the app: UI Automation's searches can time out while the app
        // waits on the dialog.
        AutomationElement dialog;
        try
        {
            dialog = await Wait.UntilAsync(
                () => NativeWindows.ProcessWindow(app.Process.Id, "#32770") is not 0 and var window ? Run.Automation.FromHandle(window) : null,
                "the folder dialog", TimeSpan.FromSeconds(20), cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            if (invoke.Exception is { } exception) Console.WriteLine($"  Invoking 'Other folder': {exception.InnerException}");
            throw;
        }

        var selectFolder = await Wait.UntilAsync(
            () => dialog.FindRaw(e => e.Properties.ClassName.ValueOrDefault == "Button" && e.Name() == "Select Folder"),
            "the dialog's Select Folder button", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
        var address = await Wait.UntilAsync(
            () => dialog.FindRaw(e => AppSteps.Like(e.Name(), "Address: *"))?.Name(),
            "the dialog's address bar", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
        checks.Check("[PLAYER-6] The folder dialog opens in the player's folder", () => address.ShouldBe($"Address: {Run.PlayerFolder}"));
        var items = await Wait.UntilAsync(
            () => dialog.FindRaw(e => e.Name() == "Items View"),
            "the dialog's folder contents", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
        checks.Check("[PLAYER-6] Nothing is selected in the folder dialog", () => items.Patterns.Selection.Pattern.Selection.Value.ShouldBeEmpty());
        checks.Check("[PLAYER-6] Select Folder is enabled with nothing selected", () => selectFolder.IsEnabled.ShouldBeTrue());

        NativeWindows.Click(selectFolder.Properties.NativeWindowHandle.Value);
        await app.WaitLibraryAsync("64 playlists and albums");
        // Whether Invoke returned or gave up waiting for the dialog doesn't matter: the dialog came.
        await Task.WhenAny(invoke, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
        var lastPicked = JsonNode.Parse(File.ReadAllText(Run.SettingsFile))?["lastPickedFolder"]?.GetValue<string>();
        checks.Check("[PLAYER-6] Select Folder picked the folder shown", () => lastPicked.ShouldBe(Run.PlayerFolder));
        await CheckLibraryShowsThePlayerAsync(app, checks, "[PLAYER-6] Select Folder picked the folder shown, and the library shows it");

        await app.OpenLibraryItemAsync("Big Album");
        await app.WaitContentsAsync(checks, "Big Album", "*60 tracks, *");
        await app.CloseAsync(cancellationToken);
        Run.Finish(checks);
    }

    private async Task CheckLibraryShowsThePlayerAsync(AppUnderTest app, Checks checks, string description)
    {
        // The title can still be being filled in just after going back to the library.
        var expected = $"Player: Folder ({Run.PlayerFolder})";
        await app.SoonAsync(() => app.Text("SelectedPlayerTitle") == expected);
        checks.Check(description, () => app.Text("SelectedPlayerTitle").ShouldBe(expected));
    }
}
