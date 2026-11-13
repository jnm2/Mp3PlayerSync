using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Placement;

public enum PlacementChangeKind
{
    /// <summary>A file moved into an album folder, or renamed to widen its number.</summary>
    Moved,

    /// <summary>A file was added: downloaded, or copied from elsewhere on the player (<see cref="PlacementChange.FromRelativePath"/>).</summary>
    Added,

    /// <summary>A file in a playlist folder was renumbered.</summary>
    Renamed,

    /// <summary>A player-only file the user selected for cleanup was deleted.</summary>
    Deleted,
}

/// <param name="FromRelativePath">The file's previous path (moves, renames, deletions) or the player file it was copied from.</param>
/// <param name="PlannedRelativePath">Where the plan wanted the file.</param>
/// <param name="ActualRelativePath">
/// Where the file is now: the planned path, or a " (2)"-suffixed one if the planned name was taken by then.
/// Null when the change failed and the file stayed where it was (or was never added); for a deletion, null on success.
/// </param>
/// <param name="TrackId">The <see cref="PlacementTrack.Id"/> or <see cref="PlaylistTrackPlacement.Id"/> of an added file.</param>
public sealed record PlacementChange(
    PlacementChangeKind Kind,
    string? FromRelativePath,
    string PlannedRelativePath,
    string? ActualRelativePath,
    string? TrackId,
    string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>What a plan changed on the player.</summary>
/// <param name="Changes">
/// Every change attempted, in order. After a cancellation this is exactly what happened before it: a copy that was
/// in progress is rolled back (nothing of it is left on the player) and isn't listed, and nothing after it was tried.
/// </param>
/// <param name="StoppedBecause">
/// Why the remaining files weren't tried, when the player turned out to be full or locked part way through.
/// </param>
public sealed record PlacementReport(IReadOnlyList<PlacementChange> Changes, bool Canceled, string? StoppedBecause = null)
{
    public IEnumerable<PlacementChange> Moved => Succeeded(PlacementChangeKind.Moved);

    public IEnumerable<PlacementChange> Added => Succeeded(PlacementChangeKind.Added);

    public IEnumerable<PlacementChange> Renamed => Succeeded(PlacementChangeKind.Renamed);

    public IEnumerable<PlacementChange> Deleted => Succeeded(PlacementChangeKind.Deleted);

    public IEnumerable<PlacementChange> Failed => Changes.Where(c => !c.Succeeded);

    private IEnumerable<PlacementChange> Succeeded(PlacementChangeKind kind) => Changes.Where(c => c.Succeeded && c.Kind == kind);
}

/// <summary>
/// Applies a <see cref="PlacementPlan"/> or <see cref="PlaylistPlan"/> to the player without ever overwriting a file,
/// writing to the player's flash storage as little as possible.
/// </summary>
/// <remarks>
/// <para>New files must be fully prepared (downloaded, remuxed, tagged) on the PC first. Each one's content is then
/// written to the player exactly once: a single sequential copy, in 1 MiB writes with one flush at the end, to a
/// "~mps-xxxxxx.partial" file in its destination folder (not an audio extension, so the player scan ignores it), which is
/// then renamed to its final name. A file with its final name therefore only exists once it's complete. If the copy
/// or rename fails or is canceled, the partial file is deleted and that track is reported as failed (or, when
/// canceled, left out). Leftover partial files from an interrupted sync are deleted at the start of the next one.
/// Copies to one drive happen one at a time, even across executors.</para>
/// <para>Moves, renumbering and widening are renames on the player's volume, never copies. Files whose name is already
/// right aren't touched. Renumbering renames each file directly to its new name, and only when numbers have to be
/// swapped parks one file of the cycle under "~mps-xxxxxx &lt;its name&gt;" first. Parked files keep their tags and audio
/// extension, so if a sync is interrupted, a rescan still sees them and <see cref="PlaylistPlanner"/> renames them
/// into place next time. If a destination name has been taken since planning, a " (2)", " (3)", … suffix is used.</para>
/// </remarks>
public sealed partial class PlacementExecutor
{
    private const int MaxCopyNumber = 1000;

    private const int CopyBufferSize = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriterGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string root;

    public PlacementExecutor(string playerRoot)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(playerRoot));
    }

    /// <summary>
    /// Whether to delete each downloaded (local) file once it has been copied to the player. Files copied from
    /// elsewhere on the player are never deleted.
    /// </summary>
    public bool DeleteDownloadsAfterCopy { get; init; }

    // How each file to copy is opened; replaceable, e.g. to cancel in the middle of a copy.
    internal Func<string, Stream> OpenSource { get; init; } = path => new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = 0,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    });

    /// <summary>
    /// Deletes partial copies left on the player by an interrupted sync. Only reads the player when there are none.
    /// Both ExecuteAsync overloads do this first.
    /// </summary>
    public void RemoveLeftoverPartialCopies()
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, TrackNumbering.TemporaryPrefixStart + "*.partial", options))
            {
                if (PartialCopyName().IsMatch(Path.GetFileName(file))) TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Applies the moves, then adds the new files. Stops early (with <see cref="PlacementReport.Canceled"/> set) on cancellation.</summary>
    /// <param name="sourcePathFor">The local file to copy for each new track, or null if it isn't available.</param>
    public async Task<PlacementReport> ExecuteAsync(
        PlacementPlan plan,
        Func<PlacementTrack, string?> sourcePathFor,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<PlacementChange>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveLeftoverPartialCopies();

            foreach (var move in plan.Moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changes.Add(Move(move));
            }

            foreach (var newFile in plan.NewFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changes.Add(sourcePathFor(newFile.Track) is { } source
                    ? await AddAsync(source, newFile.RelativePath, null, newFile.Track.Id, DeleteDownloadsAfterCopy, cancellationToken).ConfigureAwait(false)
                    : AddFailure(newFile.RelativePath, newFile.Track.Id, null, "No downloaded file is available for this track."));
                if (DeviceProblem(changes[^1]) is { } problem) return new PlacementReport(changes, Canceled: false, problem);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PlacementReport(changes, Canceled: true);
        }

        return new PlacementReport(changes, Canceled: false);
    }

    /// <summary>
    /// Deletes the player-only files selected for cleanup, renumbers, then adds the missing tracks: copies of files
    /// elsewhere on the player, and downloads. Stops early (with <see cref="PlacementReport.Canceled"/> set) on
    /// cancellation, but never in the middle of renumbering.
    /// </summary>
    /// <param name="downloadedPathFor">The local file for each <see cref="PlaylistTrackState.Download"/> track, or null if it didn't download.</param>
    public async Task<PlacementReport> ExecuteAsync(
        PlaylistPlan plan,
        Func<PlaylistTrackPlacement, string?> downloadedPathFor,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<PlacementChange>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveLeftoverPartialCopies();

            foreach (var file in plan.Deletions)
                changes.Add(Delete(file.RelativePath));

            changes.AddRange(Rename(plan.Renames));

            foreach (var track in plan.Tracks.Where(t => t.State is PlaylistTrackState.CopyFromPlayer or PlaylistTrackState.Download))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (track.State == PlaylistTrackState.CopyFromPlayer)
                {
                    var from = track.CopySourceRelativePath!;
                    changes.Add(TryResolve(from) is { } source && File.Exists(source)
                        ? await AddAsync(source, track.TargetRelativePath, from, track.Id, deleteSource: false, cancellationToken).ConfigureAwait(false)
                        : AddFailure(track.TargetRelativePath, track.Id, from, "The file to copy is no longer on the player."));
                }
                else
                {
                    changes.Add(downloadedPathFor(track) is { } source
                        ? await AddAsync(source, track.TargetRelativePath, null, track.Id, DeleteDownloadsAfterCopy, cancellationToken).ConfigureAwait(false)
                        : AddFailure(track.TargetRelativePath, track.Id, null, "No downloaded file is available for this track."));
                }

                // A full or locked player fails every other copy too, so don't try them.
                if (DeviceProblem(changes[^1]) is { } problem) return new PlacementReport(changes, Canceled: false, problem);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PlacementReport(changes, Canceled: true);
        }

        return new PlacementReport(changes, Canceled: false);
    }

    public PlacementChange Move(PlannedMove move)
    {
        try
        {
            var from = Resolve(move.FromRelativePath);
            if (!File.Exists(from))
                return new(PlacementChangeKind.Moved, move.FromRelativePath, move.ToRelativePath, null, null, "The file is no longer on the player.");

            var actual = MoveToFreeName(from, move.ToRelativePath);
            return new PlacementChange(PlacementChangeKind.Moved, move.FromRelativePath, move.ToRelativePath, actual, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(PlacementChangeKind.Moved, move.FromRelativePath, move.ToRelativePath, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Renames files to their new names. A file is renamed once its new name is no longer held by another file
    /// still to be renamed; when names form a cycle (a swap), one file is parked under a temporary name first.
    /// </summary>
    public IReadOnlyList<PlacementChange> Rename(IReadOnlyList<PlannedRename> renames)
    {
        var changes = new PlacementChange?[renames.Count];
        var pending = renames.Select((r, i) => (Index: i, Current: PlayerPathNames.NormalizeRelativePath(r.FromRelativePath), Parked: false)).ToList();

        while (pending.Count > 0)
        {
            var progressed = false;
            foreach (var item in pending.ToList())
            {
                var target = PlayerPathNames.NormalizeRelativePath(renames[item.Index].ToRelativePath);
                if (pending.Any(other => other.Index != item.Index && string.Equals(other.Current, target, StringComparison.OrdinalIgnoreCase))) continue;

                changes[item.Index] = RenameNow(renames[item.Index], item.Current, item.Parked);
                pending.Remove(item);
                progressed = true;
            }

            if (progressed) continue;

            // Every remaining file's new name is held by another: park one so the rest can move.
            var first = pending[0];
            var rename = renames[first.Index];
            try
            {
                var from = Resolve(first.Current);
                if (!File.Exists(from))
                {
                    changes[first.Index] = new(PlacementChangeKind.Renamed, rename.FromRelativePath, rename.ToRelativePath, null, null, "The file is no longer on the player.");
                    pending.RemoveAt(0);
                    continue;
                }

                var folder = PlayerPathNames.GetFolder(first.Current);
                var parkedName = TrackNumbering.AddTemporaryPrefix(Path.GetFileName(first.Current));
                pending[0] = first with { Current = MoveToFreeName(from, folder.Length > 0 ? folder + PlayerPathNames.Separator + parkedName : parkedName), Parked = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                changes[first.Index] = new(PlacementChangeKind.Renamed, rename.FromRelativePath, rename.ToRelativePath, null, null, ex.Message);
                pending.RemoveAt(0);
            }
        }

        return changes.Select(c => c!).ToList();
    }

    private PlacementChange RenameNow(PlannedRename rename, string current, bool parked)
    {
        try
        {
            var from = Resolve(current);
            if (!File.Exists(from))
                return new(PlacementChangeKind.Renamed, rename.FromRelativePath, rename.ToRelativePath, null, null, "The file is no longer on the player.");

            var actual = MoveToFreeName(from, rename.ToRelativePath);
            return new(PlacementChangeKind.Renamed, rename.FromRelativePath, rename.ToRelativePath, actual, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A parked file stays parked; the next sync renames it into place.
            return new(PlacementChangeKind.Renamed, rename.FromRelativePath, rename.ToRelativePath, parked ? current : null, null, ex.Message);
        }
    }

    public PlacementChange Delete(string relativePath)
    {
        try
        {
            var path = Resolve(relativePath);
            if (!File.Exists(path))
                return new(PlacementChangeKind.Deleted, relativePath, relativePath, null, null, "The file is no longer on the player.");

            File.Delete(path);
            return new(PlacementChangeKind.Deleted, relativePath, relativePath, null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(PlacementChangeKind.Deleted, relativePath, relativePath, relativePath, null, ex.Message);
        }
    }

    /// <exception cref="OperationCanceledException">Canceled; nothing of the file is left on the player.</exception>
    public Task<PlacementChange> AddAsync(PlannedNewFile newFile, string sourcePath, CancellationToken cancellationToken = default) =>
        AddAsync(sourcePath, newFile.RelativePath, null, newFile.Track.Id, DeleteDownloadsAfterCopy, cancellationToken);

    private async Task<PlacementChange> AddAsync(string sourcePath, string relativePath, string? fromRelativePath, string trackId, bool deleteSource, CancellationToken cancellationToken)
    {
        var gate = WriterGates.GetOrAdd(Path.GetPathRoot(root) is { Length: > 0 } drive ? drive : root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? temporary = null;
        try
        {
            var destination = Resolve(relativePath);
            var folder = Path.GetDirectoryName(destination)!;

            await using (var input = OpenSource(sourcePath))
            {
                Directory.CreateDirectory(folder);
                temporary = Path.Combine(folder, $"{TrackNumbering.TemporaryPrefixStart}{Random.Shared.Next(0x1000000):x6}.partial");
                await using var output = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 0,
                    Options = FileOptions.Asynchronous,
                    PreallocationSize = input.CanSeek ? input.Length : 0,
                });
                await input.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var actual = MoveToFreeName(temporary, relativePath);
            temporary = null;
            if (deleteSource) TryDelete(sourcePath);
            return new PlacementChange(PlacementChangeKind.Added, fromRelativePath, relativePath, actual, trackId, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return AddFailure(relativePath, trackId, fromRelativePath, DeviceProblemMessage(ex) ?? ex.Message);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
            gate.Release();
        }
    }

    public const string PlayerFullMessage = "The player is full.";
    public const string PlayerLockedMessage = "This card is locked";

    /// <summary>The message for an error that means no file can be written to the player at all, if it's one.</summary>
    private static string? DeviceProblemMessage(Exception ex) => (ex.HResult & 0xFFFF0000) != 0x80070000 ? null : (ex.HResult & 0xFFFF) switch
    {
        0x70 or 0x27 => PlayerFullMessage, // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        0x13 => PlayerLockedMessage, // ERROR_WRITE_PROTECT
        _ => null,
    };

    private static string? DeviceProblem(PlacementChange change) =>
        change.Error is PlayerFullMessage or PlayerLockedMessage ? change.Error : null;

    private string MoveToFreeName(string fullSourcePath, string plannedRelativePath)
    {
        for (var copy = 1; copy <= MaxCopyNumber; copy++)
        {
            var relative = WithCopyNumber(PlayerPathNames.NormalizeRelativePath(plannedRelativePath), copy);
            var destination = Resolve(relative);

            // The same file under another case of its name.
            if (string.Equals(destination, fullSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(destination, fullSourcePath, StringComparison.Ordinal)) File.Move(fullSourcePath, destination);
                return relative;
            }

            if (File.Exists(destination) || Directory.Exists(destination)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Move(fullSourcePath, destination, overwrite: false);
                return relative;
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                // Taken between the check and the move; try the next name.
            }
        }

        throw new IOException($"No free name was found for {plannedRelativePath}.");
    }

    private static string WithCopyNumber(string relativePath, int copy)
    {
        if (copy == 1) return relativePath;

        var folder = PlayerPathNames.GetFolder(relativePath);
        var name = Path.GetFileNameWithoutExtension(relativePath) + string.Create(CultureInfo.InvariantCulture, $" ({copy})") + Path.GetExtension(relativePath);
        return folder.Length > 0 ? folder + PlayerPathNames.Separator + name : name;
    }

    private string Resolve(string relativePath) =>
        TryResolve(relativePath) ?? throw new ArgumentException($"{relativePath} is outside the player.", nameof(relativePath));

    private string? TryResolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, PlayerPathNames.NormalizeRelativePath(relativePath)));
        // A drive root like "E:\" already ends with a separator.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static PlacementChange AddFailure(string relativePath, string trackId, string? fromRelativePath, string error) =>
        new(PlacementChangeKind.Added, fromRelativePath, relativePath, null, trackId, error);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^~mps-[0-9a-f]{6}\.partial$")]
    private static partial Regex PartialCopyName();
}
