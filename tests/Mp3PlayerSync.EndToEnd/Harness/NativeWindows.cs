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
}
