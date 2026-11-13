namespace Mp3PlayerSync.Core.Download.Tools;

/// <summary>
/// Keeps a specific cached version of a tool from being deleted until disposed.
/// </summary>
public sealed class ToolLease : IDisposable
{
    private IDisposable? lease;

    internal ToolLease(string version, string filePath, IDisposable lease)
    {
        Version = version;
        FilePath = filePath;
        this.lease = lease;
    }

    public string Version { get; }

    public string FilePath
    {
        get => Volatile.Read(ref lease) is null ? throw new ObjectDisposedException(nameof(ToolLease)) : field;
    }

    public void Dispose() => Interlocked.Exchange(ref lease, null)?.Dispose();
}
