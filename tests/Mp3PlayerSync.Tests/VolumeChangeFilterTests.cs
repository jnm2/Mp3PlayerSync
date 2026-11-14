namespace Mp3PlayerSync.Tests;

public class VolumeChangeFilterTests
{
    [Test]
    public async Task Volumes_already_there_at_the_start_are_not_changes()
    {
        var changes = 0;
        var filter = new VolumeChangeFilter(() => changes++);

        filter.OnAdded();
        filter.OnUpdated();
        filter.OnAdded();
        filter.OnEnumerationCompleted();

        await Assert.That(changes).IsZero();
    }

    [Test]
    public async Task Volumes_added_or_updated_after_the_start_are_changes()
    {
        var changes = 0;
        var filter = new VolumeChangeFilter(() => changes++);
        filter.OnAdded();
        filter.OnEnumerationCompleted();

        filter.OnAdded();
        filter.OnUpdated();

        await Assert.That(changes).IsEqualTo(2);
    }

    [Test]
    public async Task A_volume_removed_while_starting_is_a_change()
    {
        var changes = 0;
        var filter = new VolumeChangeFilter(() => changes++);
        filter.OnAdded();

        filter.OnRemoved();

        await Assert.That(changes).IsEqualTo(1);
    }
}
