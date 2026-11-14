using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class SpaceEstimateTests
{
    private const long MB = 1024 * 1024;

    [Test]
    public async Task A_download_is_estimated_from_its_length()
    {
        await Assert.That(SpaceEstimate.ForDownload(TimeSpan.FromMinutes(3))).IsEqualTo(3_600_000 + SpaceEstimate.PerFileBytes);
        await Assert.That(SpaceEstimate.ForDownload(null)).IsEqualTo(SpaceEstimate.ForDownload(SpaceEstimate.UnknownDuration));
    }

    [Test]
    public async Task Everything_fits()
    {
        await Assert.That(SpaceEstimate.Check([10 * MB, 10 * MB], freeBytes: 20 * MB + SpaceEstimate.MarginBytes)).IsNull();
    }

    [Test]
    public async Task Counts_the_songs_at_the_end_that_wont_fit()
    {
        var shortfall = SpaceEstimate.Check([10 * MB, 10 * MB, 10 * MB], freeBytes: 15 * MB + SpaceEstimate.MarginBytes);

        await Assert.That(shortfall).IsEqualTo(new SpaceShortfall(2, 15 * MB));
        await Assert.That(SpaceEstimate.Describe(shortfall!)).IsEqualTo("2 songs won't fit. 15 MB more space is needed.");
    }

    [Test]
    public async Task Files_being_removed_make_room()
    {
        await Assert.That(SpaceEstimate.Check([10 * MB], freeBytes: SpaceEstimate.MarginBytes, freedBytes: 10 * MB)).IsNull();
    }

    [Test]
    public async Task A_nearly_full_card_has_no_room_at_all()
    {
        await Assert.That(SpaceEstimate.Check([1 * MB], freeBytes: 1 * MB)).IsEqualTo(new SpaceShortfall(1, 1 * MB));
    }
}
