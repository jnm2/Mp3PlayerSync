using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.RemoteDesktop;
using Windows.Win32.Storage.Xps;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// A picture of the app's window, with where its top-left pixel is on the screen, so UI Automation's rectangles (in
/// screen pixels) can be found in it.
/// </summary>
public sealed class WindowPicture(Bitmap bitmap, int left, int top) : IDisposable
{
    public Bitmap Bitmap { get; } = bitmap;

    public int Left { get; } = left;

    public int Top { get; } = top;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Pictures of the app's window: for screenshots, and for checks of the pixels the app draws.</summary>
public static unsafe class WindowPictures
{
    /// <summary>Set by the launcher's --screenshots: where screenshots go. Without it, none are taken.</summary>
    public const string FolderVariable = "MP3PLAYERSYNC_E2E_SCREENSHOTS";

    /// <summary>
    /// The window as Windows composes it (PrintWindow), title bar and web pages included, or null when it isn't laid out
    /// yet. While the PC is locked, Windows gives a blank picture, or the window as it was before (say, a page ago).
    /// </summary>
    public static WindowPicture? Capture(AppUnderTest app)
    {
        var hwnd = (HWND)app.Window.Properties.NativeWindowHandle.Value;
        if (!PInvoke.GetWindowRect(hwnd, out var rect) || rect.right <= rect.left || rect.bottom <= rect.top) return null;
        var bitmap = new Bitmap(rect.right - rect.left, rect.bottom - rect.top);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try { PInvoke.PrintWindow(hwnd, (HDC)hdc, (PRINT_WINDOW_FLAGS)2); } // PW_RENDERFULLCONTENT
            finally { graphics.ReleaseHdc(hdc); }
        }
        return new WindowPicture(bitmap, rect.left, rect.top);
    }

    /// <summary>
    /// The window's content as the app's XAML draws it, through the test host (WindowCapture there), placed in screen
    /// pixels like UI Automation's rectangles. Unlike <see cref="Capture"/>, it works while the PC is locked, but it leaves
    /// out the title bar, backdrop and web pages. Without a picture, <paramref name="why"/> says why.
    /// </summary>
    public static WindowPicture? Render(AppUnderTest app, out string why)
    {
        var request = Path.Combine(app.DataDirectory, "Capture.request");
        var png = Path.Combine(app.DataDirectory, "Capture.png");
        // One left over from an app that has since closed would never be noticed: the host waits for one to be created.
        foreach (var file in (string[])[request, png, png + ".error"]) File.Delete(file);
        File.WriteAllText(request, "");
        var timeout = TimeSpan.FromSeconds(10) * Wait.Scale;
        var deadline = DateTime.UtcNow + timeout;
        while (File.Exists(request) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        if (File.Exists(request))
        {
            File.Delete(request);
            why = $"the app didn't draw the window within {timeout.TotalSeconds:0.#} s";
            return null;
        }
        if (File.Exists(png + ".error"))
        {
            why = $"the app couldn't draw the window: {File.ReadLines(png + ".error").FirstOrDefault()}";
            return null;
        }
        if (!File.Exists(png))
        {
            why = "the app had no window content to draw";
            return null;
        }

        Bitmap bitmap;
        using (var file = new Bitmap(png)) bitmap = new Bitmap(file); // A copy, so the file isn't held open
        if (IsBlank(bitmap))
        {
            bitmap.Dispose();
            why = "the app's drawing of the window was blank";
            return null;
        }
        var origin = new Point();
        PInvoke.ClientToScreen((HWND)app.Window.Properties.NativeWindowHandle.Value, ref origin);
        why = "";
        return new WindowPicture(bitmap, origin.X, origin.Y);
    }

    /// <summary>
    /// Saves a screenshot called <paramref name="name"/> in the launcher's --screenshots folder, if it was given one, and
    /// attaches it to the test.
    /// </summary>
    public static void SaveScreenshot(AppUnderTest app, string name)
    {
        if (Environment.GetEnvironmentVariable(FolderVariable) is not { Length: > 0 } folder) return;
        var test = TestContext.Current?.Metadata.TestDetails is { } details ? $"{details.ClassType.Name}.{details.MethodName}" : "Test";
        var path = Path.Combine(folder, $"{test}-{name}.png");
        try
        {
            var why = IsLocked() ? "the PC is locked, when Windows gives stale or blank pictures" : null;
            var picture = why is null ? Capture(app) : null;
            if (why is null && picture is null)
            {
                Console.WriteLine($"  No screenshot of {name} (window not laid out)");
                return;
            }
            if (picture is not null && IsBlank(picture.Bitmap))
            {
                picture.Dispose();
                picture = null;
                why = "Windows gave a blank picture";
            }
            // The app's own drawing is the next best thing.
            var renderWhy = "";
            picture ??= Render(app, out renderWhy);
            if (picture is null)
            {
                Console.WriteLine($"  No screenshot of {name} ({why}, and {renderWhy})");
                return;
            }
            using (picture)
            {
                Directory.CreateDirectory(folder);
                picture.Bitmap.Save(path, ImageFormat.Png);
            }
            Console.WriteLine($"  Screenshot: {path}{(why is null ? "" : $" (as the app draws it, without the title bar, backdrop or web pages: {why})")}");
            TestContext.Current?.Output.AttachArtifact(path);
        }
        catch (Exception exception) when (exception is ExternalException or IOException or ArgumentException)
        {
            Console.WriteLine($"  No screenshot of {name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether a picture shows nothing, as Windows sometimes gives PrintWindow while the PC is locked: one color, perhaps
    /// with a border.
    /// </summary>
    public static bool IsBlank(Bitmap bitmap)
    {
        var colors = new HashSet<int>();
        for (var x = 0; x < bitmap.Width; x += 8)
        {
            for (var y = 0; y < bitmap.Height; y += 8)
            {
                if (colors.Add(bitmap.GetPixel(x, y).ToArgb()) && colors.Count > 2) return false;
            }
        }
        return true;
    }

    /// <summary>Whether this session is locked, when PrintWindow gets stale or blank pictures.</summary>
    public static bool IsLocked()
    {
        // WTSSessionInfoEx for this session: its SessionFlags is WTS_SESSIONSTATE_LOCK (0) while locked.
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, PInvoke.WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSSessionInfoEx, out var buffer, out _))
            return false;
        try { return ((WTSINFOEXW*)buffer.Value)->Data.WTSInfoExLevel1.SessionFlags == 0; }
        finally { PInvoke.WTSFreeMemory(buffer); }
    }
}
