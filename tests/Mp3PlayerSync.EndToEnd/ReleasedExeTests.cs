using System.Diagnostics;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// The exe exactly as released (the publish job's <c>MP3-Player-Sync-&lt;arch&gt;.exe</c>, given with <c>--app</c>), started
/// as a user would start it on a clean PC: its first launch extracts the bundle, then it shows its window and the player
/// page, fetches its tools into <c>%LOCALAPPDATA%\Mp3PlayerSync</c>, hands a second launch over (APP-1), and closes with
/// exit code 0 when asked. It uses the real data folder and takes the foreground, so it runs only on CI's VMs, never on
/// a PC someone uses (TEST-1): it's explicit, and it skips itself unless <c>CI</c> is set, as GitHub Actions sets it.
/// </summary>
[Category(TestCategories.ReleasedExe)]
[Explicit]
public sealed class ReleasedExeTests
{
    [ClassDataSource<RunContext>(Shared = SharedType.PerClass)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task The_released_exe_starts_fetches_its_tools_hands_a_second_launch_over_then_closes(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true")
            Skip.Test("The released exe runs only on CI's VMs: it uses the real data folder in %LOCALAPPDATA% and takes the foreground.");

        var checks = new Checks();
        var exe = Locations.AppPath;
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mp3PlayerSync");
        var started = Stopwatch.StartNew();
        // Its first launch extracts the whole bundle before it shows anything.
        await using var app = await AppUnderTest.ForProcessAsync(
            Run.Automation, Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!, dataDirectory, TimeSpan.FromMinutes(2), cancellationToken);
        Console.WriteLine($"  The window appeared {started.Elapsed.TotalSeconds:0.0} s after the first launch");

        // The title can reach UI Automation a moment after the window does, so wait for it rather than read it once.
        await app.SoonAsync(() => app.Window.Name() == "MP3 Player Sync", 10);
        checks.Check("[TEXT-3] The window is called 'MP3 Player Sync'", () => app.Window.Name().ShouldBe("MP3 Player Sync"));
        await app.UntilAsync(() => app.FindById("PlayerTitle"), "the player page", 60);
        checks.Check("MP3 player page comes first", () => app.Text("PlayerTitle").ShouldBe("Select an MP3 player to sync"));
        WindowPictures.SaveScreenshot(app, "player");

        // From GitHub, at startup
        var tools = Path.Combine(dataDirectory, "Tools");
        var ytDlp = await app.UntilAsync(() => AppSteps.Tool(tools, "yt-dlp-win", "yt-dlp*.exe"), "yt-dlp in the data folder", 180);
        var deno = await app.UntilAsync(() => AppSteps.Tool(tools, "deno", "deno.exe"), "Deno in the data folder", 180);
        checks.Passed($"The app fetched {Path.GetRelativePath(dataDirectory, ytDlp)} and {Path.GetRelativePath(dataDirectory, deno)}");

        // APP-1: only one copy runs per data folder, so launching again hands over to the running copy and exits.
        int? secondExitCode;
        using (var second = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!)
        {
            try
            {
                await second.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30) * Wait.Scale, cancellationToken);
            }
            catch (TimeoutException)
            {
                second.Kill();
            }
            secondExitCode = second.HasExited ? second.ExitCode : null;
        }
        checks.Check("A second launch on the same data folder exits by itself", () => secondExitCode.ShouldBe(0));
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        var copies = running.Select(p => p.Id).ToList();
        foreach (var process in running) process.Dispose();
        checks.Check("...and the first copy stays open, the only one", () => copies.ShouldBe([app.Process.Id]));

        await app.CloseAsync(cancellationToken);
        checks.Check("The app closes when asked, with exit code 0", () => app.ShouldSatisfyAllConditions(
            () => app.WasKilled.ShouldBeFalse(),
            () => app.Process.ExitCode.ShouldBe(0)));
        checks.Finish();
    }
}
