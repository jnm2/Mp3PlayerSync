using FlaUI.Core.Definitions;
using Mp3PlayerSync.EndToEnd;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

[assembly: DesktopGuard]

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// The harness itself: starting the test host against the mock, finding its window and the page inside its WebView2,
/// waiting for what it shows, and closing it.
/// </summary>
[Category(TestCategories.Smoke)]
[ClassDataSource<RunContext>(Shared = SharedType.PerClass)]
public sealed class SmokeTests(RunContext run)
{
    [Test]
    [Timeout(5 * 60_000)]
    public async Task The_test_host_shows_Spotify_sign_in_inside_its_window_then_closes_when_asked(CancellationToken cancellationToken)
    {
        // No removable drive can be assumed, so the player is a folder, as if chosen with "Other folder" last time.
        const string playerName = "E2E Player";
        run.WriteSettings(new { Player = new { FolderPath = run.CreateFolder(playerName) } });
        // The mock's automatic sign-in waits until the test has seen the sign-in page, which would otherwise be up for
        // only a moment.
        run.Mock.SignInHeld = true;
        var app = await run.StartAppAsync("?autoSignIn", cancellationToken);

        var players = await Wait.UntilAsync(() => app.FindById("PlayerGridView"), "the MP3 player page", showing: app, cancellationToken: cancellationToken);
        var player = await Wait.UntilAsync(
            () => players.FindFirstDescendant(c => c.ByControlType(ControlType.ListItem).And(c.ByName(playerName))),
            $"the '{playerName}' player", showing: app, cancellationToken: cancellationToken);
        player.Patterns.Invoke.Pattern.Invoke();

        await Wait.UntilAsync(() => app.FindById("SignInTitle"), "the sign-in screen", TimeSpan.FromSeconds(60), app, cancellationToken);
        var page = await Wait.UntilAsync(() => app.FindWebPage("Spotify login (mock)"), "Spotify's sign-in page in the window", TimeSpan.FromSeconds(60), app, cancellationToken);
        page.FindAllDescendants(c => c.ByControlType(ControlType.Hyperlink)).Select(link => link.Properties.Name.ValueOrDefault)
            .ShouldBe(["Sign up for Spotify", "Help"], ignoreOrder: true);

        run.Mock.SignInHeld = false;
        await Wait.UntilAsync(() => app.FindById("LibraryGridView"), "the library after signing in", TimeSpan.FromSeconds(60), app, cancellationToken);
        app.ShouldSatisfyAllConditions(
            () => app.Window.Properties.Name.ValueOrDefault.ShouldBe("MP3 Player Sync"),
            () => app.FindById("SignInTitle").ShouldBeNull(),
            () => app.FindWebPage("Spotify login (mock)").ShouldBeNull());

        await app.CloseAsync(cancellationToken);
        app.ShouldSatisfyAllConditions(
            () => app.WasKilled.ShouldBeFalse(),
            () => app.Process.ExitCode.ShouldBe(0));
    }
}
