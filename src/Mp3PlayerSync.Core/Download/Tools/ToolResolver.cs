namespace Mp3PlayerSync.Core.Download.Tools;

/// <summary>
/// Keeps the latest version of a downloadable tool in a local cache (one <c>v{version}</c> folder per version) and
/// hands out leases which keep the leased version from being purged while it is in use.
/// </summary>
/// <remarks>
/// Ported from the design of jnm2/YouTubeDownloadTool's <c>ToolResolver</c>.
/// </remarks>
public sealed class ToolResolver : IDisposable
{
    private readonly string cacheDirectory;
    private readonly string fileName;
    private readonly Func<CancellationToken, Task<AvailableToolDownload>> getLatestDownloadAsync;
    private readonly Lock lockObject = new();
    private LeaseSource? currentSource;
    private Task<LeaseSource>? resolveTask;
    private bool isDisposed;

    public ToolResolver(string cacheDirectory, string fileName, Func<CancellationToken, Task<AvailableToolDownload>> getLatestDownloadAsync)
    {
        if (!Path.IsPathFullyQualified(cacheDirectory))
            throw new ArgumentException("The cache directory must be fully qualified.", nameof(cacheDirectory));

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        this.cacheDirectory = cacheDirectory;
        this.fileName = fileName;
        this.getLatestDownloadAsync = getLatestDownloadAsync ?? throw new ArgumentNullException(nameof(getLatestDownloadAsync));
        currentSource = GetNewestCachedTool();
    }

    /// <summary>The version that <see cref="LeaseToolAsync"/> will return without downloading, if any.</summary>
    public string? CachedVersion
    {
        get { lock (lockObject) return currentSource?.Version; }
    }

    public void Dispose()
    {
        LeaseSource? source;
        lock (lockObject)
        {
            isDisposed = true;
            source = currentSource;
            currentSource = null;
        }

        source?.Dispose();
    }

    /// <summary>
    /// Returns the cached tool if there is one; otherwise, downloads the latest version first.
    /// </summary>
    public async Task<ToolLease> LeaseToolAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (lockObject)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                if (currentSource is not null) return currentSource.CreateLease();
            }

            await StartOrJoinResolve().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Downloads the latest version if it is newer than the cached version. Leases already handed out continue to
    /// refer to the version they were created with.
    /// </summary>
    public Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        return StartOrJoinResolve().WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes all cached versions except the newest, skipping any that are in use.
    /// </summary>
    public void PurgeOldVersions()
    {
        foreach (var (_, _, directory) in GetVersionDirectories().OrderBy(d => d.Version).SkipLast(1))
        {
            try
            {
                // Deleting the file first fails if any instance of the app is holding a lease on it.
                File.Delete(Path.Join(directory, fileName));
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private Task<LeaseSource> StartOrJoinResolve()
    {
        lock (lockObject)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (resolveTask is { IsCompleted: false }) return resolveTask;
            return resolveTask = Task.Run(ResolveLatestAsync);
        }
    }

    private async Task<LeaseSource> ResolveLatestAsync()
    {
        // Not cancelable so that a canceled caller doesn't fail other callers who joined the same task.
        var download = await getLatestDownloadAsync(CancellationToken.None).ConfigureAwait(false);

        lock (lockObject)
        {
            if (currentSource is not null && string.Equals(download.Version, currentSource.Version, StringComparison.OrdinalIgnoreCase))
                return currentSource;
        }

        var fileLock = await GetOrDownloadFileAsync(Path.Join(cacheDirectory, "v" + download.Version, fileName), download).ConfigureAwait(false);
        var newSource = new LeaseSource(download.Version, fileLock);

        LeaseSource? replaced;
        lock (lockObject)
        {
            if (isDisposed)
            {
                replaced = newSource;
            }
            else
            {
                replaced = currentSource;
                currentSource = newSource;
            }
        }

        replaced?.Dispose();
        return newSource;
    }

    private async Task<RefCountedFileLock> GetOrDownloadFileAsync(string filePath, AvailableToolDownload download)
    {
        if (RefCountedFileLock.CreateIfExists(filePath) is { } existing)
            return existing;

        Directory.CreateDirectory(cacheDirectory);
        var tempPath = Path.Join(cacheDirectory, Path.GetRandomFileName() + ".tmp");
        var tempDirectory = tempPath + ".dir";
        try
        {
            await using (var stream = await download.DownloadAsync(CancellationToken.None).ConfigureAwait(false))
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await stream.CopyToAsync(file).ConfigureAwait(false);
            }

            if (download.IsZipArchive)
            {
                // Extract next to the version folder and then rename it into place, so that a version folder only
                // ever exists complete.
                System.IO.Compression.ZipFile.ExtractToDirectory(tempPath, tempDirectory);
                if (!File.Exists(Path.Join(tempDirectory, fileName)))
                    throw new InvalidDataException($"The downloaded archive doesn't contain {fileName}.");
            }

            var versionDirectory = Path.GetDirectoryName(filePath)!;
            while (true)
            {
                try
                {
                    if (download.IsZipArchive)
                    {
                        if (Directory.Exists(versionDirectory) && !File.Exists(filePath))
                            Directory.Delete(versionDirectory, recursive: true); // Left incomplete by an older version of the app
                        Directory.Move(tempDirectory, versionDirectory);
                    }
                    else
                    {
                        Directory.CreateDirectory(versionDirectory);
                        File.Move(tempPath, filePath);
                    }
                }
                catch (IOException) when (File.Exists(filePath))
                {
                    // Another instance of the app downloaded the same version at the same time.
                }

                if (RefCountedFileLock.CreateIfExists(filePath) is { } fileLock)
                    return fileLock;
            }
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }

            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); }
            catch (IOException) { }
        }
    }

    private LeaseSource? GetNewestCachedTool()
    {
        foreach (var (_, rawVersion, directory) in GetVersionDirectories().OrderByDescending(d => d.Version))
        {
            if (RefCountedFileLock.CreateIfExists(Path.Join(directory, fileName)) is { } fileLock)
                return new LeaseSource(rawVersion, fileLock);
        }

        return null;
    }

    private IEnumerable<(Version Version, string RawVersion, string Directory)> GetVersionDirectories()
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(cacheDirectory, "v*");
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        return
            from directory in directories
            let rawVersion = Path.GetFileName(directory)[1..]
            where Version.TryParse(rawVersion, out _)
            select (Version.Parse(rawVersion), rawVersion, directory);
    }

    private sealed class LeaseSource : IDisposable
    {
        private readonly RefCountedFileLock fileLock;
        private readonly IDisposable ownLease;

        public LeaseSource(string version, RefCountedFileLock fileLock)
        {
            Version = version;
            this.fileLock = fileLock;
            ownLease = fileLock.Lease();
        }

        public string Version { get; }

        public ToolLease CreateLease() => new(Version, fileLock.FilePath, fileLock.Lease());

        public void Dispose() => ownLease.Dispose();
    }
}
