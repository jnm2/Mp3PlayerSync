using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.StationsAndDesktops;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Skips every end-to-end test unless it runs where the app may appear (TEST-1): on the desktop the launcher made for
/// it, or wherever the user's IDE ran or is debugging it. So <c>dotnet test</c> or running the exe directly can never put
/// the app on the user's desktop, and the launcher counts the skip as not passed.
/// </summary>
/// <remarks>
/// An IDE's Test Explorer (Visual Studio, VS Code, Rider) talks to the tests in the test platform's server mode
/// (<c>--server</c>, or <c>--server jsonrpc</c>), which <c>dotnet test</c> uses only as <c>--server dotnettestcli</c>.
/// Neither needs anything from the user.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DesktopGuardAttribute() : SkipAttribute(
    @"Run through the launcher, dotnet run --project tests\Mp3PlayerSync.EndToEnd, which keeps the app off your desktop, or from your IDE's Test Explorer (TEST-1)")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!(OnLauncherDesktop() || StartedByIde(Environment.GetCommandLineArgs()) || Debugger.IsAttached));

    /// <summary>Whether the arguments are an IDE's: the test platform's server mode, other than dotnet test's.</summary>
    public static bool StartedByIde(IReadOnlyList<string> args)
    {
        var server = args.ToList().FindIndex(a => string.Equals(a, "--server", StringComparison.OrdinalIgnoreCase));
        return server >= 0 && !(server + 1 < args.Count && string.Equals(args[server + 1], "dotnettestcli", StringComparison.OrdinalIgnoreCase));
    }

    private static bool OnLauncherDesktop()
    {
        var expected = Environment.GetEnvironmentVariable(Launcher.Launcher.DesktopVariable);
        return expected == "Current" || (expected is { Length: > 0 } && expected == CurrentDesktopName());
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
