using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Mp3PlayerSync.E2EHost;

/// <summary>
/// Renders the window's content through XAML itself (<see cref="RenderTargetBitmap"/>) when the test asks, by creating
/// <c>Capture.request</c> in the data folder: the picture is written to <c>Capture.png</c>, in the window's physical
/// pixels from the top left of its client area, then the request is deleted (if it fails, <c>Capture.png.error</c>
/// says why). Unlike <c>PrintWindow</c>, which gets stale or blank pictures from Windows while the PC is locked, this
/// keeps working then. It leaves out the title bar, backdrop and web pages, which aren't drawn by the app's XAML.
/// </summary>
internal static class WindowCapture
{
    public static void Serve(Window window, string dataDirectory)
    {
        var request = Path.Combine(dataDirectory, "Capture.request");
        var output = Path.Combine(dataDirectory, "Capture.png");
        // The test can launch the app on a data folder it hasn't made yet, and the watcher needs the folder to exist.
        Directory.CreateDirectory(dataDirectory);
        var watcher = new FileSystemWatcher(dataDirectory, Path.GetFileName(request)) { NotifyFilter = NotifyFilters.FileName };
        var busy = 0;
        void Requested()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (File.Exists(request) && window.Content is UIElement content)
                    {
                        File.Delete(output);
                        await RenderAsync(content, output);
                    }
                }
                catch (Exception exception)
                {
                    File.WriteAllText(output + ".error", exception.ToString());
                }
                finally
                {
                    File.Delete(request);
                    Volatile.Write(ref busy, 0);
                }
            });
        }
        watcher.Created += (_, _) => Requested();
        watcher.EnableRaisingEvents = true;
        window.Closed += (_, _) => watcher.Dispose();
    }

    private static async Task RenderAsync(UIElement content, string output)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();

        // The backdrop isn't part of the XAML, so where the content is see-through, this shows what Mica falls back to:
        // Fluent's SolidBackgroundFillColorBase for the theme.
        var dark = content is FrameworkElement { ActualTheme: ElementTheme.Dark };
        var backdrop = dark ? (byte)0x20 : (byte)0xF3;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var seeThrough = 255 - pixels[i + 3];
            for (var channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)(pixels[i + channel] + (backdrop * seeThrough + 127) / 255);
            pixels[i + 3] = 255;
        }

        var temporary = output + ".partial";
        using (var file = File.Create(temporary))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        File.Move(temporary, output, overwrite: true);
    }
}
