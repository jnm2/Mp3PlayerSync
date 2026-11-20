using Mp3PlayerSync.EndToEnd.Harness;
using TUnit.Core.Interfaces;

[assembly: DesktopGuard]
[assembly: TUnit.Core.Executors.TestExecutor<HiddenDesktopForIdeRunsExecutor>]
[assembly: ParallelLimiter<EndToEndRuns>]

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// How many runs one process drives at once: as many as the launcher's machine-wide slots (see SlotQueue), or they
/// starve each other of CPU and time out. Each one's waits allow for the others (see <see cref="Wait.Scale"/>).
/// </summary>
public sealed record EndToEndRuns : IParallelLimit
{
    public int Limit { get; } = Math.Max(2, Environment.ProcessorCount / 4);
}
