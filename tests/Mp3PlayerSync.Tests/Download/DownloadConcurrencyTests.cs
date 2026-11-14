using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class DownloadConcurrencyTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    [Test]
    [Arguments(2, 2)]
    [Arguments(4, 3)]
    [Arguments(8, 4)]
    [Arguments(16, 4)]
    public async Task Lookups_use_about_half_the_processors_up_to_the_cap(int processorCount, int expected)
    {
        await Assert.That(DownloadConcurrency.For(processorCount, availableMemoryBytes: 32 * GiB).MaxParallelLookups).IsEqualTo(expected);
    }

    [Test]
    public async Task Downloads_follow_the_same_processor_rule()
    {
        await Assert.That(DownloadConcurrency.For(4, availableMemoryBytes: 32 * GiB).MaxParallelDownloads).IsEqualTo(3);
        await Assert.That(DownloadConcurrency.For(16, availableMemoryBytes: 32 * GiB).MaxParallelDownloads).IsEqualTo(4);
    }

    [Test]
    public async Task An_8_GB_PC_with_3_GB_free_runs_one_lookup_but_more_downloads()
    {
        await Assert.That(DownloadConcurrency.For(8, availableMemoryBytes: 3 * GiB)).IsEqualTo(new DownloadConcurrency(MaxParallelLookups: 1, MaxParallelDownloads: 4, MaxParallelLookupsAndDownloads: 4));
    }

    [Test]
    public async Task One_processor_still_runs_two_at_once()
    {
        await Assert.That(DownloadConcurrency.For(1, availableMemoryBytes: 32 * GiB)).IsEqualTo(new DownloadConcurrency(2, 2, 2));
    }

    [Test]
    [Arguments(0)]
    [Arguments(100 * MiB)]
    [Arguments(-1)]
    public async Task Tiny_memory_still_runs_one_at_a_time(long availableMemoryBytes)
    {
        await Assert.That(DownloadConcurrency.For(16, availableMemoryBytes)).IsEqualTo(new DownloadConcurrency(1, 1, 4));
    }

    [Test]
    [Arguments(1, 2)]
    [Arguments(4, 3)]
    [Arguments(8, 4)]
    [Arguments(16, 4)]
    public async Task Lookups_and_downloads_together_share_one_processor_budget_and_cap(int processorCount, int expected)
    {
        await Assert.That(DownloadConcurrency.For(processorCount, availableMemoryBytes: 32 * GiB).MaxParallelLookupsAndDownloads).IsEqualTo(expected);
    }

    [Test]
    public async Task Huge_machines_stop_at_the_cap()
    {
        await Assert.That(DownloadConcurrency.For(1024, availableMemoryBytes: 4096 * GiB)).IsEqualTo(new DownloadConcurrency(DownloadConcurrency.Cap, DownloadConcurrency.Cap, DownloadConcurrency.Cap));
    }

    [Test]
    public async Task Exact_multiples_are_not_rounded_down()
    {
        // 12 × 0.5 / 0.6 is exactly 10, and 12 GiB × 0.25 / 1 GiB is exactly 3.
        await Assert.That(DownloadConcurrency.MaxParallel(12, 64 * GiB, 400 * MiB, cpuPerWall: 0.6m, userShare: 0.5m, memoryShare: 0.25m, cap: 100)).IsEqualTo(10);
        await Assert.That(DownloadConcurrency.MaxParallel(12, 12 * GiB, 1 * GiB, cpuPerWall: 0.6m, userShare: 0.5m, memoryShare: 0.25m, cap: 100)).IsEqualTo(3);
    }
}
