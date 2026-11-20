using System.Diagnostics;
using Mp3PlayerSync.EndToEnd.Launcher;
using TUnit.Core.Interfaces;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Runs a test that an IDE's Test Explorer runs without debugging on a hidden desktop instead, through the launcher, so
/// the app doesn't cover the user's screen (TEST-3). The test's outcome, and what the launcher printed, come back as this
/// test's. A test that's being debugged, or that the launcher started, runs here as usual.
/// </summary>
public sealed class HiddenDesktopForIdeRunsExecutor : ITestExecutor
{
    /// <summary>Whether this process hands its tests to the launcher rather than running them itself.</summary>
    public static bool RunsTestsElsewhere =>
        TestRunEnvironment.StartedByIde && !Debugger.IsAttached && !TestRunEnvironment.OnLauncherDesktop;

    public async ValueTask ExecuteTest(TestContext context, Func<ValueTask> action)
    {
        if (!RunsTestsElsewhere)
        {
            await action();
            return;
        }

        var details = context.Metadata.TestDetails;
        var results = Path.Combine(Path.GetTempPath(), $"Mp3PlayerSyncIdeRun-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Mp3PlayerSync.EndToEnd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])["--results-directory", results, "--", "--treenode-filter", $"/*/{details.ClassType.Namespace}/{details.ClassType.Name}/{details.MethodName}"])
            start.ArgumentList.Add(argument);

        try
        {
            using var launcher = Process.Start(start) ?? throw new InvalidOperationException("The launcher didn't start.");
            var output = context.Output.StandardOutput;
            launcher.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.WriteLine(e.Data); };
            launcher.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.WriteLine(e.Data); };
            launcher.BeginOutputReadLine();
            launcher.BeginErrorReadLine();
            try
            {
                await launcher.WaitForExitAsync(context.Execution.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Ending the launcher closes its job, which ends the tests' process and the app with it.
                launcher.Kill(entireProcessTree: true);
                throw;
            }

            var outcome = Directory.Exists(results)
                ? Directory.EnumerateFiles(results, "*.trx").Select(TestOutcomes.Read).SelectMany(o => o.Results).SingleOrDefault()
                : null;
            switch (launcher.ExitCode, outcome)
            {
                case (0, { Passed: true }):
                    return;
                case (2, { Inconclusive: true } inconclusive):
                    Skip.Test(inconclusive.Message?.Replace("Skipped: ", "") ?? TestOutcome.InconclusivePrefix);
                    return;
                default:
                    throw new HiddenDesktopRunException(
                        $"On the hidden desktop the test {(outcome is null ? "didn't run" : $"ended as {outcome.Outcome}")} (the launcher exited with {launcher.ExitCode}){(outcome?.Message is { Length: > 0 } message ? $": {message}" : "")}");
            }
        }
        finally
        {
            try { Directory.Delete(results, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}

public sealed class HiddenDesktopRunException(string message) : Exception(message);
