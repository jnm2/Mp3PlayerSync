using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class ColumnWidthsTests
{
    [Test]
    public void When_everything_fits_each_column_gets_what_it_needs_and_a_proportional_share_of_the_rest() =>
        ColumnWidths.Fit(600, [(100, 80), (200, 60), (0, 0)]).ShouldBe([200, 400, 0]);

    [Test]
    public void When_it_does_not_fit_the_widest_column_shrinks_first() =>
        ColumnWidths.Fit(400, [(100, 80), (150, 60), (300, 60)]).ShouldBe([100, 150, 150]);

    [Test]
    public void Columns_wider_than_the_cap_share_the_shrinking() =>
        ColumnWidths.Fit(300, [(50, 40), (200, 60), (300, 60)]).ShouldBe([50, 125, 125]);

    [Test]
    public void No_column_goes_below_its_minimum() =>
        ColumnWidths.Fit(100, [(200, 80), (200, 60)]).ShouldBe([80, 60]);

    [Test]
    public void A_column_with_nothing_to_fit_gets_nothing() =>
        ColumnWidths.Fit(500, [(0, 60), (0, 0)]).ShouldBe([0, 0]);
}
