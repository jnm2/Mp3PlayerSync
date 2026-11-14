namespace Mp3PlayerSync;

/// <summary>
/// Runs a check one at a time, on one thread. Asked again while it runs, it runs once more when it finishes, rather
/// than not at all, so what changed meanwhile, such as a sync filling the card, is always seen.
/// </summary>
public sealed class RerunningCheck(Func<Task> check)
{
    private bool running;
    private bool again;

    public async Task RunAsync()
    {
        if (running)
        {
            again = true;
            return;
        }

        running = true;
        try
        {
            do
            {
                again = false;
                await check();
            } while (again);
        }
        finally
        {
            running = false;
        }
    }
}
