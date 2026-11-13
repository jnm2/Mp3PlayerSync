namespace Mp3PlayerSync.Core.Download.Tools;

/// <summary>
/// Holds a file open with read-only sharing so that it can't be deleted or replaced (e.g. by <see
/// cref="ToolResolver.PurgeOldVersions"/> in another instance of the app) while any lease is outstanding.
/// </summary>
internal sealed class RefCountedFileLock
{
    private readonly FileStream stream;
    private readonly object lockObject = new();
    private int refCount;

    private RefCountedFileLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static RefCountedFileLock? CreateIfExists(string filePath)
    {
        try
        {
            return new RefCountedFileLock(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public string FilePath => stream.Name;

    /// <summary>
    /// Adds a reference. The file is released when the last lease is disposed.
    /// </summary>
    public IDisposable Lease()
    {
        lock (lockObject)
        {
            if (refCount < 0)
                throw new ObjectDisposedException(nameof(RefCountedFileLock));

            refCount++;
        }

        return new LeaseHandle(this);
    }

    private void Release()
    {
        lock (lockObject)
        {
            refCount--;
            if (refCount > 0) return;
            refCount = -1;
        }

        stream.Dispose();
    }

    private sealed class LeaseHandle(RefCountedFileLock owner) : IDisposable
    {
        private RefCountedFileLock? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
