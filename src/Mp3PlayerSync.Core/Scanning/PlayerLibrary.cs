namespace Mp3PlayerSync.Core.Scanning;

public enum PlayerScanPhase
{
    NotStarted,

    /// <summary>Listing the folders on the player to find audio files.</summary>
    FindingFiles,

    /// <summary>Reading the tags of the audio files found.</summary>
    ReadingTags,

    /// <summary>The initial scan is done; the index is kept current as files change.</summary>
    Complete,

    Canceled,

    /// <summary>The player folder couldn't be read. See <see cref="PlayerScanProgress.Error"/>.</summary>
    Failed,
}

/// <param name="FilesFound">Audio files found so far.</param>
/// <param name="FilesRead">Audio files whose tags have been read.</param>
/// <param name="CurrentPath">The folder or file being scanned.</param>
public sealed record PlayerScanProgress(
    PlayerScanPhase Phase,
    int FoldersScanned,
    int FilesFound,
    int FilesRead,
    string? CurrentPath = null,
    Exception? Error = null)
{
    public bool IsFinished => Phase is PlayerScanPhase.Complete or PlayerScanPhase.Canceled or PlayerScanPhase.Failed;

    /// <summary>Between 0 and 1 while reading tags and when complete; <see langword="null"/> while still finding files.</summary>
    public double? Fraction => Phase switch
    {
        PlayerScanPhase.ReadingTags => FilesFound == 0 ? 0 : (double)FilesRead / FilesFound,
        PlayerScanPhase.Complete => 1,
        _ => null,
    };
}

public sealed class PlayerLibraryOptions
{
    /// <summary>How long to wait after the last file system change before updating the index.</summary>
    public TimeSpan ChangeDebounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The least time between <see cref="PlayerLibrary.ProgressChanged"/> events during the scan.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether to watch the player for files added, removed or renamed after the initial scan.</summary>
    public bool WatchForChanges { get; init; } = true;

    public TimeSpan DurationTolerance { get; init; } = PlayerIndexSnapshot.DefaultDurationTolerance;
}

/// <summary>
/// The audio files on one MP3 player (a drive root or any folder), scanned at any depth on a background thread and
/// kept current afterwards. Create one when a player is chosen and dispose it when the player changes.
/// </summary>
/// <remarks>
/// Events are raised on background threads. All members are thread-safe.
/// </remarks>
public sealed class PlayerLibrary : IDisposable
{
    private readonly PlayerLibraryOptions options;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource scanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock gate = new();
    private readonly Dictionary<string, IndexedFile> files = new(StringComparer.OrdinalIgnoreCase);
    private long version;
    private PlayerIndexSnapshot? snapshot;

    // Serializes disk work between the initial scan and processing watched changes.
    private readonly SemaphoreSlim diskLock = new(1, 1);
    private readonly Lock pendingGate = new();
    private readonly Dictionary<string, WatcherChangeTypes> pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool rescanRequested;
    private readonly Timer debounceTimer;
    private FileSystemWatcher? watcher;

    private volatile PlayerScanProgress progress = new(PlayerScanPhase.NotStarted, 0, 0, 0);
    private long lastProgressTicks;
    private int started;
    private int disposed;
    private volatile bool scanFinished;

    public PlayerLibrary(string rootPath, PlayerLibraryOptions? options = null)
    {
        RootPath = Path.GetFullPath(rootPath);
        this.options = options ?? new PlayerLibraryOptions();
        debounceTimer = new Timer(_ => _ = ProcessPendingChangesAsync());
    }

    public string RootPath { get; }

    public PlayerScanProgress Progress => progress;

    /// <summary>
    /// Completes when the initial scan has finished. Canceled if the library is disposed first; faulted if the
    /// player folder can't be read.
    /// </summary>
    public Task ScanCompletion => scanCompletion.Task;

