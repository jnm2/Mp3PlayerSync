using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Mp3PlayerSync.EndToEnd.Harness;

internal static class NativeWindows
{
    /// <summary>The descendant windows of <paramref name="parent"/> of the window class <paramref name="className"/>.</summary>
    public static unsafe List<nint> Children(nint parent, string className)
    {
        var found = new List<nint>();
        var name = stackalloc char[256];
        PInvoke.EnumChildWindows((HWND)parent, (child, _) =>
        {
            var length = PInvoke.GetClassName(child, name, 256);
            if (new string(name, 0, length) == className) found.Add(child);
            return true;
        }, default);
        return found;
    }

    /// <summary>
    /// The visible top-level window of the window class <paramref name="className"/> that process
    /// <paramref name="processId"/> has, such as its folder dialog (#32770), or 0 if there's none.
    /// </summary>
    public static unsafe nint ProcessWindow(int processId, string className)
    {
        nint found = 0;
        var name = stackalloc char[256];
        PInvoke.EnumWindows((window, _) =>
        {
            uint owner;
            PInvoke.GetWindowThreadProcessId(window, &owner);
            if (owner != (uint)processId || !PInvoke.IsWindowVisible(window)) return true;
            if (new string(name, 0, PInvoke.GetClassName(window, name, 256)) != className) return true;
            found = window;
            return false;
        }, default);
        return found;
    }

    /// <summary>
    /// Clicks a Win32 button, such as a dialog's, without waiting for what it does: UI Automation can't, since on a test
    /// desktop such buttons show up as panes without InvokePattern.
    /// </summary>
    public static void Click(nint button)
    {
        if (!PInvoke.PostMessage((HWND)button, PInvoke.BM_CLICK, default, default)) throw new Win32Exception();
    }
}