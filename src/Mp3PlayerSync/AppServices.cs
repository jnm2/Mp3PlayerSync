using Microsoft.UI.Xaml;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync;

/// <summary>
/// What the app works with outside itself: where it keeps its data, the sites it signs in to and exports from, where
/// songs come from, and how it shows its window and opens links. Put together once, when the app starts.
/// </summary>
public sealed class AppServices
{
    /// <summary>The folder holding settings, caches, downloads and the WebView2 profile.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Exportify's home page.</summary>
    public Uri ExportifyUri { get; init; } = new("https://exportify.net/");

    /// <summary>The origins of Spotify's sign-in, consent and verification pages.</summary>
    public IReadOnlyList<string> SpotifySignInOrigins { get; init; } = (string[])["https://accounts.spotify.com", "https://challenge.spotify.com"];

    /// <summary>Finds and downloads songs, given the YouTube Music downloader.</summary>
    public Func<YouTubeMusicDownloader, ITrackFinder> CreateTrackFinder { get; init; } = downloader => new YouTubeTrackFinder(downloader);

    /// <summary>Lists the mounted drives, with what Windows reports about each.</summary>
    public Func<IReadOnlyList<DriveCandidate>> GetDrives { get; init; } = () => DriveProbe.GetDrives();

    /// <summary>Where a drive's files are: its root folder.</summary>
    public Func<DriveCandidate, string> DriveFolder { get; init; } = drive => drive.RootPath;

    /// <summary>How many YouTube lookups and downloads run at once, from this PC's processors and free memory.</summary>
    public DownloadConcurrency DownloadConcurrency { get; init; } = DownloadConcurrency.For(
        Environment.ProcessorCount,
        (long)Windows.System.Diagnostics.SystemDiagnosticInfo.GetForCurrentSystem().MemoryUsage.GetReport().AvailableSizeInBytes);

    /// <summary>Shows the app's window once it's created.</summary>
    public Action<Window> ShowWindow { get; init; } = window => window.Activate();

    /// <summary>Brings the window back, restored and in front, when the app is launched again.</summary>
    public Action<Window> BringBackWindow { get; init; } = window =>
    {
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        window.Activate();
    };

    /// <summary>Opens a web page in the user's default app for it.</summary>
    public Action<Uri> OpenExternally { get; init; } = uri => _ = Windows.System.Launcher.LaunchUriAsync(uri);

    /// <summary>The services of a normal launch.</summary>
    public static AppServices CreateDefault() => new()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mp3PlayerSync"),
    };
}
