using System.Globalization;

namespace Mp3PlayerSync.Core;

public enum StorageBus { Unknown, Usb, SecureDigital, Mmc, Other }

/// <summary>
/// A mounted drive and what Windows reports about the device behind it.
/// </summary>
/// <param name="RootPath">For example <c>E:\</c>.</param>
/// <param name="VolumeId">The volume GUID path (<c>\\?\Volume{…}\</c>), which stays the same whatever letter the drive gets.</param>
public sealed record DriveCandidate(
    string RootPath,
    string Label,
    DriveType DriveType,
    StorageBus Bus,
    bool IsSystemDrive,
    string? VolumeId,
    uint? VolumeSerial,
    long TotalBytes,
    long FreeBytes,
    bool IsWriteProtected = false);

public enum PlayerOptionKind { Drive, Folder, OtherFolder }

public sealed record PlayerOption(PlayerOptionKind Kind, string Title, string Subtitle, string Detail, DriveCandidate? Drive = null, string? FolderPath = null)
{
    public string Glyph => Kind switch
    {
        PlayerOptionKind.Drive when Drive?.Bus is StorageBus.SecureDigital or StorageBus.Mmc => "\uE7F1",
        PlayerOptionKind.Drive when Drive?.DriveType == DriveType.Fixed => "\uEDA2",
        PlayerOptionKind.Drive => "\uE88E",
        PlayerOptionKind.Folder => "\uE8B7",
        _ => "\uE838",
    };

    /// <summary>Whether to show how full the drive is: only for whole drives whose size is known.</summary>
    public bool ShowsUsage => Drive is { TotalBytes: > 0 };

    /// <summary>How much of the drive is used, from 0 to 1.</summary>
    public double UsedFraction => Drive is { TotalBytes: > 0 } drive
        ? Math.Clamp((drive.TotalBytes - drive.FreeBytes) / (double)drive.TotalBytes, 0, 1)
        : 0;

    /// <summary>Like File Explorer, a drive that's 90% full or more gets the warning color.</summary>
    public bool IsNearlyFull => ShowsUsage && UsedFraction >= 0.9;

    /// <summary>How the chosen player is named once chosen: <c>SANDISK (E:)</c>, or <c>Folder (D:\Music)</c>.</summary>
    public string DisplayName => Drive is { } drive ? $"{Title} ({drive.RootPath.TrimEnd('\\')})"
        : FolderPath is { } folder ? $"Folder ({folder})"
        : Title;

    /// <summary>Like <see cref="DisplayName"/>, with a long folder path shortened in the middle: <c>Folder (C:\…\Music\Player)</c>.</summary>
    public string ShortDisplayName => Drive is null && FolderPath is { } folder ? $"Folder ({Players.ShortenPath(folder)})" : DisplayName;

    /// <summary>The tile's subtitle, with a folder's path shortened in the middle to fit the tile.</summary>
    public string ShortSubtitle => Subtitle == FolderPath ? Players.ShortenPath(Subtitle, TileSubtitleLength) : Subtitle;

    /// <summary>The full path, as a tooltip, when <see cref="ShortSubtitle"/> shortened it.</summary>
    public string? SubtitleToolTip => ShortSubtitle != Subtitle ? Subtitle : null;

    // What fits on one line of a player tile's caption
    private const int TileSubtitleLength = 30;

    // Used by XAML item containers as the accessible name.
    public override string ToString() => Title;
}

/// <summary>
/// The MP3 player chosen last time: a drive, remembered by its volume rather than its letter, or a folder.
/// </summary>
public sealed record PlayerChoice(string? VolumeId = null, uint? VolumeSerial = null, string? Label = null, string? FolderPath = null);

public static class Players
{
    /// <summary>
    /// Whether a drive can be an MP3 player or memory card: removable media, or a fixed disk on a USB, SD or MMC bus
    /// (some players and card readers report themselves as fixed). Never the Windows drive, network or optical drives.
    /// </summary>
    public static bool IsPlayer(DriveCandidate drive) => !drive.IsSystemDrive && drive.DriveType switch
    {
        DriveType.Removable => true,
        DriveType.Fixed => drive.Bus is StorageBus.Usb or StorageBus.SecureDigital or StorageBus.Mmc,
        _ => false,
    };

