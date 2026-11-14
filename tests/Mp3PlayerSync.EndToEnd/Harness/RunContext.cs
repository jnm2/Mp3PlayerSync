using System.Text.Json;
using FlaUI.Core;
using FlaUI.UIA3;
using Mp3PlayerSync.MockExportify;
using TUnit.Core.Interfaces;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Everything one run of the tests uses, its own so runs can go side by side: a data folder (and so the app's settings,
/// WebView2 profile, caches and single-instance key, and the test's player and drive folders), the mock Exportify on
/// a port of its own, and the apps it started. Shared by a test class's tests, through
/// <c>[ClassDataSource&lt;RunContext&gt;(Shared = SharedType.PerClass)]</c>.
/// </summary>
public sealed class RunContext : IAsyncInitializer, IAsyncDisposable
{
    private readonly List<AppUnderTest> apps = [];

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
        Directory.CreateDirectory(DataDirectory);
        Mock = await MockExportifyServer.StartAsync(Console.WriteLine);
    }

    /// <summary>How to start the app against the mock, with <paramref name="query"/>'s test switches, such as <c>?autoSignIn</c>.</summary>
    public HostLaunch Launch(string query = "") => new(
        DataDirectory,
        new Uri(Mock.Url, query),
        // The mock's fake Spotify sign-in page, on another origin like accounts.spotify.com
        new UriBuilder(Mock.Url) { Host = "127.0.0.1" }.Uri);

    /// <summary>Starts the app against the mock and waits for its window; it's closed with the run, if not before.</summary>
    public async Task<AppUnderTest> StartAppAsync(string query = "", CancellationToken cancellationToken = default)
    {
        var app = await AppUnderTest.StartAsync(Automation, Launch(query), cancellationToken);
        apps.Add(app);
        return app;
    }

    /// <summary>A folder in the data folder, for a player or a drive.</summary>
    public string CreateFolder(string name) => Directory.CreateDirectory(Path.Combine(DataDirectory, name)).FullName;

    /// <summary>Writes the app's settings.json, as if it had been used before.</summary>
    public void WriteSettings(object settings) =>
        File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public async ValueTask DisposeAsync()
    {
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
