using Windows.Win32;
using Windows.Win32.System.Power;

namespace Mp3PlayerSync;

/// <summary>
/// Keeps the PC from sleeping while a sync runs, so a long sync isn't cut short. The display can still turn off.
/// </summary>
/// <remarks>
/// No WinRT or Windows App SDK API can stop a desktop app's PC from sleeping (<c>DisplayRequest</c> also keeps the
/// screen on, and extended execution is only for UWP), so this calls <c>SetThreadExecutionState</c>. Its state belongs
/// to the calling thread: start and dispose this on the same thread (the UI thread).
/// </remarks>
internal sealed class SleepBlocker : IDisposable
{
    private bool disposed;

    private SleepBlocker()
    {
    }

    public static SleepBlocker Start()
    {
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
        return new SleepBlocker();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
    }
}
