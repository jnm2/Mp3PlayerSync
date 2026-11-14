using System.Xml.Linq;

namespace Mp3PlayerSync.EndToEnd.Launcher;

/// <summary>What the TRX report says about each test.</summary>
internal sealed record TestOutcomes(IReadOnlyList<TestOutcome> Results)
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestOutcomes Read(string path)
    {
        var results = XDocument.Load(path).Descendants(Trx + "UnitTestResult").Select(result => new TestOutcome(
            (string?)result.Attribute("testName") ?? "?",
            (string?)result.Attribute("outcome") ?? "?",
            // A failure's message, or a skip's reason, which the test platform writes as "Skipped: <reason>"
            ((string?)result.Descendants(Trx + "Message").FirstOrDefault() ?? (string?)result.Descendants(Trx + "DebugTrace").FirstOrDefault())?.Trim()));
        return new(results.ToList());
    }
}

internal sealed record TestOutcome(string Name, string Outcome, string? Message)
{
    /// <summary>
    /// How a test reports that it couldn't see what it checks: as a skip whose reason starts with this, since the test
    /// platform has no outcome of its own for it.
    /// </summary>
    public const string InconclusivePrefix = "Inconclusive:";

    public bool Passed => Outcome == "Passed";

    public bool Inconclusive => Outcome == "NotExecuted" && Message?.Contains(InconclusivePrefix, StringComparison.Ordinal) == true;
}
