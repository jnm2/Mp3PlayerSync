using TUnit.Core.Executors;

namespace Mp3PlayerSync.Tests;

[STAThreadExecutor]
public class LaunchHandOverTests
{
    [Test]
    public void A_hand_over_that_finishes_is_reported()
    {
        LaunchHandOver.TryHandOver(() => Task.Delay(10), TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    [Test]
    public void A_running_copy_that_never_answers_is_given_up_on_after_the_timeout()
    {
        var neverAnswers = new TaskCompletionSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var handedOver = LaunchHandOver.TryHandOver(() => neverAnswers.Task, TimeSpan.FromMilliseconds(200));

        handedOver.ShouldBeFalse();
        stopwatch.Elapsed.ShouldBeInRange(TimeSpan.FromMilliseconds(190), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void A_running_copy_that_cannot_be_reached_is_reported_without_throwing()
    {
        LaunchHandOver.TryHandOver(() => throw new InvalidOperationException("gone"), TimeSpan.FromSeconds(10)).ShouldBeFalse();
    }
}
