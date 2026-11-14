using TUnit.Core.Executors;

namespace Mp3PlayerSync.Tests;

[STAThreadExecutor]
public class LaunchHandOverTests
{
    [Test]
    public async Task A_hand_over_that_finishes_is_reported()
    {
        await Assert.That(LaunchHandOver.TryHandOver(() => Task.Delay(10), TimeSpan.FromSeconds(10))).IsTrue();
    }

    [Test]
    public async Task A_running_copy_that_never_answers_is_given_up_on_after_the_timeout()
    {
        var neverAnswers = new TaskCompletionSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var handedOver = LaunchHandOver.TryHandOver(() => neverAnswers.Task, TimeSpan.FromMilliseconds(200));

        await Assert.That(handedOver).IsFalse();
        await Assert.That(stopwatch.Elapsed).IsBetween(TimeSpan.FromMilliseconds(190), TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_running_copy_that_cannot_be_reached_is_reported_without_throwing()
    {
        await Assert.That(LaunchHandOver.TryHandOver(() => throw new InvalidOperationException("gone"), TimeSpan.FromSeconds(10))).IsFalse();
    }
}
