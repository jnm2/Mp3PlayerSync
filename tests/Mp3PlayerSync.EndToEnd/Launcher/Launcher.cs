using System.Diagnostics;
using System.Text;
using Windows.Win32;

namespace Mp3PlayerSync.EndToEnd.Launcher;

/// <summary>
/// Runs the tests kept apart from whatever the user is doing: this same exe, in test mode, on a hidden desktop of its own
/// on the interactive window station (the tests, the mock server and the app all live on that desktop, which UI
/// Automation requires). Meanwhile it watches the user's foreground window, to confirm nothing the run started takes it
/// (TEST-1), then reads the results and ends anything the run left behind.
/// </summary>
internal static class Launcher
{
    /// <summary>
    /// Set for the tests' process: the desktop it was started on, or <c>Current</c>. Also what makes the exe run the tests.
    /// </summary>
    public const string DesktopVariable = "MP3PLAYERSYNC_E2E_DESKTOP";

    /// <summary>Set for the tests' process: the queue of runs going at the same time (see <see cref="SlotQueue"/>).</summary>
    public const string QueueVariable = "MP3PLAYERSYNC_E2E_QUEUE";

    public const string PassedText = "All end-to-end checks passed";

    public static int Run(string[] args)
    {
        if (args is ["--help" or "-h" or "-?"])
        {
            Console.WriteLine(string.Format(LauncherOptions.Usage, string.Join(", ", TestCategories.All)));
            return 0;
        }

        LauncherOptions options;
        try
        {
            options = LauncherOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(string.Format(LauncherOptions.Usage, string.Join(", ", TestCategories.All)));
            return 1;
        }

        string appPath;
        try { appPath = options.AppPath ?? Locations.AppPath; }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        using var slot = SlotQueue.Enter(Locations.RepositoryRoot ?? AppContext.BaseDirectory, string.Join(',', options.Only));
        var desktopName = options.HiddenDesktop ? $"Mp3PlayerSyncTests-{Environment.ProcessId}" : null;
        var trx = $"e2e-{Environment.ProcessId}.trx";
        Directory.CreateDirectory(options.ResultsDirectory);
        File.Delete(Path.Combine(options.ResultsDirectory, trx));

        // The tests' process inherits these.
        Environment.SetEnvironmentVariable(DesktopVariable, desktopName ?? "Current");
        Environment.SetEnvironmentVariable(QueueVariable, SlotQueue.QueueFolder);
        Environment.SetEnvironmentVariable(Locations.AppVariable, appPath);

        var self = Path.Combine(AppContext.BaseDirectory, "Mp3PlayerSync.EndToEnd.exe");
        var commandLine = new StringBuilder($"\"{self}\" --no-ansi --progress off --output Detailed --report-trx --report-trx-filename {trx} --results-directory \"{options.ResultsDirectory}\"");
        if (options.TreeNodeFilter is { } filter) commandLine.Append($" --treenode-filter \"{filter}\"");
        foreach (var argument in options.TestArguments) commandLine.Append($" \"{argument.Replace("\"", "\\\"")}\"");

        using var tests = TestProcess.Start(self, commandLine.ToString(), desktopName);
        Console.WriteLine($"Running the end-to-end tests on {(desktopName is null ? "this desktop" : $"desktop '{desktopName}'")} (process {tests.ProcessId}), with {appPath}");
        var echo = Task.Run(() => tests.Output.CopyTo(Console.OpenStandardOutput()));

        var foreground = new ForegroundWatch(tests, hiddenDesktop: desktopName is not null);
        var deadline = Stopwatch.StartNew();
        var timedOut = false;
        while (!tests.WaitForExit(TimeSpan.FromMilliseconds(100)))
        {
            foreground.Check();
            if (deadline.Elapsed > options.Timeout)
            {
                Console.WriteLine($"Timed out after {options.Timeout.TotalMinutes} minutes");
                timedOut = true;
                break;
            }
        }

        var exitCode = tests.ExitCode;
        if (tests.RunningProcesses > 0)
        {
            Console.WriteLine($"Ending {tests.RunningProcesses} process(es) the run left running");
            tests.Terminate();
        }
        echo.Wait(TimeSpan.FromSeconds(10));
        Console.Out.Flush();

        foreground.Report();
        return Verdict(options, Path.Combine(options.ResultsDirectory, trx), exitCode, timedOut, foreground.Stolen.Count > 0);
    }

    private static int Verdict(LauncherOptions options, string trxPath, int? exitCode, bool timedOut, bool tookForeground)
    {
        var outcomes = File.Exists(trxPath) ? TestOutcomes.Read(trxPath).Results : [];
        var failed = outcomes.Where(o => !o.Passed && !o.Inconclusive).ToList();
        var inconclusive = outcomes.Where(o => o.Inconclusive).ToList();
        Console.WriteLine();
        foreach (var outcome in failed) Console.WriteLine($"  {outcome.Outcome.ToUpperInvariant()}  {outcome.Name}: {outcome.Message}");
        foreach (var outcome in inconclusive) Console.WriteLine($"  INCONCLUSIVE  {outcome.Name}: {outcome.Message}");

        // The tests' exit code and their results have to agree that everything passed, so neither can hide a failure.
        var problems = new List<string>();
        if (timedOut) problems.Add("the tests timed out");
        if (tookForeground) problems.Add("a process of the run took the foreground");
        if (exitCode is not 0 && !timedOut) problems.Add($"the tests exited with {exitCode?.ToString() ?? "no exit code"}");
        if (failed.Count > 0) problems.Add($"{failed.Count} test(s) failed or didn't run");
        if (outcomes.Count == 0) problems.Add($"no test ran{(options.Only.Count > 0 ? $" for --only {string.Join(',', options.Only)}" : "")}");

        if (problems.Count > 0)
        {
            Console.WriteLine($"Failed: {string.Join("; ", problems)}");
            return 1;
        }
        if (inconclusive.Count > 0)
        {
            Console.WriteLine($"Inconclusive: nothing failed, but {inconclusive.Count} test(s) could neither pass nor fail (see above)");
            return 2;
        }
        Console.WriteLine($"{PassedText} ({outcomes.Count} test(s))");
        return 0;
    }

    /// <summary>
    /// Watches the user's desktop: its foreground window may change because of the user (who may well be running the app
    /// themselves), but never to a process of the run.
    /// </summary>
    private sealed class ForegroundWatch(TestProcess tests, bool hiddenDesktop)
    {
        private readonly List<string> changes = [];
        private Windows.Win32.Foundation.HWND last;

        public List<string> Stolen { get; } = [];

        public unsafe void Check()
        {
            var window = PInvoke.GetForegroundWindow();
            if (window == last) return;
            last = window;
            uint owner;
            PInvoke.GetWindowThreadProcessId(window, &owner);
            var name = "?";
            try { using var process = Process.GetProcessById((int)owner); name = process.ProcessName; }
            catch (ArgumentException) { }
            changes.Add($"{DateTime.Now:HH:mm:ss.fff} {name} ({owner})");
            // On this desktop (CI, or someone asking for it) Windows can give a newly started app the foreground, and
            // there's nobody's work to protect.
            if (hiddenDesktop && owner != 0 && tests.Contains((int)owner)) Stolen.Add($"{name} ({owner})");
        }

        public void Report()
        {
            Console.WriteLine("Foreground windows on the user's desktop during the run:");
            foreach (var change in changes) Console.WriteLine($"  {change}");
            if (!hiddenDesktop) return;
            Console.WriteLine(Stolen.Count == 0
                ? "  PASS  The app never took the foreground"
                : $"  FAIL  The app never took the foreground: {string.Join(", ", Stolen)}");
        }
    }
}
