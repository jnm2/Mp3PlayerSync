using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class SpaceEstimateTests
{
    private const long MB = 1024 * 1024;

    [Test]
    public void A_download_is_estimated_from_its_length()
    {
        SpaceEstimate.ForDownload(TimeSpan.FromMinutes(3)).ShouldBe(3_600_000 + SpaceEstimate.PerFileBytes);
        SpaceEstimate.ForDownload(null).ShouldBe(SpaceEstimate.ForDownload(SpaceEstimate.UnknownDuration));
    }

    [Test]
    public void Everything_fits()
    {
        SpaceEstimate.Check([10 * MB, 10 * MB], freeBytes: 20 * MB + SpaceEstimate.MarginBytes).ShouldBeNull();
    }

    [Test]
    public void Counts_the_songs_at_the_end_that_wont_fit()
    {
        var shortfall = SpaceEstimate.Check([10 * MB, 10 * MB, 10 * MB], freeBytes: 15 * MB + SpaceEstimate.MarginBytes);

        shortfall.ShouldNotBeNull().ShouldBe(new SpaceShortfall(2, 15 * MB));
        SpaceEstimate.Describe(shortfall).ShouldBe("2 songs won't fit. 15 MB more space is needed.");
    }

    [Test]
    public void Files_being_removed_make_room()
    {
        SpaceEstimate.Check([10 * MB], freeBytes: SpaceEstimate.MarginBytes, freedBytes: 10 * MB).ShouldBeNull();
    }

    [Test]
    public void A_nearly_full_card_has_no_room_at_all()
    {
        SpaceEstimate.Check([1 * MB], freeBytes: 1 * MB).ShouldBe(new SpaceShortfall(1, 1 * MB));
    }
}
