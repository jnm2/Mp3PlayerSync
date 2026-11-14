using System.Reflection;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>Where the things the tests run are, by default.</summary>
internal static class Locations
{
    /// <summary>Set by the launcher for the tests: the test host to run.</summary>
    public const string AppVariable = "MP3PLAYERSYNC_E2E_APP";

    /// <summary>The repository's root, which this was built in.</summary>
    public static string? RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Mp3PlayerSync.slnx"))) return directory.FullName;
            }
            return null;
        }
    }

    /// <summary>
    /// The test host (tests\Mp3PlayerSync.E2EHost) built with the same configuration as this, which builds it first.
    /// </summary>
    public static string? DefaultAppPath
    {
        get
        {
            var configuration = typeof(Locations).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Configuration").Value!;
            var bin = RepositoryRoot is { } root ? Path.Combine(root, "tests", "Mp3PlayerSync.E2EHost", "bin", configuration) : null;
            return bin is not null && Directory.Exists(bin)
                ? Directory.EnumerateFiles(bin, "Mp3PlayerSync.E2EHost.exe", SearchOption.AllDirectories).Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
    }

    public static string AppPath =>
        Environment.GetEnvironmentVariable(AppVariable) is { Length: > 0 } path ? path
        : DefaultAppPath ?? throw new InvalidOperationException("The test host isn't built. Build first: dotnet build Mp3PlayerSync.slnx");

    /// <summary>What every song is "downloaded" as (see FakeTrackFinder in the test host).</summary>
    public static string FakeAudio => Path.Combine(AppContext.BaseDirectory, "Assets", "silence.m4a");
}
