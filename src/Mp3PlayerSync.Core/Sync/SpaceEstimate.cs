namespace Mp3PlayerSync.Core.Sync;

/// <summary>How far short of space a sync would be.</summary>
/// <param name="SongsThatWontFit">How many songs, at the end of the sync, there's no room for.</param>
/// <param name="MissingBytes">How much more free space the whole sync needs.</param>
public sealed record SpaceShortfall(int SongsThatWontFit, long MissingBytes);

/// <summary>
/// Estimates how much space a sync needs on the player, before anything is downloaded.
/// </summary>
public static class SpaceEstimate
{
    /// <summary>YouTube Music's AAC is about 128 kbps (256 kbps with Premium); estimate on the generous side.</summary>
    public const double DownloadKbps = 160;

    /// <summary>Tags, cover art and the file system's own overhead, per file.</summary>
    public const long PerFileBytes = 256 * 1024;

    /// <summary>Space to leave free, so the card is never filled to the last byte.</summary>
    public const long MarginBytes = 16L * 1024 * 1024;

    public static readonly TimeSpan UnknownDuration = TimeSpan.FromMinutes(4);

    /// <summary>The estimated size of a song once downloaded and tagged.</summary>
    public static long ForDownload(TimeSpan? duration) =>
        (long)Math.Ceiling((duration is { } d && d > TimeSpan.Zero ? d : UnknownDuration).TotalSeconds * DownloadKbps * 1000 / 8) + PerFileBytes;

    /// <summary>
    /// Whether the songs fit, in the order they're added, with the space files being removed first freed up.
    /// </summary>
    /// <param name="songSizes">The size of each song to add (estimated for downloads, actual for copies).</param>
    /// <returns>Null if everything fits.</returns>
    public static SpaceShortfall? Check(IReadOnlyList<long> songSizes, long freeBytes, long freedBytes = 0)
    {
        var available = freeBytes + freedBytes - MarginBytes;
        var total = songSizes.Sum();
        if (total <= available) return null;

        var used = 0L;
        var fit = 0;
        foreach (var size in songSizes)
        {
            if (used + size > available) break;
            used += size;
            fit++;
        }
        return new SpaceShortfall(songSizes.Count - fit, total - Math.Max(available, 0));
    }

    /// <summary>For example "3 songs won't fit. 42 MB more space is needed."</summary>
    public static string Describe(SpaceShortfall shortfall) =>
        $"{shortfall.SongsThatWontFit} {(shortfall.SongsThatWontFit == 1 ? "song won't" : "songs won't")} fit. {Players.FormatBytes(shortfall.MissingBytes)} more space is needed.";
}
