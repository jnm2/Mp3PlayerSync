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
    public static Task<AppUnderTest> StartAsync(AutomationBase automation, HostLaunch launch, CancellationToken cancellationToken = default) =>
        ForProcessAsync(automation, launch.StartProcess(), launch.DataDirectory, cancellationToken: cancellationToken);

    /// <summary>
    /// Waits for the window of an app that was just started, which keeps its data in <paramref name="dataDirectory"/>.
    /// If it doesn't show one, the process is ended.
    /// </summary>
    public static async Task<AppUnderTest> ForProcessAsync(
        AutomationBase automation, Process process, string dataDirectory, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var window = await Wait.UntilAsync(
                () => process.HasExited
                    ? throw new InvalidOperationException($"The app exited with {process.ExitCode} before showing its window.")
                    : automation.GetDesktop().FindFirstChild(c => c.ByProcessId(process.Id).And(c.ByClassName(WindowClass))),
                "the app's window", timeout, cancellationToken: cancellationToken);
            return new AppUnderTest(automation, process, window, dataDirectory);
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

    /// <summary>The documents UI Automation reaches in the window's WebView2, which can include one the page just left.</summary>
    private List<AutomationElement> WebDocuments() =>
        [.. NativeWindows.Children(Window.Properties.NativeWindowHandle.Value, "InputSiteWindowClass")
            .SelectMany(inputSite => automation.FromHandle(inputSite).FindAllDescendants(c => c.ByControlType(ControlType.Document)))];

    /// <summary>Notes whether the window shows Exportify's own page now (see <see cref="SawExportifyPage"/>).</summary>
    public void Watch()
    {
        if (SawExportifyPage || Process.HasExited) return;
        try
        {
            if (!ShowsSignInView()) return;
            // UI Automation can still hold the document of a page the WebView just left, such as Exportify's when it goes on to
            // Spotify's sign-in on another site. So Exportify's page counts as shown only when it has a size and no other page
            // does, and the sign-in view is asked about again afterwards, since the app hides it as soon as the page starts
            // back to Exportify, which can happen between one question and the next.
            var shown = WebDocuments().Where(d => d.BoundingRectangle is { IsEmpty: false } && !d.Properties.IsOffscreen.ValueOrDefault).ToList();
            if (shown.Count == 0 || shown.Any(d => d.Properties.Name.ValueOrDefault != "Exportify (mock)") || !ShowsSignInView()) return;

            SawExportifyPage = true;
            // What showed, to tell a page on screen from one UI Automation only seems to show
            var documents = WebDocuments().Select(d => $"'{d.Properties.Name.ValueOrDefault}' at {d.BoundingRectangle}{(d.Properties.IsOffscreen.ValueOrDefault ? " off-screen" : "")}");
            Console.WriteLine($"  Exportify's own page in the sign-in view at {DateTime.Now:HH:mm:ss.fff}. The web pages: {string.Join(", ", documents)}. " +
                $"The window showed: {TryReadAllText()}");
            WindowPictures.SaveScreenshot(this, "exportify-page");
        }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.ElementNotAvailableException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // The window is closing or busy; the next look will tell.
        }
    }
    /// <summary>
    /// Whether the sign-in view shows, with its web page. UI Automation can still list a view that was just collapsed, with
    /// no size, so the title and the page must have one.
    /// </summary>
    private bool ShowsSignInView() =>
        FindById("SignInTitle") is { BoundingRectangle.IsEmpty: false }
        && Window.FindFirstDescendant(c => c.ByControlType(ControlType.Pane).And(c.ByName("Spotify sign-in"))) is { BoundingRectangle.IsEmpty: false };

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
