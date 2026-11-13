namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// How many YouTube lookups and downloads run at once, worked out once at startup from the PC's logical processors
/// and free memory, so a sync uses about half of the PC and leaves the rest to the user. Lookups and downloads share
/// one processor budget and one cap: <see cref="MaxParallelLookupsAndDownloads"/> limits them together.
/// </summary>
public sealed record DownloadConcurrency(int MaxParallelLookups, int MaxParallelDownloads, int MaxParallelLookupsAndDownloads)
{
    /// <summary>The most lookups and downloads, together, to run at once from one PC: more can't go faster than <see cref="YouTubeTraffic"/> allows.</summary>
    public const int Cap = 4;

    /// <summary>The share of the processors a sync may use; the rest is left for the user.</summary>
    public const decimal UserShare = 0.5m;

    /// <summary>
    /// The CPU time each lookup or download takes per second of wall time (measured: lookups 3.2 s of 5.4 s,
    /// downloads 0.7 s of 1.1 s).
    /// </summary>
    public const decimal CpuPerWall = 0.6m;

    /// <summary>The share of the free memory a sync may use.</summary>
    public const decimal MemoryShare = 0.25m;

    /// <summary>The memory one lookup needs at its peak (measured at about 370 MiB, mostly Deno).</summary>
    public const long LookupMemoryBytes = 400L * 1024 * 1024;

    /// <summary>The memory one download needs (measured).</summary>
    public const long DownloadMemoryBytes = 60L * 1024 * 1024;

    /// <summary>The lookups and downloads to run at once on a PC with these logical processors and free memory.</summary>
    public static DownloadConcurrency For(int processorCount, long availableMemoryBytes) => new(
        MaxParallelLookups: MaxParallel(processorCount, availableMemoryBytes, LookupMemoryBytes, CpuPerWall, UserShare, MemoryShare, Cap),
        MaxParallelDownloads: MaxParallel(processorCount, availableMemoryBytes, DownloadMemoryBytes, CpuPerWall, UserShare, MemoryShare, Cap),
        MaxParallelLookupsAndDownloads: Math.Max(1, Math.Min(Cap, ByCpu(processorCount, CpuPerWall, UserShare))));

    /// <summary>
    /// How many tasks to run at once: as many as keep CPU use to <paramref name="userShare"/> of the processors
    /// (but at least 2), and memory use to <paramref name="memoryShare"/> of the free memory, up to
    /// <paramref name="cap"/>, and always at least 1.
    /// </summary>
    public static int MaxParallel(
        int processorCount,
        long availableMemoryBytes,
        long memoryPerTaskBytes,
        decimal cpuPerWall,
        decimal userShare,
        decimal memoryShare,
        int cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryPerTaskBytes, 1);

        var byCpu = ByCpu(processorCount, cpuPerWall, userShare);
        var byMemory = (long)Math.Floor(Math.Max(0, availableMemoryBytes) * memoryShare / memoryPerTaskBytes);

        return (int)Math.Max(1, Math.Min(Math.Min(cap, byCpu), byMemory));
    }

    /// <summary>How many tasks keep CPU use to <paramref name="userShare"/> of the processors, but at least 2.</summary>
    private static int ByCpu(int processorCount, decimal cpuPerWall, decimal userShare)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cpuPerWall, 0);
        return Math.Max(2, (int)Math.Floor(processorCount * userShare / cpuPerWall));
    }
}
