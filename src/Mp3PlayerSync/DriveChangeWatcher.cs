using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Mp3PlayerSync;

/// <summary>
/// Raised on the window's thread when a drive is connected or removed.
/// </summary>
internal sealed class DriveChangeWatcher : IDisposable
{
    private const nuint SubclassId = 1;

    private readonly HWND window;
    private readonly SUBCLASSPROC procedure; // Kept alive for as long as Windows can call it

    public DriveChangeWatcher(nint windowHandle)
    {
        window = (HWND)windowHandle;
        procedure = WindowProcedure;
        PInvoke.SetWindowSubclass(window, procedure, SubclassId, 0);
    }

    public event EventHandler? DrivesChanged;

    private LRESULT WindowProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        if (message == PInvoke.WM_DEVICECHANGE && wParam.Value is PInvoke.DBT_DEVICEARRIVAL or PInvoke.DBT_DEVICEREMOVECOMPLETE)
            DrivesChanged?.Invoke(this, EventArgs.Empty);

        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose() => PInvoke.RemoveWindowSubclass(window, procedure, SubclassId);
}
