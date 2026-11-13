using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;

namespace Mp3PlayerSync;

/// <summary>
/// Starts the app, or hands the launch to the copy that's already running on the same data folder: two copies would
/// race on the settings, the YouTube matches and the card, and both would renew the Spotify session.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main() => Run(AppServices.CreateDefault());

    /// <summary>Starts the app with the given services, unless a copy is already running on their data folder.</summary>
    public static int Run(AppServices services)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToRunningInstance(services))
            return 0;

        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App(services);
        });
        return 0;
    }

    /// <summary>
    /// One instance per data folder, so copies that have folders of their own run side by side.
    /// </summary>
    private static string InstanceKey(string dataDirectory) =>
        "Mp3PlayerSync-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(dataDirectory).TrimEnd('\\').ToUpperInvariant())))[..32];

    private static bool RedirectToRunningInstance(AppServices services)
    {
        var running = AppInstance.FindOrRegisterForKey(InstanceKey(services.DataDirectory));
        if (running.IsCurrent)
        {
            running.Activated += (_, _) => App.ShowMainWindow(services);
            return false;
        }

        // This launch came from the user, so it may hand the foreground on to the running copy's window.
        PInvoke.AllowSetForegroundWindow(running.ProcessId);

        // Exit even if the running copy never answers: this launch has no window, so nobody could close it.
        var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        LaunchHandOver.TryHandOver(() => running.RedirectActivationToAsync(arguments).AsTask(), LaunchHandOver.Timeout);
        return true;
    }
}
