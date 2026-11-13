using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync;

/// <summary>
/// Functions used by the library tiles' bindings.
/// </summary>
internal static class LibraryVisuals
{
    private static readonly Brush LikedSongsBackground = new LinearGradientBrush(
        [new GradientStop { Color = ColorHelper.FromArgb(255, 0x45, 0x0A, 0xF5), Offset = 0 }, new GradientStop { Color = ColorHelper.FromArgb(255, 0xC4, 0xEF, 0xD9), Offset = 1 }],
        angle: 45);

    /// <summary>Loads the cover Exportify shows for a playlist (only that image; nothing else is requested).</summary>
    public static ImageSource? Cover(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new BitmapImage(uri) { DecodePixelWidth = 320, DecodePixelType = DecodePixelType.Logical }
            : null;

    /// <summary>
    /// Loads a cover decoded at the size it's drawn, for small covers. The decoder shrinks it smoothly; drawing a large
    /// image much smaller leaves it jagged, since the GPU samples it without averaging.
    /// </summary>
    /// <param name="size">The drawn width, in effective pixels.</param>
    /// <param name="scale">The screen's scale (XamlRoot.RasterizationScale).</param>
    public static ImageSource? SmallCover(string? url, double size, double scale) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new BitmapImage(uri) { DecodePixelWidth = (int)Math.Ceiling(size * scale), DecodePixelType = DecodePixelType.Physical }
            : null;

    public static Brush TileBackground(LibraryItemKind kind) => kind == LibraryItemKind.LikedSongs
        ? LikedSongsBackground
        : (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];

    public static Brush GlyphForeground(LibraryItemKind kind) => kind == LibraryItemKind.LikedSongs
        ? new SolidColorBrush(Colors.White)
        : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    public static Visibility VisibleIfAny(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Windows.UI.Text.FontStyle ItalicIf(bool italic) => italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    /// <summary>The drive usage bar: the accent color, or the critical color when the drive is nearly full.</summary>
    public static Brush UsageForeground(bool nearlyFull) =>
        (Brush)Application.Current.Resources[nearlyFull ? "SystemFillColorCriticalBrush" : "AccentFillColorDefaultBrush"];

    public static string UndoSkipLabel(string title) => $"Undo skipping {title}";

    public static string RemoveLabel(string title) => $"Remove {title} from the player";
}
