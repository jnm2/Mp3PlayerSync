namespace Mp3PlayerSync;

/// <summary>
/// Hands a launch over to the copy that's already running, without letting a slow or stuck copy keep this invisible
/// launch alive.
/// </summary>
public static class LaunchHandOver
{
    /// <summary>How long a launch waits for the running copy to take it over before exiting anyway.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="handOver"/> on a pool thread and waits for it, at most <paramref name="timeout"/>.
    /// Returns whether it finished; failures are reported as not finishing.
    /// </summary>
    public static bool TryHandOver(Func<Task> handOver, TimeSpan timeout)
    {
        var task = Task.Run(handOver);

        // A WaitHandle wait on an STA thread keeps pumping COM, which the hand-over needs from this thread.
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout))
        {
            System.Diagnostics.Debug.WriteLine($"The running copy did not take over the launch within {timeout.TotalSeconds:0} s.");
            return false;
        }

        if (!task.IsCompletedSuccessfully)
        {
            System.Diagnostics.Debug.WriteLine($"Could not reach the running copy: {task.Exception?.InnerException?.Message ?? "canceled"}");
            return false;
        }

        return true;
    }
}
