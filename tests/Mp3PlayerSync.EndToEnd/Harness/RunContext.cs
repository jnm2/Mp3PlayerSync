using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.UIA3;
using Mp3PlayerSync.MockExportify;
using Shouldly;
using TUnit.Core.Interfaces;
using Windows.Win32;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Everything one run of the tests uses, its own so runs can go side by side: a data folder (and so the app's settings,
/// WebView2 profile, caches and single-instance key, and the test's player and drive folders), the mock Exportify on
/// a port of its own, and the apps it started. A test class takes it through a required property with
/// <c>[ClassDataSource&lt;RunContext&gt;]</c>: <c>Shared = SharedType.PerClass</c> shares one run among its tests, and
/// <c>SharedType.None</c> gives each test a run of its own.
/// </summary>
/// <remarks>
/// It's a property rather than a constructor parameter because TUnit names a class with constructor parameters after
/// them, such as <c>SmokeTests(RunContext)</c>, and then Visual Studio can't find the test that the editor's Run Tests or
/// Debug Tests asks for.
/// </remarks>
public sealed class RunContext : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The test's player: a folder, since no removable drive can be assumed, as if chosen with "Other folder" last time.</summary>
    public const string PlayerName = "E2E Player";

    private static int active;
    private readonly List<AppUnderTest> apps = [];
    private readonly DateTime started = DateTime.Now;
    private bool counted;

    /// <summary>How many runs this process is driving at the same time, which slows each of them down (see <see cref="Wait.Scale"/>).</summary>
    public static int Active => Volatile.Read(ref active);

    public RunContext()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), $"Mp3PlayerSyncE2E-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
    }

    /// <summary>
    /// UI Automation's own client (UIA3), as Narrator uses. Its timeouts stop a request to an app that hangs from
    /// hanging the test.
    /// </summary>
    public UIA3Automation Automation { get; } = new() { ConnectionTimeout = TimeSpan.FromSeconds(10), TransactionTimeout = TimeSpan.FromSeconds(10) };

    public string DataDirectory { get; }

    public MockExportifyServer Mock { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // The test runs on a hidden desktop in another process, which makes its own.
        if (HiddenDesktopForIdeRunsExecutor.RunsTestsElsewhere) return;
        Directory.CreateDirectory(DataDirectory);
        Interlocked.Increment(ref active);
        counted = true;
        Mock = await MockExportifyServer.StartAsync(Console.WriteLine);
    }

    /// <summary>How to start the app against the mock, with <paramref name="query"/>'s test switches, such as <c>?autoSignIn</c>.</summary>
    public HostLaunch Launch(string query = "") => new(
        DataDirectory,
        new Uri(Mock.Url, query),
        // The mock's fake Spotify sign-in page, on another origin like accounts.spotify.com
        new UriBuilder(Mock.Url) { Host = "127.0.0.1" }.Uri);

    /// <summary>Starts the app against the mock and waits for its window; it's closed with the run, if not before.</summary>
    public Task<AppUnderTest> StartAppAsync(string query = "", CancellationToken cancellationToken = default) =>
        StartAppAsync(Launch(query), cancellationToken);

    /// <summary>Starts the app as <paramref name="launch"/> says and waits for its window; it's closed with the run, if not before.</summary>
    public async Task<AppUnderTest> StartAppAsync(HostLaunch launch, CancellationToken cancellationToken = default)
    {
        var app = await AppUnderTest.StartAsync(Automation, launch, cancellationToken);
        apps.Add(app);
        return app;
    }

    /// <summary>A folder in the data folder, for a player or a drive.</summary>
    public string CreateFolder(string name) => Directory.CreateDirectory(Path.Combine(DataDirectory, name)).FullName;

    /// <summary>The app's settings.json.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Writes the app's settings.json, as if it had been used before.</summary>
    public void WriteSettings(object settings) =>
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    /// <summary>The player's folder (see <see cref="PlayerName"/>).</summary>
    public string PlayerFolder => Path.Combine(DataDirectory, PlayerName);

    /// <summary>The playlist folder on the player that "Road Trip, Vol. 2" syncs into.</summary>
    public string RoadTripFolder => Path.Combine(PlayerFolder, "Road Trip, Vol. 2");

    /// <summary>A file in Road Trip's folder that's no longer in the playlist.</summary>
    public string RemovedFile => Path.Combine(RoadTripFolder, "03. Gone - Removed song.mp3");

    /// <summary>
    /// Makes the player and chooses it in the app's settings. Playlists sync into a folder of their own. One Road Trip
    /// track is already there, one file there is no longer in the playlist, and another track is elsewhere on the player
    /// (which doesn't count for the playlist).
    /// </summary>
    public void SetUpPlayer()
    {
        foreach (var file in (string[])[Path.Combine(RoadTripFolder, "02. Artist 1 - roadtrip song 2.mp3"), RemovedFile, Path.Combine(PlayerFolder, @"Music\Deep\Artist 4 - roadtrip song 5.mp3")])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [.. Enumerable.Range(1, 64).Select(b => (byte)b)]);
        }
        WriteSettings(new { Player = new { FolderPath = PlayerFolder } });
    }

    /// <summary>
    /// Gets to where a run starts that comes after others: signed in, with the whole library listed and
    /// <paramref name="lastItem"/> opened last. It sets up the player first.
    /// </summary>
    public async Task PrepareAsync(string lastItem, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Preparing: signed in, '{lastItem}' opened last");
        SetUpPlayer();
        var app = await StartAppAsync("?autoSignIn", cancellationToken);
        await app.ChoosePlayerAsync(null, PlayerName);
        // The item opened last reopens by itself; go back to the library from it.
        await app.UntilAsync(() => (app.Text("LibrarySummaryText") is { Length: > 0 } && app.FindById("LibraryBusyText") is null) || app.Text("PlaylistTitle") is not null,
            "the library or the item opened last", 90);
        if (app.Text("PlaylistTitle") is not null) await app.ClickAsync("BackButton");
        await app.UntilAsync(() => app.Text("LibrarySummaryText") is { Length: > 0 } && app.FindById("LibraryBusyText") is null, "the library", 90);
        await app.SelectFilterAsync("All");
        // All of it, saved albums included: they arrive after the playlists, without busy text, and rebuild the grid,
        // which would drop a click on a tile from before.
        await app.UntilAsync(() => app.Text("LibrarySummaryText") == "64 playlists and albums" && app.FindById("LibraryBusyText") is null, "the whole library", 60);
        await app.OpenLibraryItemAsync(lastItem);
        await app.UntilAsync(() => app.Text("SummaryText") is { Length: > 0 } && app.FindById("PlaylistBusyText") is null, $"'{lastItem}' to load", 60);
        await app.CloseAsync(cancellationToken);
    }

    /// <summary>Sets up the player, then starts the app signed in, with the player chosen, and waits for the library.</summary>
    public async Task<AppUnderTest> SignInAsync(CancellationToken cancellationToken = default)
    {
        SetUpPlayer();
        var app = await StartAppAsync("?autoSignIn", cancellationToken);
        await app.ChoosePlayerAsync(null, PlayerName);
        await app.WaitLibraryAsync("40 playlists");
        return app;
    }

    /// <summary>
    /// Adds the checks that hold for a whole run, then finishes <paramref name="checks"/> (see <see cref="Checks.Finish"/>).
    /// </summary>
    public void Finish(Checks checks)
    {
        checks.Check("Exportify's own page was never shown", () => apps.Where(a => a.SawExportifyPage).ShouldBeEmpty());
        // Only what Exportify makes (CSV files, or a zip of them) or a download in progress: the user may well be
        // downloading something else meanwhile.
        var downloads = DownloadsFolder();
        var written = Directory.EnumerateFiles(downloads)
            .Where(f => File.GetLastWriteTime(f) > started && Path.GetExtension(f).ToLowerInvariant() is ".csv" or ".zip" or ".crdownload")
            .ToList();
        checks.Check("Nothing was written to the Downloads folder", () => written.ShouldBeEmpty());
        checks.Finish();
    }

    private static string DownloadsFolder()
    {
        PInvoke.SHGetKnownFolderPath(PInvoke.FOLDERID_Downloads, 0, null, out var path).ThrowOnFailure();
        using (path) return Marshal.PtrToStringUni(path.DangerousGetHandle())!;
    }

    public async ValueTask DisposeAsync()
    {
        if (counted) Interlocked.Decrement(ref active);
        foreach (var app in apps) await app.DisposeAsync();
        if (Mock is not null) await Mock.DisposeAsync();
        Automation.Dispose();
        if (!Directory.Exists(DataDirectory)) return; // Skipped before it was made
        try { Directory.Delete(DataDirectory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  Couldn't delete the data folder {DataDirectory}: {exception.Message}");
        }
    }
}
