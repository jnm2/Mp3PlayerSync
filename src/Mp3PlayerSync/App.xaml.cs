using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Mp3PlayerSync;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private readonly AppServices services;

    /// <summary>The app's one window.</summary>
    public static Window? MainWindow { get; private set; }

    private static Microsoft.UI.Dispatching.DispatcherQueue? uiQueue;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App(AppServices services)
    {
        this.services = services;
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(services);
        uiQueue = MainWindow.DispatcherQueue;
        services.ShowWindow(MainWindow);
    }

    /// <summary>
    /// Brings the window back when the app is launched again. Called on any thread, and returns at once: the other
    /// launch waits for it.
    /// </summary>
    public static void ShowMainWindow(AppServices services) =>
        // The window is touched only on the UI thread, not on the pool thread this is called on.
        uiQueue?.TryEnqueue(() => { if (MainWindow is { } window) services.BringBackWindow(window); });
}
