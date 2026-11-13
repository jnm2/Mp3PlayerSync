using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class DownloadConcurrencyTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    [TestCase(2, 2)]
    [TestCase(4, 3)]
    [TestCase(8, 4)]
    [TestCase(16, 4)]
    public void Lookups_use_about_half_the_processors_up_to_the_cap(int processorCount, int expected)
    {
        Assert.That(DownloadConcurrency.For(processorCount, availableMemoryBytes: 32 * GiB).MaxParallelLookups, Is.EqualTo(expected));
    }

    [Test]
    public void Downloads_follow_the_same_processor_rule()
    {
        Assert.That(DownloadConcurrency.For(4, availableMemoryBytes: 32 * GiB).MaxParallelDownloads, Is.EqualTo(3));
        Assert.That(DownloadConcurrency.For(16, availableMemoryBytes: 32 * GiB).MaxParallelDownloads, Is.EqualTo(4));
    }

    [Test]
    public void An_8_GB_PC_with_3_GB_free_runs_one_lookup_but_more_downloads()
    {
        Assert.That(DownloadConcurrency.For(8, availableMemoryBytes: 3 * GiB), Is.EqualTo(new DownloadConcurrency(MaxParallelLookups: 1, MaxParallelDownloads: 4, MaxParallelLookupsAndDownloads: 4)));
    }

    [Test]
    public void One_processor_still_runs_two_at_once()
    {
        Assert.That(DownloadConcurrency.For(1, availableMemoryBytes: 32 * GiB), Is.EqualTo(new DownloadConcurrency(2, 2, 2)));
    }

    [TestCase(0)]
    [TestCase(100 * MiB)]
    [TestCase(-1)]
    public void Tiny_memory_still_runs_one_at_a_time(long availableMemoryBytes)
    {
        Assert.That(DownloadConcurrency.For(16, availableMemoryBytes), Is.EqualTo(new DownloadConcurrency(1, 1, 4)));
    }

    [TestCase(1, 2)]
    [TestCase(4, 3)]
    [TestCase(8, 4)]
    [TestCase(16, 4)]
    public void Lookups_and_downloads_together_share_one_processor_budget_and_cap(int processorCount, int expected)
    {
        Assert.That(DownloadConcurrency.For(processorCount, availableMemoryBytes: 32 * GiB).MaxParallelLookupsAndDownloads, Is.EqualTo(expected));
    }

    [Test]
    public void Huge_machines_stop_at_the_cap()
    {
        Assert.That(DownloadConcurrency.For(1024, availableMemoryBytes: 4096 * GiB), Is.EqualTo(new DownloadConcurrency(DownloadConcurrency.Cap, DownloadConcurrency.Cap, DownloadConcurrency.Cap)));
    }

    [Test]
    public void Exact_multiples_are_not_rounded_down()
    {
        // 12 × 0.5 / 0.6 is exactly 10, and 12 GiB × 0.25 / 1 GiB is exactly 3.
        Assert.That(DownloadConcurrency.MaxParallel(12, 64 * GiB, 400 * MiB, cpuPerWall: 0.6m, userShare: 0.5m, memoryShare: 0.25m, cap: 100), Is.EqualTo(10));
        Assert.That(DownloadConcurrency.MaxParallel(12, 12 * GiB, 1 * GiB, cpuPerWall: 0.6m, userShare: 0.5m, memoryShare: 0.25m, cap: 100), Is.EqualTo(3));
    }
}
