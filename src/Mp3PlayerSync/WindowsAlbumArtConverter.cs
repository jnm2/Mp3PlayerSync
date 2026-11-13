using Mp3PlayerSync.Core.Tagging;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Mp3PlayerSync;

/// <summary>
/// Converts cover art with Windows' own imaging (WIC), so the app needs no image library. WIC's JPEG encoder only
/// writes baseline JPEGs.
/// </summary>
public sealed class WindowsAlbumArtConverter : IAlbumArtConverter
{
    private const float JpegQuality = 0.9f;

    public async Task<AlbumArt?> ConvertAsync(byte[] image, CancellationToken cancellationToken)
    {
        try
        {
            using var input = new MemoryStream(image, writable: false).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);

            var crop = PlayerCoverArt.PlanCrop((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)crop.ScaledWidth,
                ScaledHeight = (uint)crop.ScaledHeight,
                Bounds = new BitmapBounds { X = (uint)crop.X, Y = (uint)crop.Y, Width = (uint)crop.Size, Height = (uint)crop.Size },
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);

            using var output = new MemoryStream();
            using (var outputStream = output.AsRandomAccessStream())
            {
                var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(JpegQuality, PropertyType.Single) };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream, options).AsTask(cancellationToken).ConfigureAwait(false);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)crop.Size, (uint)crop.Size, 96, 96, pixels.DetachPixelData());
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }

            return new AlbumArt(output.ToArray(), "image/jpeg");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not an image WIC can decode (e.g. a codec that isn't installed).
            return null;
        }
    }
}
