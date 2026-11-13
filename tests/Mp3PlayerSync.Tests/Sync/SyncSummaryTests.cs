using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class SyncSummaryTests
{
    [TestCase(12, 3, ExpectedResult = "Adding 12 songs, removing 3 songs")]
    [TestCase(12, 0, ExpectedResult = "Adding 12 songs")]
    [TestCase(0, 3, ExpectedResult = "Removing 3 songs")]
    [TestCase(1, 1, ExpectedResult = "Adding 1 song, removing 1 song")]
    [TestCase(0, 0, ExpectedResult = "")]
    public string Describe(int adding, int removing) => SyncSummary.Describe(adding, removing);
}
