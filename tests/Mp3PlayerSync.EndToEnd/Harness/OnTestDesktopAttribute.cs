using Windows.Win32;
using Windows.Win32.System.StationsAndDesktops;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Skips every end-to-end test unless the launcher started it on the desktop it made for the tests, so an IDE or a
/// stray <c>dotnet run</c> can never put the app on the user's desktop (TEST-1). The launcher counts the skip as not
/// passed.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class OnTestDesktopAttribute() : SkipAttribute(
    @"Run through the launcher, dotnet run --project tests\Mp3PlayerSync.EndToEnd, which keeps the app off your desktop (TEST-1)")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
    {
        var expected = Environment.GetEnvironmentVariable(Launcher.Launcher.DesktopVariable);
        var onTestDesktop = expected == "Current" || (expected is { Length: > 0 } && expected == CurrentDesktopName());
        return Task.FromResult(!onTestDesktop);
    }

    private static unsafe string? CurrentDesktopName()
    {
        var desktop = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
        var name = stackalloc char[256];
        uint length;
        return PInvoke.GetUserObjectInformation((Windows.Win32.Foundation.HANDLE)desktop.Value, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, name, 256 * sizeof(char), &length)
            ? new string(name)
            : null;
    }
}
