namespace Mp3PlayerSync.Tests;

public class RerunningCheckTests
{
    [Test]
    public async Task Asking_during_a_check_runs_it_again_afterwards()
    {
        var runs = 0;
        var first = new TaskCompletionSource();
        var check = new RerunningCheck(async () =>
        {
            if (++runs == 1) await first.Task;
        });

        var running = check.RunAsync();
        // Asked twice while the first check waits, such as after a sync and by the drive watcher
        var asked = check.RunAsync();
        var askedAgain = check.RunAsync();
        new[] { asked, askedAgain }.ShouldAllBe(task => task.IsCompleted, "Asking while it runs doesn't wait for it");

        first.SetResult();
        await running;

        runs.ShouldBe(2);
    }

    [Test]
    public async Task A_check_that_failed_still_runs_when_asked_again()
    {
        var runs = 0;
        var check = new RerunningCheck(() => ++runs == 1 ? Task.FromException(new IOException("gone")) : Task.CompletedTask);

        await Should.ThrowAsync<IOException>(check.RunAsync);
        await check.RunAsync();

        runs.ShouldBe(2);
    }
}
