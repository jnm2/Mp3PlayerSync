using Microsoft.Testing.Platform.Builder;

namespace Mp3PlayerSync.EndToEnd;

public static class Program
{
    // One exe in two roles. Run by a person or CI, it's the launcher, which starts it again on a desktop of its own to run
    // the tests. Started by the launcher, an IDE's Test Explorer or dotnet test (whose test platform server mode is
    // --server), it runs the tests on Microsoft.Testing.Platform, as the entry point it would otherwise generate does.
#pragma warning disable TUnit0034 // Do not declare a main method
    public static int Main(string[] args) =>
        Environment.GetEnvironmentVariable(Launcher.Launcher.DesktopVariable) is { Length: > 0 } || args.Contains("--server", StringComparer.OrdinalIgnoreCase)
            ? RunTestsAsync(args).GetAwaiter().GetResult()
            : Launcher.Launcher.Run(args);
#pragma warning restore TUnit0034

    private static async Task<int> RunTestsAsync(string[] args)
    {
        var builder = await TestApplication.CreateBuilderAsync(args);
        builder.AddSelfRegisteredExtensions(args);
        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }
}
