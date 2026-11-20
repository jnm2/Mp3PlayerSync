using System.Diagnostics;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Skips every end-to-end test unless it runs where the app may appear (TEST-1): on the desktop the launcher made for
/// it, or wherever the user's IDE ran or is debugging it. So <c>dotnet test</c> or running the exe directly can never put
/// the app on the user's desktop, and the launcher counts the skip as not passed.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DesktopGuardAttribute() : SkipAttribute(
    @"Run through the launcher, dotnet run --project tests\Mp3PlayerSync.EndToEnd, which keeps the app off your desktop, or from your IDE's Test Explorer (TEST-1)")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!(TestRunEnvironment.OnLauncherDesktop || TestRunEnvironment.StartedByIde || Debugger.IsAttached));
}
