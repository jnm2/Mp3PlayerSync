namespace Mp3PlayerSync.Core.Sync;

/// <summary>
/// A local folder (on the PC, never the player) where one sync prepares files: downloads, remuxes and tags them
/// before they're copied to the player. Deleted when disposed; folders left by a sync that crashed are deleted
/// when the next one starts.
/// </summary>
public sealed class StagingDirectory : IDisposable
{
    private const string LockFileName = "sync.lock";

    private readonly FileStream lockFile;

    private StagingDirectory(string path, FileStream lockFile)
    {
        Path = path;
        this.lockFile = lockFile;
    }

    public string Path { get; }

    /// <summary>Removes stale staging folders under <paramref name="workDirectory"/>, then creates a new one.</summary>
    public static StagingDirectory Create(string workDirectory)
    {
        RemoveStale(workDirectory);
        var path = Directory.CreateDirectory(System.IO.Path.Combine(workDirectory, Guid.NewGuid().ToString("N"))).FullName;

        // Held open while syncing, so other syncs know this folder is in use.
        var lockFile = new FileStream(System.IO.Path.Combine(path, LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new StagingDirectory(path, lockFile);
    }

    /// <summary>Deletes the staging folders under <paramref name="workDirectory"/> that no running sync is using.</summary>
    public static void RemoveStale(string workDirectory)
    {
        if (!Directory.Exists(workDirectory)) return;

        foreach (var folder in Directory.EnumerateDirectories(workDirectory))
        {
            try
            {
                var lockPath = System.IO.Path.Combine(folder, LockFileName);
                if (File.Exists(lockPath))
                {
                    // Fails while a sync holds it open.
                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }

                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        lockFile.Dispose();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
