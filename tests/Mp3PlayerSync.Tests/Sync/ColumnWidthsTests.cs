using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class ColumnWidthsTests
{
    [Test]
    public async Task When_everything_fits_each_column_gets_what_it_needs_and_a_proportional_share_of_the_rest() =>
        await Assert.That(ColumnWidths.Fit(600, [(100, 80), (200, 60), (0, 0)])).IsEquivalentTo(new double[] { 200, 400, 0 }, CollectionOrdering.Matching);

    [Test]
    public async Task When_it_does_not_fit_the_widest_column_shrinks_first() =>
        await Assert.That(ColumnWidths.Fit(400, [(100, 80), (150, 60), (300, 60)])).IsEquivalentTo(new double[] { 100, 150, 150 }, CollectionOrdering.Matching);

    [Test]
    public async Task Columns_wider_than_the_cap_share_the_shrinking() =>
        await Assert.That(ColumnWidths.Fit(300, [(50, 40), (200, 60), (300, 60)])).IsEquivalentTo(new double[] { 50, 125, 125 }, CollectionOrdering.Matching);

    [Test]
    public async Task No_column_goes_below_its_minimum() =>
        await Assert.That(ColumnWidths.Fit(100, [(200, 80), (200, 60)])).IsEquivalentTo(new double[] { 80, 60 }, CollectionOrdering.Matching);

    [Test]
    public async Task A_column_with_nothing_to_fit_gets_nothing() =>
        await Assert.That(ColumnWidths.Fit(500, [(0, 60), (0, 0)])).IsEquivalentTo(new double[] { 0, 0 }, CollectionOrdering.Matching);
}
