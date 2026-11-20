namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// How the test host (tests\Mp3PlayerSync.E2EHost) is started: what it reads from its environment, set for each launch
/// of the app rather than for the tests' own process, so runs side by side in one process can't see each other's.
/// </summary>
/// <param name="DataDirectory">The app's data folder, and so its settings, WebView2 profile, caches and single-instance key.</param>
/// <param name="ExportifyUrl">Exportify's home page: the mock's, with any test switches such as <c>?autoSignIn</c>; or null, with no <paramref name="SpotifyAccountsUrl"/>, for the real exportify.net and Spotify.</param>
/// <param name="SpotifyAccountsUrl">Where Spotify's sign-in pages are, which the app shows itself.</param>
public sealed record HostLaunch(string DataDirectory, Uri? ExportifyUrl, Uri? SpotifyAccountsUrl)
{
    /// <summary>What every song is "downloaded" as (see FakeTrackFinder in the test host).</summary>
    public string FakeAudio { get; init; } = Locations.FakeAudio;

    /// <summary>
    /// Drives to list instead of the real ones, as JSON (see TestDrives in the test host), or null for the real ones.
    /// </summary>
    public string? TestDrives { get; init; }

    /// <summary>
    /// A file for the test host to write the songs the app looks for to, as the app read them from the export (see
    /// TrackRecorder in the test host), or null for none.
    /// </summary>
    public string? RecordTracksTo { get; init; }

    /// <summary>The test host to run.</summary>
    public string AppPath { get; init; } = Locations.AppPath;

    public Dictionary<string, string> EnvironmentVariables() => new()
    {
        ["MP3PLAYERSYNC_DATA_DIR"] = DataDirectory,
        ["MP3PLAYERSYNC_EXPORTIFY_URL"] = ExportifyUrl?.AbsoluteUri ?? "",
        ["MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL"] = SpotifyAccountsUrl?.AbsoluteUri ?? "",
        ["MP3PLAYERSYNC_TEST_FAKE_AUDIO"] = FakeAudio,
        ["MP3PLAYERSYNC_TEST_DRIVES"] = TestDrives ?? "",
        ["MP3PLAYERSYNC_TEST_RECORD_TRACKS"] = RecordTracksTo ?? "",
    };
}
