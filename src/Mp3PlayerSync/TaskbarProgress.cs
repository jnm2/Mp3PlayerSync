using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Mp3PlayerSync;

public enum TaskbarProgressState { None, Normal, Indeterminate, Paused, Error }

/// <summary>
/// Shows progress on the app's taskbar button.
/// </summary>
internal sealed class TaskbarProgress(nint windowHandle)
{
    private ITaskbarList3? taskbar;
    private bool unavailable;

    public void Set(TaskbarProgressState state, double fraction = 0)
    {
        if (Taskbar is not { } list) return;
        try
        {
            var hwnd = new HWND(windowHandle);
            list.SetProgressState(hwnd, state switch
            {
                TaskbarProgressState.Normal => TBPFLAG.TBPF_NORMAL,
                TaskbarProgressState.Indeterminate => TBPFLAG.TBPF_INDETERMINATE,
                TaskbarProgressState.Paused => TBPFLAG.TBPF_PAUSED,
                TaskbarProgressState.Error => TBPFLAG.TBPF_ERROR,
                _ => TBPFLAG.TBPF_NOPROGRESS,
            });
            if (state is TaskbarProgressState.Normal or TaskbarProgressState.Paused or TaskbarProgressState.Error)
                list.SetProgressValue(hwnd, (ulong)Math.Round(Math.Clamp(fraction, 0, 1) * 1000), 1000);
        }
        catch (Exception ex)
        {
            // E.g. Explorer restarted (the taskbar comes back with a new button), or there's no taskbar on this
            // desktop at all. Progress on the taskbar is only a nicety, so it never gets in the way of a sync.
            System.Diagnostics.Debug.WriteLine($"Taskbar progress failed: {ex.Message}");
            taskbar = null;
            unavailable = true;
        }
    }

    private ITaskbarList3? Taskbar
    {
        get
        {
            if (taskbar is not null || unavailable) return taskbar;
            try
            {
                var list = (ITaskbarList3)new TaskbarList();
                list.HrInit();
                taskbar = list;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"No taskbar progress: {ex.Message}");
                unavailable = true;
            }
            return taskbar;
        }
    }
}
