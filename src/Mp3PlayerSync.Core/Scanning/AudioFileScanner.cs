using System.Security;

namespace Mp3PlayerSync.Core.Scanning;

/// <summary>
/// Finds audio files under a folder at any depth, skipping system folders and never following links.
/// </summary>
public static class AudioFileScanner
{
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".oga", ".opus",
        ".wma", ".wav", ".ape", ".aif", ".aiff", ".wv", ".mpc",
    };

    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "RECYCLER", "RECYCLED",
        ".Trashes", ".Spotlight-V100", ".fseventsd", ".TemporaryItems", ".DocumentRevisions-V100", "LOST.DIR",
    };

    // FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS mark cloud placeholders (such as OneDrive
    // online-only files) that would be downloaded if read. They aren't really on the device.
    private const FileAttributes RecallAttributes = (FileAttributes)0x40000 | (FileAttributes)0x400000 | FileAttributes.Offline;

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Whether the file name has an audio extension. macOS "._" resource-fork files are not audio.
    /// </summary>
    public static bool IsAudioFile(string path)
    {
        var name = Path.GetFileName(path);
        return !name.StartsWith("._", StringComparison.Ordinal) && AudioExtensions.Contains(Path.GetExtension(name));
    }

    /// <summary>
    /// Folders the OS or other devices maintain, which never hold the user's music: the recycle bin, System Volume
    /// Information, macOS and Android metadata, and chkdsk's FOUND.000 folders.
    /// </summary>
    public static bool IsSkippedFolderName(string name)
    {
        return SkippedFolderNames.Contains(name)
            || name.StartsWith(".Trash-", StringComparison.OrdinalIgnoreCase)
            || (name.Length > 6 && name.StartsWith("FOUND.", StringComparison.OrdinalIgnoreCase) && name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0);
    }

    /// <summary>
    /// Whether to leave a subfolder out of the scan: a system folder by name, a hidden system folder, or a
    /// junction or symbolic link (which could lead outside the player or back into a folder already scanned).
    /// </summary>
    /// <param name="linkTarget">Gets the link target of a reparse point, or <see langword="null"/> if it's not a link.</param>
    public static bool ShouldSkipDirectory(string name, FileAttributes attributes, Func<string?>? linkTarget = null)
    {
        if (IsSkippedFolderName(name)) return true;
        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) return true;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Junctions and symbolic links have a target. Other reparse points (such as OneDrive folders) are
            // ordinary folders.
            if (linkTarget is null) return true;
            try { return linkTarget() is not null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { return true; }
        }

        return false;
    }

    public static bool ShouldSkipDirectory(DirectoryInfo directory) =>
        ShouldSkipDirectory(directory.Name, directory.Attributes, () => directory.LinkTarget);

    public static bool ShouldIndexFile(FileInfo file) =>
        IsAudioFile(file.Name) && (file.Attributes & RecallAttributes) == 0;

    /// <summary>
    /// Whether a path under <paramref name="root"/> is or is inside a folder skipped by name.
    /// </summary>
    public static bool IsInSkippedFolder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsSkippedFolderName);
    }

    /// <summary>
    /// Lists audio files at any depth under <paramref name="root"/>, folder by folder. Subfolders that can't be read
    /// are skipped.
    /// </summary>
    /// <param name="folderEntered">Called with each folder as the scan reaches it.</param>
    /// <exception cref="DirectoryNotFoundException">The root folder doesn't exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The root folder can't be read.</exception>
    public static IEnumerable<FileInfo> EnumerateAudioFiles(string root, CancellationToken cancellationToken = default, Action<string>? folderEntered = null)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) throw new DirectoryNotFoundException($"The folder \"{root}\" doesn't exist.");

        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            folderEntered?.Invoke(directory.FullName);

            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", EnumerationOptions).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                if (directory == rootInfo) throw;
                continue;
            }

            entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            var subdirectories = new List<DirectoryInfo>();
            foreach (var entry in entries)
            {
                bool include;
                try
                {
                    include = entry switch
                    {
                        DirectoryInfo subdirectory => !ShouldSkipDirectory(subdirectory),
                        FileInfo file => ShouldIndexFile(file),
                        _ => false,
                    };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
                {
                    continue;
                }

                if (!include) continue;
                if (entry is DirectoryInfo d) subdirectories.Add(d);
                else yield return (FileInfo)entry;
            }

            for (var i = subdirectories.Count - 1; i >= 0; i--)
                pending.Push(subdirectories[i]);
        }
    }
}
