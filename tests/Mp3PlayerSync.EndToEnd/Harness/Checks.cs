using Mp3PlayerSync.EndToEnd.Launcher;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// The checks of one test, which go on after one fails, so a run shows every check that failed rather than only the
/// first. Each logs a PASS, FAIL or INCONCLUSIVE line. <see cref="Finish"/> then fails the test if any failed, or
/// reports it as inconclusive if any couldn't look at what it checks.
/// </summary>
/// <remarks>
/// A description starts with the decision it checks, such as <c>[TEXT-3]</c>, and docs\design-decisions.md quotes it.
/// What was actually found is in Shouldly's message, so the description doesn't repeat it.
/// </remarks>
public sealed class Checks
{
    private readonly List<string> failures = [];
    private readonly List<string> inconclusive = [];

    /// <summary>Runs <paramref name="assert"/>, a Shouldly assertion, and records whether it held.</summary>
    public void Check(string description, Action assert)
    {
        try
        {
            assert();
            Passed(description);
        }
        catch (ShouldAssertException exception)
        {
            Console.WriteLine($"  FAIL  {description}");
            Console.WriteLine(Indent(exception.Message));
            failures.Add($"{description}\n{Indent(exception.Message)}");
        }
    }

    /// <summary>Records a check that the test already showed by waiting for it, such as a page that came up.</summary>
    public void Passed(string description) => Console.WriteLine($"  PASS  {description}");

    /// <summary>
    /// Records a check that couldn't look at what it checks, say because there was no picture of the window: it neither
    /// passed nor failed.
    /// </summary>
    public void Inconclusive(string description, string why)
    {
        Console.WriteLine($"  INCONCLUSIVE  {description} ({why})");
        inconclusive.Add($"{description} ({why})");
    }

    /// <summary>
    /// Fails the test if any check failed, or skips it as inconclusive (which the launcher reports as exit code 2) if
    /// none failed but some couldn't look at what they check.
    /// </summary>
    public void Finish()
    {
        if (failures.Count > 0)
        {
            var also = inconclusive.Count > 0 ? $"\nAlso inconclusive:\n{string.Join('\n', inconclusive)}" : "";
            throw new ChecksFailedException($"{failures.Count} check(s) failed:\n{string.Join('\n', failures)}{also}");
        }
        if (inconclusive.Count > 0)
            Skip.Test($"{TestOutcome.InconclusivePrefix} {string.Join("; ", inconclusive)}");
    }

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(line => "        " + line.TrimEnd('\r')));
}

public sealed class ChecksFailedException(string message) : Exception(message);