    /// <summary>
    /// Whether the initial scan has finished. This becomes true just before <see cref="ScanCompletion"/> completes,
    /// after the final <see cref="ProgressChanged"/> and <see cref="IndexChanged"/> events of the scan.
    /// </summary>
    public bool IsScanComplete => scanFinished;

    /// <summary>Raised during the scan (at most every <see cref="PlayerLibraryOptions.ProgressInterval"/>) and when it finishes.</summary>
    public event EventHandler<PlayerScanProgress>? ProgressChanged;

    /// <summary>
    /// Raised when the initial scan completes and whenever files are added, changed or removed afterwards.
    /// Call <see cref="GetSnapshot"/> to see the new index.
    /// </summary>
    public event EventHandler? IndexChanged;

    /// <summary>
    /// Starts scanning on a background thread. Subscribe to events first. Calling this again does nothing.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0) return;

        if (options.WatchForChanges) StartWatcher();

        Task.Factory.StartNew(Scan, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// The indexed files and lookups for matching, as of now. Before the scan completes this holds only the files
    /// read so far (see <see cref="PlayerIndexSnapshot.IsScanComplete"/>).
    /// </summary>
    public PlayerIndexSnapshot GetSnapshot()
    {
        IndexedFile[] current;
        long currentVersion;
        var complete = IsScanComplete;
        lock (gate)
        {
            if (snapshot is { } cached && cached.Version == version && cached.IsScanComplete == complete) return cached;
            current = files.Values.ToArray();
            currentVersion = version;
        }

        var created = new PlayerIndexSnapshot(current, options.DurationTolerance, complete, currentVersion);
        lock (gate)
        {
            if (snapshot is null || snapshot.Version < currentVersion || (snapshot.Version == currentVersion && complete))
                snapshot = created;
        }

        return created;
    }

    /// <summary>
    /// Tells the index that a file was written or replaced (for example, copied to the player by sync). Reads its
    /// tags on the calling thread, so the index is current when this returns.
    /// </summary>
    public void NotifyFileWritten(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (IsUnderRoot(file.FullName) && file.Exists && AudioFileScanner.ShouldIndexFile(file) && Upsert(file))
            RaiseIndexChanged();
    }

    /// <summary>Tells the index that a file or folder was deleted.</summary>
    public void NotifyDeleted(string path)
    {
        if (RemoveUnder(Path.GetFullPath(path)).Count > 0) RaiseIndexChanged();
    }

    /// <summary>
    /// Tells the index that a file or folder was moved or renamed. Files keep the tags already read when their size
    /// and modification time are unchanged.
    /// </summary>
    public void NotifyMoved(string oldPath, string newPath)
    {
        oldPath = Path.GetFullPath(oldPath);
        newPath = Path.GetFullPath(newPath);

        var changed = MoveEntries(oldPath, newPath);
        if (IsUnderRoot(newPath) && !AudioFileScanner.IsInSkippedFolder(RootPath, newPath))
        {
            if (Directory.Exists(newPath)) changed |= IndexFolder(newPath, CancellationToken.None);
            else if (new FileInfo(newPath) is { Exists: true } file && AudioFileScanner.ShouldIndexFile(file)) changed |= Upsert(file);
        }

        if (changed) RaiseIndexChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        cancellation.Cancel();
        watcher?.Dispose();
        debounceTimer.Dispose();

        if (ClaimOutcome())
        {
            SetProgress(progress with { Phase = PlayerScanPhase.Canceled }, force: true);
            scanCompletion.TrySetCanceled(cancellation.Token);
        }
    }

    // The scan ends once: completed, failed or canceled (by the scan or by Dispose, whichever comes first). Whoever
    // claims it sets the final Progress and raises its events before completing ScanCompletion, so code that awaits
    // ScanCompletion always sees the final phase.
    private int outcomeClaimed;

    private bool ClaimOutcome() => Interlocked.Exchange(ref outcomeClaimed, 1) == 0;

    private void Scan()
    {
        var token = cancellation.Token;
        var folders = 0;
        var found = new List<FileInfo>();
        try
        {
            diskLock.Wait(token);
            try
            {
                SetProgress(new(PlayerScanPhase.FindingFiles, 0, 0, 0, RootPath), force: true);

                foreach (var file in AudioFileScanner.EnumerateAudioFiles(RootPath, token, folder =>
                {
                    folders++;
                    SetProgress(new(PlayerScanPhase.FindingFiles, folders, found.Count, 0, folder), force: false);
                }))
                {
                    found.Add(file);
                }

                SetProgress(new(PlayerScanPhase.ReadingTags, folders, found.Count, 0), force: true);

                for (var i = 0; i < found.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    SetProgress(new(PlayerScanPhase.ReadingTags, folders, found.Count, i, found[i].FullName), force: false);
                    Upsert(found[i]);
                }

                token.ThrowIfCancellationRequested();
            }
            finally
            {
                diskLock.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ClaimOutcome())
            {
                SetProgress(progress with { Phase = PlayerScanPhase.Canceled }, force: true);
                scanCompletion.TrySetCanceled(token);
            }
            return;
        }
        catch (Exception e)
        {
            if (ClaimOutcome())
            {
                SetProgress(progress with { Phase = PlayerScanPhase.Failed, Error = e }, force: true);
                scanCompletion.TrySetException(e);
            }
            return;
        }

        if (!ClaimOutcome()) return;
        scanFinished = true;
        // Raise the events for the finished scan before completing ScanCompletion, so that code which awaits it and
        // then subscribes never receives them.
        SetProgress(new(PlayerScanPhase.Complete, folders, found.Count, found.Count), force: true);
        RaiseIndexChanged();
        scanCompletion.TrySetResult();

        lock (pendingGate)
        {
            if (pendingChanges.Count > 0 || rescanRequested) ScheduleProcessing();
        }
    }

    private void StartWatcher()
    {
        try
        {
            var w = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Enqueue(e.FullPath, WatcherChangeTypes.Created);
            w.Changed += (_, e) => Enqueue(e.FullPath, WatcherChangeTypes.Changed);
            w.Deleted += (_, e) => Enqueue(e.FullPath, WatcherChangeTypes.Deleted);
            w.Renamed += (_, e) =>
            {
                Enqueue(e.OldFullPath, WatcherChangeTypes.Deleted);
                Enqueue(e.FullPath, WatcherChangeTypes.Created);
            };
            w.Error += (_, _) =>
            {
                // The watcher's buffer overflowed or the device went away: compare the whole player again.
                lock (pendingGate)
                {
                    rescanRequested = true;
                    ScheduleProcessing();
                }
            };
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some file systems can't be watched; the index then only changes through the Notify methods.
        }
    }

    private void Enqueue(string path, WatcherChangeTypes change)
    {
        if (AudioFileScanner.IsInSkippedFolder(RootPath, path)) return;

        lock (pendingGate)
        {
            pendingChanges[path] = pendingChanges.GetValueOrDefault(path) | change;
            ScheduleProcessing();
        }
    }

    // Called under pendingGate.
    private void ScheduleProcessing()
    {
        if (!IsScanComplete || Volatile.Read(ref disposed) != 0) return;
        try { debounceTimer.Change(options.ChangeDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private async Task ProcessPendingChangesAsync()
    {
        var token = cancellation.Token;
        try
        {
            await diskLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            Dictionary<string, WatcherChangeTypes> changes;
            bool rescan;
            lock (pendingGate)
            {
                changes = new(pendingChanges, StringComparer.OrdinalIgnoreCase);
                pendingChanges.Clear();
                rescan = rescanRequested;
                rescanRequested = false;
            }

            var changed = rescan ? Reconcile(token) : ApplyChanges(changes, token);
            if (changed && !token.IsCancellationRequested) RaiseIndexChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The player may have been removed. A later change or the next player choice starts over.
        }
        finally
        {
            diskLock.Release();
        }
    }

    private bool ApplyChanges(Dictionary<string, WatcherChangeTypes> changes, CancellationToken token)
    {
        var changed = false;
        foreach (var (path, change) in changes)
        {
            token.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                // A folder's own "changed" event only means its contents changed; those files have events too.
                if ((change & (WatcherChangeTypes.Created | WatcherChangeTypes.Renamed)) != 0
                    && !AudioFileScanner.ShouldSkipDirectory(new DirectoryInfo(path)))
                {
                    changed |= IndexFolder(path, token);
                }
            }
            else if (new FileInfo(path) is { Exists: true } file)
            {
                if (AudioFileScanner.ShouldIndexFile(file)) changed |= Upsert(file);
            }
            else
            {
                changed |= RemoveUnder(path).Count > 0;
            }
        }

        return changed;
    }

    // Indexes a folder that appeared (or reappeared) and drops entries under it that are gone.
    private bool IndexFolder(string folder, CancellationToken token)
    {
        var changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in AudioFileScanner.EnumerateAudioFiles(folder, token))
        {
            seen.Add(file.FullName);
            changed |= Upsert(file);
        }

        lock (gate)
        {
            var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            foreach (var gone in files.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !seen.Contains(p)).ToArray())
            {
                files.Remove(gone);
                version++;
                changed = true;
            }
        }

        return changed;
    }

    private bool Reconcile(CancellationToken token)
    {
        if (!Directory.Exists(RootPath))
        {
            lock (gate)
            {
                if (files.Count == 0) return false;
                files.Clear();
                version++;
                return true;
            }
        }

        return IndexFolder(RootPath, token);
    }

    private bool Upsert(FileInfo file)
    {
        var path = file.FullName;
        IndexedFile? existing;
        lock (gate) files.TryGetValue(path, out existing);

        if (existing is not null && existing.Size == file.Length && existing.LastWriteTimeUtc == file.LastWriteTimeUtc)
        {
            if (string.Equals(existing.Path, path, StringComparison.Ordinal)) return false;
            return Store(existing with { Path = path });
        }

        return Store(AudioTagReader.Read(file));

        bool Store(IndexedFile entry)
        {
            lock (gate)
            {
                // A newer version may have been stored by a Notify call while this one was being read.
                if (files.TryGetValue(entry.Path, out var current) && current.LastWriteTimeUtc > entry.LastWriteTimeUtc) return false;
                files[entry.Path] = entry;
                version++;
                return true;
            }
        }
    }

    private List<IndexedFile> RemoveUnder(string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        lock (gate)
        {
            var removed = files
                .Where(p => string.Equals(p.Key, path, StringComparison.OrdinalIgnoreCase) || p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .ToList();

            foreach (var file in removed) files.Remove(file.Path);
            if (removed.Count > 0) version++;
            return removed;
        }
    }

    private bool MoveEntries(string oldPath, string newPath)
    {
        var removed = RemoveUnder(oldPath);
        var changed = removed.Count > 0;
        if (!IsUnderRoot(newPath)) return changed;

        foreach (var entry in removed)
        {
            var movedPath = entry.Path.Length == oldPath.Length ? newPath : newPath + entry.Path[oldPath.Length..];
            var file = new FileInfo(movedPath);
            if (file.Exists && AudioFileScanner.ShouldIndexFile(file) && file.Length == entry.Size && file.LastWriteTimeUtc == entry.LastWriteTimeUtc)
            {
                lock (gate)
                {
                    files[movedPath] = entry with { Path = movedPath };
                    version++;
                }
            }
        }

        return changed;
    }

    private bool IsUnderRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void SetProgress(PlayerScanProgress value, bool force)
    {
        progress = value;
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref lastProgressTicks) < (long)options.ProgressInterval.TotalMilliseconds) return;
        Interlocked.Exchange(ref lastProgressTicks, now);
        ProgressChanged?.Invoke(this, value);
    }

    private void RaiseIndexChanged()
    {
        if (Volatile.Read(ref disposed) == 0 && IsScanComplete) IndexChanged?.Invoke(this, EventArgs.Empty);
    }
}
