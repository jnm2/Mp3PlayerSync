using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class SyncSummaryTests
{
    [Test]
    [Arguments(12, 3, "Adding 12 songs, removing 3 songs")]
    [Arguments(12, 0, "Adding 12 songs")]
    [Arguments(0, 3, "Removing 3 songs")]
    [Arguments(1, 1, "Adding 1 song, removing 1 song")]
    [Arguments(0, 0, "")]
    public void Describe(int adding, int removing, string expected)
    {
        SyncSummary.Describe(adding, removing).ShouldBe(expected);
    }
}
