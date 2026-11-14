namespace Mp3PlayerSync.E2EHost;

/// <summary>
/// Starts MP3 Player Sync for the end-to-end tests (tests\Test-MockEndToEnd.ps1): off-screen and never activated,
/// against the mock Exportify, with its own data folder, and with <see cref="FakeTrackFinder"/> standing in for
/// YouTube Music. Without the mock's addresses, it runs against the real exportify.net and Spotify, with the
/// addresses the released app uses.
/// </summary>
/// <remarks>
/// Set by the test script:
/// <list type="bullet">
/// <item><c>MP3PLAYERSYNC_DATA_DIR</c>: the data folder.</item>
/// <item><c>MP3PLAYERSYNC_EXPORTIFY_URL</c> (optional): the mock Exportify's home page.</item>
/// <item><c>MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL</c> (optional): where the mock's Spotify sign-in pages are. It is set
/// together with <c>MP3PLAYERSYNC_EXPORTIFY_URL</c>, or neither is set.</item>
/// <item><c>MP3PLAYERSYNC_TEST_FAKE_AUDIO</c>: the .m4a file every song is "downloaded" as.</item>
/// <item><c>MP3PLAYERSYNC_TEST_DRIVES</c> (optional): drives to list instead of the real ones (see <see cref="TestDrives"/>).</item>
/// </list>
/// Links the app would open outside itself are written to <c>OpenedExternally.txt</c> in the data folder instead.
/// While <c>HoldFinding.txt</c> exists in the data folder, looking for songs in the background pauses (see <see cref="FakeTrackFinder"/>).
/// Creating <c>Capture.request</c> there asks for a picture of the window (see <see cref="WindowCapture"/>).
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main()
    {
        var dataDirectory = Required("MP3PLAYERSYNC_DATA_DIR");
        var fakeAudio = Required("MP3PLAYERSYNC_TEST_FAKE_AUDIO");
        var testDrives = TestDrives.FromEnvironment();
        var mockExportify = Optional("MP3PLAYERSYNC_EXPORTIFY_URL");
        var mockSpotifyAccounts = Optional("MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL");
        // Half the mock would send the app between the mock and the real sites.
        if ((mockExportify is null) != (mockSpotifyAccounts is null))
            throw new InvalidOperationException("Set both MP3PLAYERSYNC_EXPORTIFY_URL and MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL for the mock, or neither for the real sites.");

        var defaults = new AppServices { DataDirectory = dataDirectory };
        var services = new AppServices
        {
            DataDirectory = dataDirectory,
            ExportifyUri = mockExportify is null ? defaults.ExportifyUri : new Uri(mockExportify),
            SpotifySignInOrigins = mockSpotifyAccounts is null ? defaults.SpotifySignInOrigins : (string[])[mockSpotifyAccounts],
            CreateTrackFinder = _ => new FakeTrackFinder(fakeAudio, Path.Combine(dataDirectory, "HoldFinding.txt")),
            GetDrives = testDrives is null ? defaults.GetDrives : testDrives.List,
            DriveFolder = testDrives is null ? defaults.DriveFolder : testDrives.FolderOf,
            ShowWindow = window =>
            {
                // Off-screen rather than minimized, which would stop the window's content from rendering.
                window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
                window.AppWindow.Show(activateWindow: false);
                WindowCapture.Serve(window, dataDirectory);
            },
            // Launching again never brings the window to the front: it stays off-screen and inactive.
            BringBackWindow = _ => { },
            OpenExternally = uri => File.AppendAllLines(Path.Combine(dataDirectory, "OpenedExternally.txt"), (string[])[uri.AbsoluteUri]),
        };

        return global::Mp3PlayerSync.Program.Run(services);
    }

    private static string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException($"{name} is not set.");

    private static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
