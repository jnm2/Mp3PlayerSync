using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class SpaceEstimateTests
{
    private const long MB = 1024 * 1024;

    [Test]
    public void A_download_is_estimated_from_its_length()
    {
        Assert.That(SpaceEstimate.ForDownload(TimeSpan.FromMinutes(3)), Is.EqualTo(3_600_000 + SpaceEstimate.PerFileBytes));
        Assert.That(SpaceEstimate.ForDownload(null), Is.EqualTo(SpaceEstimate.ForDownload(SpaceEstimate.UnknownDuration)));
    }

    [Test]
    public void Everything_fits()
    {
        Assert.That(SpaceEstimate.Check([10 * MB, 10 * MB], freeBytes: 20 * MB + SpaceEstimate.MarginBytes), Is.Null);
    }

    [Test]
    public void Counts_the_songs_at_the_end_that_wont_fit()
    {
        var shortfall = SpaceEstimate.Check([10 * MB, 10 * MB, 10 * MB], freeBytes: 15 * MB + SpaceEstimate.MarginBytes);

        Assert.That(shortfall, Is.EqualTo(new SpaceShortfall(2, 15 * MB)));
        Assert.That(SpaceEstimate.Describe(shortfall!), Is.EqualTo("2 songs won't fit. 15 MB more space is needed."));
    }

    [Test]
    public void Files_being_removed_make_room()
    {
        Assert.That(SpaceEstimate.Check([10 * MB], freeBytes: SpaceEstimate.MarginBytes, freedBytes: 10 * MB), Is.Null);
    }

    [Test]
    public void A_nearly_full_card_has_no_room_at_all()
    {
        Assert.That(SpaceEstimate.Check([1 * MB], freeBytes: 1 * MB), Is.EqualTo(new SpaceShortfall(1, 1 * MB)));
    }
}
