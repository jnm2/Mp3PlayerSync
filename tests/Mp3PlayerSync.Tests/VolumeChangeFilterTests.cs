namespace Mp3PlayerSync.Tests;

public class VolumeChangeFilterTests
{
    [Test]
    public void Volumes_already_there_at_the_start_are_not_changes()
    {
        var changes = 0;
        var filter = new VolumeChangeFilter(() => changes++);

        filter.OnAdded();
        filter.OnUpdated();
        filter.OnAdded();
        filter.OnEnumerationCompleted();

        changes.ShouldBe(0);
    }

    [Test]
    public void Volumes_added_or_updated_after_the_start_are_changes()
    {
        var changes = 0;
        var filter = new VolumeChangeFilter(() => changes++);
        filter.OnAdded();
        filter.OnEnumerationCompleted();

        filter.OnAdded();
        filter.OnUpdated();

        changes.ShouldBe(2);
    }

    [Test]
    public void A_volume_removed_while_starting_is_a_change()
    {
        var changes = 0;
        var filter = new VolumeChangeFilter(() => changes++);
        filter.OnAdded();

        filter.OnRemoved();

        changes.ShouldBe(1);
    }
}
