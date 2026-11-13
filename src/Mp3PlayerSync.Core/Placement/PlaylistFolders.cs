using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Core.Placement;

/// <summary>
/// The hidden marker file in a playlist folder, recording which Spotify playlist owns the folder. It travels with
/// the player, so ownership survives app restarts, other PCs and the playlist being renamed on Spotify.
/// </summary>
/// <param name="PlaylistId">The playlist's stable ID; see <see cref="PlaylistIdFromKey"/>.</param>
/// <param name="Name">The playlist's name when the folder was last synced (informational).</param>
/// <param name="Owner">The playlist owner's Spotify user ID.</param>
public sealed record PlaylistFolderMarker(string PlaylistId, string Name, string Owner)
{
    /// <summary>Not an audio extension, so the player scan never indexes it.</summary>
    public const string FileName = ".mp3playersync-playlist.json";

    /// <summary>Stands for a marker that couldn't be read: the folder belongs to some playlist, but not a known one.</summary>
    public static PlaylistFolderMarker Unreadable { get; } = new("", "", "");

    /// <summary>
    /// The stable ID for a library item key (the playlist's link): e.g. "spotify:playlist:37i9…" or
    /// "spotify:collection:tracks" for Liked Songs. Query strings and locale prefixes in the link are ignored.
    /// </summary>
    public static string PlaylistIdFromKey(string key)
    {
        key = key.Trim();
        if (Uri.TryCreate(key, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("spotify.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase)))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2) return $"spotify:{segments[^2]}:{segments[^1]}";
        }

        return key;
    }

    /// <summary>Reads the marker in a folder: null when there is none, <see cref="Unreadable"/> when it's corrupt.</summary>
    public static PlaylistFolderMarker? Read(string folderPath)
    {
        var path = Path.Combine(folderPath, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var marker = JsonSerializer.Deserialize(File.ReadAllText(path), PlaylistFolderMarkerJsonContext.Default.PlaylistFolderMarker);
            return marker is { PlaylistId.Length: > 0 } ? marker with { Name = marker.Name ?? "", Owner = marker.Owner ?? "" } : Unreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return File.Exists(path) ? Unreadable : null;
        }
    }

    /// <summary>Writes the marker into a folder as a hidden file, unless it's already there unchanged.</summary>
    public void Write(string folderPath)
    {
        if (Read(folderPath) == this) return;

        var path = Path.Combine(folderPath, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, PlaylistFolderMarkerJsonContext.Default.PlaylistFolderMarker));
        File.SetAttributes(temporary, FileAttributes.Hidden);
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>A folder directly under the player root.</summary>
/// <param name="Marker">Its playlist marker, if it has one.</param>
public sealed record PlayerRootFolder(string Name, PlaylistFolderMarker? Marker, bool HasSubfolders);

/// <summary>Where a playlist's folder is on the player, and where it will be after the sync.</summary>
/// <param name="CurrentRelativePath">The folder the playlist's files are in now. Plan and execute the sync in this folder.</param>
/// <param name="FinalRelativePath">
/// The folder's name after <see cref="PlaylistFolders.FinishRename"/>: different from <see cref="CurrentRelativePath"/> when
/// the playlist was renamed on Spotify.
/// </param>
/// <param name="Marker">The marker <see cref="PlaylistFolders.Claim"/> writes.</param>
/// <param name="Exists">Whether the folder is already on the player (owned by this playlist, or adopted).</param>
public sealed record PlaylistFolderAssignment(string CurrentRelativePath, string FinalRelativePath, PlaylistFolderMarker Marker, bool Exists)
{
    public bool NeedsRename => !string.Equals(CurrentRelativePath, FinalRelativePath, StringComparison.Ordinal);
}

/// <summary>
/// Gives each playlist its own folder at the player root, tracked by the hidden <see cref="PlaylistFolderMarker"/>.
/// </summary>
/// <remarks>
/// <para>A playlist's folder is the root folder whose marker has its ID. With none, a new folder is named after the
/// playlist; if that name is taken, by another playlist's folder or anything else, it gets the owner appended
/// ("Name (owner)") when the other playlist has a different owner, and otherwise " (2)", " (3)", ….</para>
/// <para>A folder named after the playlist without any marker (from before markers existed) is adopted, unless it has
/// subfolders (such as an artist's folder of albums). A folder with another playlist's marker, or an unreadable
/// one, is never used.</para>
/// <para>When the playlist is renamed on Spotify, its folder is renamed to match, picking a free name the same way.
/// A folder whose name already fits the playlist's name (including a disambiguated one) keeps it.</para>
/// <para>Sync sequence: <see cref="Assign(string, string, string, string, int)"/>, plan in
/// <see cref="PlaylistFolderAssignment.CurrentRelativePath"/> (<see cref="PlaylistPlanner.Plan(PlaylistFolderAssignment, IReadOnlyList{ExportedTrack}, string, IEnumerable{IndexedFile}, IEnumerable{string}?, string, int)"/>),
/// <see cref="Claim"/>, execute, then <see cref="FinishRename"/>. If interrupted, the marker is already in place and the
/// next sync finds the folder and finishes the rename.</para>
/// </remarks>
public static partial class PlaylistFolders
{
    // Leaves room for a reasonable file name inside the folder.
    private static readonly PathPiece FileNameAllowance = new(new string('x', 40), IsFixed: true);

    /// <summary>Reads the player root's folders and their markers, and assigns the playlist's folder.</summary>
    public static PlaylistFolderAssignment Assign(
        string playerRoot,
        string playlistKey,
        string playlistName,
        string owner,
        int maxRelativePathLength = PlayerPathNames.DefaultMaxRelativePathLength)
    {
        var folders = ReadRootFolders(playerRoot, out var fileNames);
        return Assign(playlistKey, playlistName, owner, folders, fileNames, maxRelativePathLength);
    }

    /// <summary>Assigns the playlist's folder, given what's at the player root.</summary>
    /// <param name="playlistKey">The library item key (the playlist's link), or an ID from <see cref="PlaylistFolderMarker.PlaylistIdFromKey"/>.</param>
    /// <param name="rootFileNames">The names of files directly in the player root, which folders can't take.</param>
    public static PlaylistFolderAssignment Assign(
        string playlistKey,
        string playlistName,
        string owner,
        IReadOnlyList<PlayerRootFolder> rootFolders,
        IEnumerable<string>? rootFileNames = null,
        int maxRelativePathLength = PlayerPathNames.DefaultMaxRelativePathLength)
    {
        var id = PlaylistFolderMarker.PlaylistIdFromKey(playlistKey);
        var marker = new PlaylistFolderMarker(id, playlistName, owner);
        var baseName = PlayerPathNames.SanitizeComponent(playlistName, "Playlist");
        var fileNames = new HashSet<string>(rootFileNames ?? [], StringComparer.OrdinalIgnoreCase);

        string Candidate(string suffix) =>
            PlayerPathNames.GetFolder(PlayerPathNames.Join([new PathPiece(baseName, suffix), FileNameAllowance], maxRelativePathLength));

        var ownerSuffix = owner.Trim().Length > 0 ? $" ({PlayerPathNames.SanitizeComponent(owner)})" : null;

        // Whether a folder name is one this playlist's name could have been given.
        bool Fits(string name, out string exact)
        {
            exact = name;
            var suffixes = new List<string> { "" };
            if (ownerSuffix is not null) suffixes.Add(ownerSuffix);
            if (CopyNumberSuffix().Match(name) is { Success: true } copy) suffixes.Add(copy.Value);
            foreach (var suffix in suffixes)
            {
                var candidate = Candidate(suffix);
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    exact = candidate;
                    return true;
                }
            }

            return false;
        }

        string FirstFree(PlayerRootFolder? current)
        {
            bool IsFree(string name) =>
                !fileNames.Contains(name)
                && !rootFolders.Any(f => f != current && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

            var first = Candidate("");
            if (IsFree(first)) return first;

            var occupant = rootFolders.FirstOrDefault(f => f != current && string.Equals(f.Name, first, StringComparison.OrdinalIgnoreCase));
            if (ownerSuffix is not null && occupant?.Marker is { PlaylistId.Length: > 0 } other
                && !string.Equals(other.Owner.Trim(), owner.Trim(), StringComparison.OrdinalIgnoreCase)
                && Candidate(ownerSuffix) is var withOwner && IsFree(withOwner))
                return withOwner;

            for (var copy = 2; ; copy++)
            {
                var numbered = Candidate($" ({copy})");
                if (IsFree(numbered)) return numbered;
            }
        }

        var owned = rootFolders
            .Where(f => f.Marker is { } m && m.PlaylistId.Length > 0 && string.Equals(m.PlaylistId, id, StringComparison.Ordinal))
            .OrderBy(f => Fits(f.Name, out _) ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (owned is not null)
        {
            var final = Fits(owned.Name, out var exact) && !IsTakenByOther(exact, owned) ? exact : FirstFree(owned);
            return new PlaylistFolderAssignment(owned.Name, final, marker, Exists: true);
        }

        var legacyName = Candidate("");
        var legacy = rootFolders.FirstOrDefault(f => f.Marker is null && !f.HasSubfolders && string.Equals(f.Name, legacyName, StringComparison.OrdinalIgnoreCase));
        if (legacy is not null)
            return new PlaylistFolderAssignment(legacy.Name, legacyName, marker, Exists: true);

        var name = FirstFree(null);
        return new PlaylistFolderAssignment(name, name, marker, Exists: false);

        bool IsTakenByOther(string name, PlayerRootFolder current) =>
            fileNames.Contains(name) || rootFolders.Any(f => f != current && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The folders directly under the player root, with their markers. System folders are left out.</summary>
    public static IReadOnlyList<PlayerRootFolder> ReadRootFolders(string playerRoot, out IReadOnlyList<string> rootFileNames)
    {
        var folders = new List<PlayerRootFolder>();
        var files = new List<string>();
        rootFileNames = files;
        if (!Directory.Exists(playerRoot)) return folders;

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var entry in new DirectoryInfo(playerRoot).EnumerateFileSystemInfos("*", options))
        {
            if (entry is FileInfo)
            {
                files.Add(entry.Name);
                continue;
            }

            bool hasSubfolders;
            try
            {
                hasSubfolders = Directory.EnumerateDirectories(entry.FullName, "*", options).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hasSubfolders = true;
            }

            folders.Add(new PlayerRootFolder(entry.Name, PlaylistFolderMarker.Read(entry.FullName), hasSubfolders));
        }

        return folders;
    }

    /// <summary>The playlist folders on the player: root folders with a marker (readable or not), as relative paths.</summary>
    public static IReadOnlyList<string> FindPlaylistFolders(string playerRoot) =>
        ReadRootFolders(playerRoot, out _).Where(f => f.Marker is not null).Select(f => f.Name).ToList();

    /// <summary>
    /// Creates the playlist's current folder if needed and writes its marker, claiming it. Call before putting files in it.
    /// </summary>
    /// <exception cref="IOException">The folder has become another playlist's since it was assigned.</exception>
    public static void Claim(string playerRoot, PlaylistFolderAssignment folder)
    {
        var path = Resolve(playerRoot, folder.CurrentRelativePath);
        Directory.CreateDirectory(path);
        if (PlaylistFolderMarker.Read(path) is { } existing && existing.PlaylistId != folder.Marker.PlaylistId)
            throw new IOException($"The folder {folder.CurrentRelativePath} belongs to another playlist.");

        folder.Marker.Write(path);
    }

    /// <summary>
    /// Renames the folder to <see cref="PlaylistFolderAssignment.FinalRelativePath"/> if needed. Never overwrites or
    /// merges: when that name has been taken since, the folder keeps its current name.
    /// </summary>
    /// <returns>The folder's relative path afterwards. When it differs from the current one, tell the player index about the move.</returns>
    public static string FinishRename(string playerRoot, PlaylistFolderAssignment folder)
    {
        if (!folder.NeedsRename) return folder.CurrentRelativePath;

        var from = Resolve(playerRoot, folder.CurrentRelativePath);
        var to = Resolve(playerRoot, folder.FinalRelativePath);
        var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(from) || (!caseOnly && (Directory.Exists(to) || File.Exists(to)))) return folder.CurrentRelativePath;

        try
        {
            Directory.Move(from, to);
            return folder.FinalRelativePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keeps its name; the next sync tries again.
            return folder.CurrentRelativePath;
        }
    }

    private static string Resolve(string playerRoot, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(playerRoot));
        var full = Path.GetFullPath(Path.Combine(root, PlayerPathNames.NormalizeRelativePath(relativePath)));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new ArgumentException($"{relativePath} is outside the player.", nameof(relativePath));
    }

    [GeneratedRegex(@" \([0-9]+\)$")]
    private static partial Regex CopyNumberSuffix();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(PlaylistFolderMarker))]
internal sealed partial class PlaylistFolderMarkerJsonContext : JsonSerializerContext;
