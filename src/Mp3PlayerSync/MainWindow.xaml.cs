using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Mp3PlayerSync;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(AppServices services)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        RootFrame.Content = new MainPage(services);

        // Closing mid-sync asks first.
        AppWindow.Closing += async (_, args) =>
        {
            if (closeConfirmed || RootFrame.Content is not MainPage { IsSyncing: true } page) return;
            args.Cancel = true;
            if (confirmingClose) return;

            confirmingClose = true;
            try
            {
                if (!await page.ConfirmCloseAsync()) return;
            }
            finally
            {
                confirmingClose = false;
            }

            closeConfirmed = true;
            Close();
        };

        RootFrame.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            RootFrame.XamlRoot.Changed += (_, _) => ApplyMinimumSize(); // e.g. moved to a screen with another scale
        };
    }

    private bool closeConfirmed, confirmingClose;

    // In effective pixels: the narrowest the track list is laid out for, and the window's height.
    private const int MinimumWidth = 800, MinimumHeight = 600;

    // Room to spare beyond what the content measured, in effective pixels.
    private const int SafetyMargin = 16;

    private double requiredContentWidth;
    private (double Scale, int Width, int Height) applied;

    /// <summary>
    /// Keeps the window wide enough for content that mustn't be clipped, e.g. the playlist page's tabs and buttons.
    /// </summary>
    /// <param name="width">The content's width in effective pixels, without the window's frame.</param>
    public void RequireContentWidth(double width)
    {
        requiredContentWidth = Math.Max(requiredContentWidth, width);
        ApplyMinimumSize();
    }

    // The presenter takes physical pixels and doesn't scale them for the screen.
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter || RootFrame.XamlRoot is not { } root) return;

        var scale = root.RasterizationScale;
        var frame = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width); // Physical pixels
        var width = (int)Math.Ceiling(Math.Max(MinimumWidth * scale, (requiredContentWidth + SafetyMargin) * scale + frame));
        var height = (int)Math.Ceiling(MinimumHeight * scale);
        if ((scale, width, height) == applied) return; // XamlRoot.Changed is also raised for every resize
        applied = (scale, width, height);

        presenter.PreferredMinimumWidth = width;
        presenter.PreferredMinimumHeight = height;
        if (AppWindow.Size.Width < width) AppWindow.Resize(new Windows.Graphics.SizeInt32(width, Math.Max(AppWindow.Size.Height, height)));
    }
}
