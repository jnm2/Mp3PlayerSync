using Windows.Win32;
using Windows.Win32.System.StationsAndDesktops;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>Where and how the tests' process was started, which decides where the app may appear (TEST-1, TEST-3).</summary>
internal static class TestRunEnvironment
{
    /// <summary>
    /// Whether an IDE's Test Explorer (Visual Studio, VS Code, Rider) started the tests. It talks to them in the test
    /// platform's server mode (<c>--server</c>, or <c>--server jsonrpc</c>), which <c>dotnet test</c> uses only as
    /// <c>--server dotnettestcli</c>. Neither needs anything from the user.
    /// </summary>
    public static bool StartedByIde => IsIdeCommandLine(Environment.GetCommandLineArgs());

    /// <summary>Whether the arguments are an IDE's: the test platform's server mode, other than dotnet test's.</summary>
    public static bool IsIdeCommandLine(IReadOnlyList<string> args)
    {
        var server = args.ToList().FindIndex(a => string.Equals(a, "--server", StringComparison.OrdinalIgnoreCase));
        return server >= 0 && !(server + 1 < args.Count && string.Equals(args[server + 1], "dotnettestcli", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the launcher started the tests, on the desktop it made for them or on the one it was told to use.</summary>
    public static bool OnLauncherDesktop
    {
        get
        {
            var expected = Environment.GetEnvironmentVariable(Launcher.Launcher.DesktopVariable);
            return expected == "Current" || (expected is { Length: > 0 } && expected == CurrentDesktopName());
        }
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
