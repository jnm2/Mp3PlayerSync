using System.Diagnostics;
using System.Management;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// The test host, started off-screen and never activated, and its window as UI Automation sees it.
/// </summary>
public sealed class AppUnderTest : IAsyncDisposable
{
    /// <summary>The class of the app's own window: the process can have other top-level windows, without content.</summary>
    private const string WindowClass = "WinUIDesktopWin32WindowClass";

    private readonly AutomationBase automation;

    private AppUnderTest(AutomationBase automation, Process process, AutomationElement window, string dataDirectory)
    {
        this.automation = automation;
        DataDirectory = dataDirectory;
        Process = process;
        Window = window;
    }

    public Process Process { get; }

    /// <summary>The app's data folder.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Whether the window ever showed Exportify's own page, which it may never do (SIGNIN-1, SIGNIN-2): the embedded page
    /// is only shown while it's Spotify's sign-in. Every wait on the app watches for it (see <see cref="Watch"/>).
    /// </summary>
    public bool SawExportifyPage { get; private set; }

    /// <summary>The app's own window.</summary>
    public AutomationElement Window { get; }

    /// <summary>Whether <see cref="CloseAsync"/> had to end the app because it didn't close when asked.</summary>
    public bool WasKilled { get; private set; }

    /// <summary>Starts the test host and waits for its window.</summary>
    public static async Task<AppUnderTest> StartAsync(AutomationBase automation, HostLaunch launch, CancellationToken cancellationToken = default)
    {
        var process = launch.StartProcess();
        try
        {
            var window = await Wait.UntilAsync(
                () => process.HasExited
                    ? throw new InvalidOperationException($"The app exited with {process.ExitCode} before showing its window.")
                    : automation.GetDesktop().FindFirstChild(c => c.ByProcessId(process.Id).And(c.ByClassName(WindowClass))),
                "the app's window", cancellationToken: cancellationToken);
            return new AppUnderTest(automation, process, window, launch.DataDirectory);
        }
        catch (TimeoutException exception)
        {
            // Such as a message box from the Windows App SDK's bootstrapper, when its runtime isn't installed
            var others = automation.GetDesktop().FindAllChildren(c => c.ByProcessId(process.Id))
                .Select(w => $"'{w.Properties.Name.ValueOrDefault}' ({w.Properties.ClassName.ValueOrDefault})").ToList();
            if (!process.HasExited) process.Kill();
            process.Dispose();
            throw new TimeoutException($"{exception.Message}. The app's other windows: {(others.Count > 0 ? string.Join(", ", others) : "none")}", exception);
        }
        catch
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
            throw;
        }
    }

    public AutomationElement? FindById(string automationId) => Window.FindFirstDescendant(c => c.ByAutomationId(automationId));

    /// <summary>The text of the element with <paramref name="automationId"/>, or null when it isn't shown.</summary>
    public string? Text(string automationId) => FindById(automationId)?.Properties.Name.ValueOrDefault;

    /// <summary>Every text in the window, a line each.</summary>
    public string AllText() =>
        string.Join('\n', Window.FindAllDescendants(c => c.ByControlType(ControlType.Text)).Select(e => e.Properties.Name.ValueOrDefault));

    /// <summary>
    /// The page called <paramref name="title"/> in the window's WebView2, or null when it isn't shown. UI Automation
    /// reaches the page through the window's input site windows rather than the XAML tree.
    /// </summary>
    public AutomationElement? FindWebPage(string title)
    {
        foreach (var inputSite in NativeWindows.Children(Window.Properties.NativeWindowHandle.Value, "InputSiteWindowClass"))
        {
            if (automation.FromHandle(inputSite).FindFirstDescendant(c => c.ByControlType(ControlType.Document).And(c.ByName(title))) is { } page)
                return page;
        }
        return null;
    }

    /// <summary>Notes whether the window shows Exportify's own page now (see <see cref="SawExportifyPage"/>).</summary>
    public void Watch()
    {
        if (SawExportifyPage || Process.HasExited) return;
        try
        {
            // The sign-in view is asked about again after the page is found: the page is found through the WebView's window
            // even while the view is hidden, and the app hides the view as soon as the page starts back to Exportify, which can
            // happen between one question and the next.
            if (ShowsSignInView() && FindWebPage("Exportify (mock)") is not null && ShowsSignInView())
                SawExportifyPage = true;
        }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.ElementNotAvailableException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // The window is closing or busy; the next look will tell.
        }
    }

    private bool ShowsSignInView() =>
        FindById("SignInTitle") is not null
        && Window.FindFirstDescendant(c => c.ByControlType(ControlType.Pane).And(c.ByName("Spotify sign-in"))) is not null;

    /// <summary>What the window shows, for a message, or null if it can't be read.</summary>
    internal string? TryReadAllText()
    {
        try
        {
            var text = string.Join(' ', AllText().Split((char[])['\n', ' '], StringSplitOptions.RemoveEmptyEntries));
            return text.Length > 500 ? text[..500] + "…" : text;
        }
        catch (Exception exception)
        {
            return $"(couldn't read the window: {exception.Message})";
        }
    }

    /// <summary>
    /// Closes the window as the user would, waits for the app to exit (ending it if it doesn't), then for its WebView2
    /// processes, which outlive it: the next launch on the same data folder could otherwise hand over to the closing copy
    /// or find its WebView2 profile still in use.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(5) * Wait.Scale;
        if (!Process.HasExited)
        {
            // To the app's own window: Process.CloseMainWindow's main window can be a helper window.
            PInvoke.PostMessage((HWND)Window.Properties.NativeWindowHandle.Value, PInvoke.WM_CLOSE, default, default);
            using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closed.CancelAfter(timeout);
            try
            {
                await Process.WaitForExitAsync(closed.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"  The app didn't close within {timeout.TotalSeconds:0.#} s; ending it");
                WasKilled = true;
                Process.Kill();
                await Process.WaitForExitAsync(cancellationToken);
            }
        }

        var webViewData = Path.Combine(DataDirectory, "WebView2") + Path.DirectorySeparatorChar;
        try
        {
            await Wait.UntilAsync(() => WebViewProcesses(webViewData).Count == 0, "the app's WebView2 processes to exit", timeout / Wait.Scale, cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            var left = WebViewProcesses(webViewData);
            Console.WriteLine($"  The app's WebView2 processes were still running {timeout.TotalSeconds:0.#} s after it closed; ending them");
            foreach (var id in left)
            {
                try { using var process = Process.GetProcessById(id); process.Kill(); }
                catch (ArgumentException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(); }
        finally { Process.Dispose(); }
    }

    /// <summary>The WebView2 processes that use the app's data folder: only this run's, never the user's own.</summary>
    private static List<int> WebViewProcesses(string webViewData)
    {
        using var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");
        using var found = search.Get();
        return found.Cast<ManagementObject>()
            .Where(p => p["CommandLine"] is string commandLine && commandLine.Contains(webViewData, StringComparison.OrdinalIgnoreCase))
            .Select(p => (int)(uint)p["ProcessId"])
            .ToList();
    }
}
