using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Mp3PlayerSync.EndToEnd.Harness;
using Mp3PlayerSync.MockExportify;
using Shouldly;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// Regenerates the README's screenshots in docs\images from the mock Exportify's made-up demo library
/// (tests\Mp3PlayerSync.MockExportify\DemoLibrary.json), with a fake "MP3 PLAYER (E:)" drive backed by a folder and the
/// fake YouTube Music, so no real Spotify account, library, drive or folder path can appear in the pictures. It's
/// explicit, so it runs only when asked for with <c>--only ReadmeScreenshots</c>, never as part of the other runs. Keep
/// the PC unlocked, with its display on, while it runs: otherwise windows capture as blank, and then nothing is saved.
/// </summary>
[Category(TestCategories.ReadmeScreenshots)]
[Explicit]
public sealed class ReadmeScreenshots
{
    // The window's size in device-independent pixels
    private const int Width = 1100;
    private const int Height = 760;
    private const string RoadTrip = "roadtrip";

    [ClassDataSource<RunContext>(Shared = SharedType.PerClass)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(5 * 60_000)]
    public async Task Take_the_READMEs_screenshots(CancellationToken cancellationToken)
    {
        var output = Path.Combine(Locations.RepositoryRoot ?? throw new InvalidOperationException("The repository isn't where this was built."), "docs", "images");
        var staging = Run.CreateFolder("Screenshots");
        await using var demo = await MockExportifyServer.StartDemoAsync(Console.WriteLine);

        // The player: a USB MP3 player and a memory card, each a folder standing in for a drive
        var playerFolder = Run.CreateFolder("MP3 PLAYER");
        var cardFolder = Run.CreateFolder("Memory card");
        const long gigabyte = 1L << 30;
        var drives = JsonSerializer.Serialize(new object[]
        {
            new { root = @"E:\", label = "MP3 PLAYER", bus = "Usb", totalBytes = 16 * gigabyte, freeBytes = (long)(9.4 * gigabyte), folder = playerFolder },
            new { root = @"F:\", label = "", bus = "SecureDigital", totalBytes = 32 * gigabyte, freeBytes = (long)(31.2 * gigabyte), folder = cardFolder },
        });
        Run.WriteSettings(new { Player = new { VolumeId = @"\\?\Volume{test-E}\", Label = "MP3 PLAYER" } });

        // Synced before: the playlist's songs up to those added since
        var playlist = demo.Playlist(RoadTrip);
        var playlistFolder = Directory.CreateDirectory(Path.Combine(playerFolder, playlist.Name)).FullName;
        for (var i = 0; i < playlist.Total - playlist.NewSinceLastSync; i++)
        {
            var track = demo.Track(RoadTrip, i);
            // Placeholder bytes: with no tags to read, the app goes by the file name.
            File.WriteAllBytes(Path.Combine(playlistFolder, $"{i + 1:00}. {track.Artist} - {track.Title}.m4a"), [.. Enumerable.Range(1, 64).Select(b => (byte)b)]);
        }

        var app = await Run.StartAppAsync(Run.Launch("?autoSignIn", demo) with { TestDrives = drives }, cancellationToken);
        var window = (HWND)app.Window.Properties.NativeWindowHandle.Value;
        // The app needs Windows 11, so it always has the window's DPI.
        var dpiScale = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ? PInvoke.GetDpiForWindow(window) / 96.0 : 1;
        PInvoke.SetWindowPos(window, HWND.Null, 0, 0, (int)(Width * dpiScale), (int)(Height * dpiScale),
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        // 1. Pick your player
        var players = await app.UntilAsync(() => app.FindById("PlayerGridView"), "the player page");
        var tile = await app.UntilAsync(
            () => players.FindListItem("MP3 PLAYER") is { } t && t.Patterns.SelectionItem.Pattern.IsSelected.Value ? t : null, "MP3 PLAYER, pre-selected");
        await SaveAsync(app, staging, "step-1-player");
        tile.Invoke();

        // 2. Pick a playlist (the mock signs in by itself)
        await app.UntilAsync(() => AppSteps.Like(app.Text("LibrarySummaryText"), "*playlists*") && app.FindById("LibraryBusyText") is null, "the library", 90);
        await SaveAsync(app, staging, "step-2-playlist");
        await app.OpenLibraryItemAsync(playlist.Name);

        // The payoff: songs already on the player, the new ones found and ready to go
        var adding = $"Adding {playlist.NewSinceLastSync} songs";
        await app.UntilAsync(() => app.Text("SyncSummaryText") == adding && app.FindById("PlaylistBusyText") is null, $"'{adding}'", 60);
        var tracks = await app.UntilAsync(() => app.FindById("TracksListView"), "the track list");
        var last = demo.Track(RoadTrip, playlist.Total - 1);
        await app.UntilAsync(() => app.AllText().Contains("Source found", StringComparison.Ordinal) && tracks.FindListItem($"{last.Title} — {last.Artist}") is not null,
            "the new songs to be found");
        await SaveAsync(app, staging, "hero");

        // 3. Sync
        await app.ClickAsync("SyncButton");
        await app.UntilAsync(() =>
        {
            var text = app.AllText();
            return string.IsNullOrEmpty(app.Text("SyncSummaryText")) && text.Contains("Sync to player", StringComparison.Ordinal)
                && !(text.Contains("Source found", StringComparison.Ordinal) || text.Contains("Downloading", StringComparison.Ordinal)
                    || text.Contains("Queued", StringComparison.Ordinal) || text.Contains("Saving", StringComparison.Ordinal));
        }, "the sync to finish", 60);
        Directory.GetFiles(playlistFolder, "*.m4a").Length.ShouldBe(playlist.Total, "Every song should be in the playlist's folder");
        await SaveAsync(app, staging, "step-3-synced");
        await app.CloseAsync(cancellationToken);

        // Only once every picture came out
        Directory.CreateDirectory(output);
        foreach (var name in (string[])["hero", "step-1-player", "step-2-playlist", "step-3-synced"])
            Publish(Path.Combine(staging, name + ".png"), Path.Combine(output, name + ".png"));
        Console.WriteLine($"Saved the screenshots to {output}");
    }

    /// <summary>
    /// Saves the window without its invisible resize borders, at its size in device-independent pixels whatever the
    /// display scaling, so the pictures are the same size for everyone. It waits for the window to be drawn, and for
    /// animations and covers to settle: until two pictures in a row are the same, and not blank.
    /// </summary>
    private static async Task SaveAsync(AppUnderTest app, string folder, string name)
    {
        // Windows gives PrintWindow stale or blank pictures while the PC is locked.
        if (WindowPictures.IsLocked()) throw new InvalidOperationException($"The PC is locked, so {name}.png can't be taken. Run this again once it's unlocked.");
        byte[]? previous = null;
        var picture = await app.UntilAsync(() =>
        {
            var taken = Take(app);
            var pixels = Pixels(taken);
            var settled = previous is not null && pixels.AsSpan().SequenceEqual(previous) && !IsBlank(taken);
            previous = pixels;
            if (settled) return taken;
            taken.Dispose();
            return null;
        }, $"{name} to be drawn and settle. Windows gives blank pictures while the PC is locked or its display is off. Nothing was saved", 15);
        using (picture)
        {
            var path = Path.Combine(folder, name + ".png");
            picture.Save(path, ImageFormat.Png);
            Console.WriteLine($"  Screenshot: {name} ({new FileInfo(path).Length} bytes)");
        }
    }

    private static unsafe Bitmap Take(AppUnderTest app)
    {
        var window = (HWND)app.Window.Properties.NativeWindowHandle.Value;
        PInvoke.GetWindowRect(window, out var bounds);
        RECT frame;
        if (PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Failed) frame = bounds;
        using var whole = new Bitmap(bounds.right - bounds.left, bounds.bottom - bounds.top);
        using (var graphics = Graphics.FromImage(whole))
        {
            var hdc = graphics.GetHdc();
            try { PInvoke.PrintWindow(window, (HDC)hdc, (PRINT_WINDOW_FLAGS)2); } // PW_RENDERFULLCONTENT
            finally { graphics.ReleaseHdc(hdc); }
        }
        var cropped = whole.Clone(new Rectangle(frame.left - bounds.left, frame.top - bounds.top, frame.right - frame.left, frame.bottom - frame.top), PixelFormat.Format24bppRgb);
        var scale = (double)Width / cropped.Width;
        if (Math.Abs(scale - 1) <= 0.01) return cropped;
        var resized = new Bitmap(Width, (int)Math.Round(cropped.Height * scale), PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(cropped, 0, 0, resized.Width, resized.Height);
        }
        cropped.Dispose();
        return resized;
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// While the PC is locked, Windows gives blank pictures (one color, black or white, perhaps with a border) or stale
    /// ones (refused before they're taken); never keep those.
    /// </summary>
    private static bool IsBlank(Bitmap bitmap)
    {
        var (bright, samples) = (0, 0);
        var colors = new HashSet<int>();
        for (var x = 0; x < bitmap.Width; x += 17)
        {
            for (var y = 0; y < bitmap.Height; y += 17)
            {
                var pixel = bitmap.GetPixel(x, y);
                samples++;
                colors.Add(pixel.ToArgb());
                if (pixel.GetBrightness() > 0.08) bright++;
            }
        }
        return (double)bright / samples < 0.2 || colors.Count <= 2;
    }

    /// <summary>
    /// Copies a screenshot into docs\images, made smaller with ImageMagick if it's installed: flat UI loses nothing
    /// visible at 256 colors, but the covers' gradients would dither, so the playlist page stays lossless.
    /// </summary>
    private static void Publish(string from, string to)
    {
        string[] colors = Path.GetFileNameWithoutExtension(from) == "step-2-playlist" ? [] : ["-colors", "256"];
        try
        {
            var start = new ProcessStartInfo("magick") { UseShellExecute = false };
            foreach (var argument in (string[])[from, "-strip", .. colors, "-define", "png:compression-level=9", to]) start.ArgumentList.Add(argument);
            using var magick = Process.Start(start)!;
            magick.WaitForExit();
            if (magick.ExitCode == 0) return;
            Console.WriteLine($"  ImageMagick exited with {magick.ExitCode}; copying {Path.GetFileName(from)} as it is");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ImageMagick isn't installed.
        }
        File.Copy(from, to, overwrite: true);
    }
}
