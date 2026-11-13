using System.Security;
using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Core;

/// <param name="Folder">The folder to sync into, relative to the drive root: the MUSIC folder's own name, or "" for the root.</param>
/// <param name="InMusic">How much of the app's content was found in the MUSIC folder.</param>
/// <param name="AtRoot">How much of the app's content was found outside it.</param>
public readonly record struct MusicFolderChoice(string Folder, int InMusic, int AtRoot)
{
    /// <summary>Whether the app's content was found in both places, so the one with more was chosen.</summary>
    public bool FoundInBoth => InMusic > 0 && AtRoot > 0;
}

/// <summary>
/// Where on a player drive the app keeps its music, decided from what's on the card each time. Many players (Sony
/// Walkman, some SanDisk and Garmin) only read a top-level MUSIC folder, so a card with one and nothing from the app
/// is synced into it. A card the app has already synced to keeps being synced to the same place, so nothing is moved
/// or copied again.
/// </summary>
public static class MusicFolder
{
    /// <summary>
    /// How many audio files to read tags from, at most, in each place. Each read is a few kilobytes, but cards are slow.
    /// </summary>
    private const int MaxTagReads = 20;

    private static readonly EnumerationOptions TopLevel = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>Chooses the folder on a player drive to sync into, without writing anything.</summary>
    /// <param name="driveRoot">The drive's root, e.g. <c>E:\</c>.</param>
    public static MusicFolderChoice Choose(string driveRoot)
    {
        if (FindTopLevelFolder(driveRoot, "MUSIC") is not { } music) return new("", 0, 0);

        var inMusic = CountAppContent(Path.Combine(driveRoot, music), skipFolder: null);
        var atRoot = CountAppContent(driveRoot, skipFolder: music);

        // Ties, including a card with nothing from the app, go to the MUSIC folder, which more players read.
        return new(atRoot > inMusic ? "" : music, inMusic, atRoot);
    }

    /// <summary>The folder to sync into, as a full path.</summary>
    public static string PathFor(string driveRoot, MusicFolderChoice choice) =>
        choice.Folder.Length == 0 ? driveRoot : Path.Combine(driveRoot, choice.Folder);

    /// <summary>The folder directly under <paramref name="root"/> with this name in any case, with its real casing.</summary>
    public static string? FindTopLevelFolder(string root, string name)
    {
        try
        {
            return new DirectoryInfo(root).EnumerateDirectories("*", TopLevel)
                .FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// How much the app has synced to a sync folder: its playlist folders with the app's marker, plus its downloads
    /// (an .m4a with an ISRC tag, the only kind of file it writes) directly in the folder or in an
    /// <c>Artist\Album\</c> folder, reading at most <see cref="MaxTagReads"/> tags.
    /// </summary>
    /// <param name="skipFolder">A folder directly under <paramref name="syncFolder"/> to leave out.</param>
    public static int CountAppContent(string syncFolder, string? skipFolder)
    {
        bool Included(string name) => skipFolder is null || !string.Equals(name, skipFolder, StringComparison.OrdinalIgnoreCase);

        var count = PlaylistFolders.FindPlaylistFolders(syncFolder).Count(Included);

        var reads = 0;
        foreach (var file in CandidateDownloads(syncFolder, Included))
        {
            if (reads++ == MaxTagReads) break;
            if (AudioTagReader.Read(file) is { HasTags: true, Tags.Isrc.Length: > 0 }) count++;
        }

        return count;
    }

    private static IEnumerable<FileInfo> CandidateDownloads(string syncFolder, Func<string, bool> included)
    {
        var root = new DirectoryInfo(syncFolder);
        foreach (var file in Files(root)) yield return file;

        foreach (var artist in Folders(root).Where(d => included(d.Name)))
        foreach (var album in Folders(artist))
        foreach (var file in Files(album))
            yield return file;
    }

    private static List<DirectoryInfo> Folders(DirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateDirectories("*", TopLevel).Where(d => !AudioFileScanner.ShouldSkipDirectory(d)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    private static List<FileInfo> Files(DirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateFiles("*.m4a", TopLevel).Where(AudioFileScanner.ShouldIndexFile).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }
}