    /// <summary>
    /// The players to offer: player drives, then the folder chosen last time if it still exists, then "Other folder".
    /// </summary>
    /// <returns>The options and which one was chosen last time, if it's available.</returns>
    public static (List<PlayerOption> Options, PlayerOption? Remembered) BuildOptions(
        IEnumerable<DriveCandidate> drives, PlayerChoice? remembered, Func<string, bool> folderExists)
    {
        var options = drives
            .Where(IsPlayer)
            .OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(d => new PlayerOption(PlayerOptionKind.Drive, DriveTitle(d), $"Drive {d.RootPath.TrimEnd('\\')}", SpaceText(d), Drive: d))
            .ToList();

        PlayerOption? match = remembered is null ? null
            : options.FirstOrDefault(o => remembered.VolumeId is not null && string.Equals(o.Drive!.VolumeId, remembered.VolumeId, StringComparison.OrdinalIgnoreCase))
              // The volume GUID differs on another PC; the same card still has its serial number and label.
              ?? options.FirstOrDefault(o => remembered.VolumeSerial is { } serial and not 0 && o.Drive!.VolumeSerial == serial && o.Drive.Label == (remembered.Label ?? ""));

        if (remembered?.FolderPath is { Length: > 0 } folder && folderExists(folder))
        {
            match = ForFolder(folder, options);
            if (match.Kind == PlayerOptionKind.Folder) options.Add(match);
        }

        options.Add(new PlayerOption(PlayerOptionKind.OtherFolder, "Other folder", "Choose any folder", ""));
        return (options, match);
    }

    /// <summary>
    /// The player for a chosen folder: the player drive itself when the folder is the root of one of
    /// <paramref name="options"/>' drives, otherwise the folder.
    /// </summary>
    public static PlayerOption ForFolder(string folder, IEnumerable<PlayerOption> options)
    {
        if (IsDriveRoot(folder))
        {
            var root = Path.GetPathRoot(folder)!;
            if (options.FirstOrDefault(o => o.Drive is { } d && SameRoot(d.RootPath, root)) is { } drive)
                return drive;

            var title = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return new PlayerOption(PlayerOptionKind.Folder, title, "Recent folder", "", FolderPath: folder);
        }

        return new PlayerOption(PlayerOptionKind.Folder, FolderTitle(folder), folder, "", FolderPath: folder);
    }

    /// <summary>
    /// Where the folder dialog opens: the folder player in use, otherwise the folder picked last time, if it still
    /// exists. Null lets Windows choose.
    /// </summary>
    public static string? DialogStartFolder(string? playerFolder, string? lastPicked, Func<string, bool> folderExists) =>
        new[] { playerFolder, lastPicked }.FirstOrDefault(f => f is { Length: > 0 } && folderExists(f));

    private static bool IsDriveRoot(string folder) =>
        Path.GetPathRoot(folder) is { Length: > 0 } root && SameRoot(root, folder);

    private static bool SameRoot(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static PlayerChoice ChoiceFor(PlayerOption option) => option.Drive is { } drive
        ? new PlayerChoice(drive.VolumeId, drive.VolumeSerial, drive.Label)
        : new PlayerChoice(FolderPath: option.FolderPath);

    /// <summary>
    /// Shortens a path longer than <paramref name="maxLength"/> by replacing folders in the middle with "…", keeping the
    /// drive and as many of the last folders as fit, and always the last one: <c>C:\…\Desktop\mp3player</c>.
    /// </summary>
    public static string ShortenPath(string path, int maxLength = 40)
    {
        if (path.Length <= maxLength) return path;

        var root = Path.GetPathRoot(path) ?? "";
        var folders = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (folders.Length < 2) return path;

        var prefix = (Path.EndsInDirectorySeparator(root) || root.Length == 0 ? root : root + '\\') + "…\\";
        var kept = folders[^1];
        for (var i = folders.Length - 2; i > 0; i--)
        {
            var longer = folders[i] + '\\' + kept;
            if (prefix.Length + longer.Length > maxLength) break;
            kept = longer;
        }
        return prefix + kept;
    }

    public static string FormatBytes(long bytes)
    {
        const double GB = 1024d * 1024 * 1024, MB = 1024d * 1024;
        return bytes >= GB
            ? (bytes / GB).ToString("0.#", CultureInfo.CurrentCulture) + " GB"
            : (bytes / MB).ToString("0", CultureInfo.CurrentCulture) + " MB";
    }

    private static string DriveTitle(DriveCandidate drive) =>
        drive.Label is { Length: > 0 } label ? label
        : drive.Bus is StorageBus.SecureDigital or StorageBus.Mmc ? "Memory card"
        : "USB drive";

    public const string LockedText = "This card is locked";

    private static string SpaceText(DriveCandidate drive) =>
        drive.IsWriteProtected ? LockedText
        : drive.TotalBytes > 0 ? $"{FormatBytes(drive.FreeBytes)} free of {FormatBytes(drive.TotalBytes)}"
        : "";

    private static string FolderTitle(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
}
