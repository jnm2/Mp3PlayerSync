namespace Mp3PlayerSync.Core.Tagging;

/// <summary>
/// Cover art as it's embedded for MP3 players: a square baseline (not progressive) JPEG of at most
/// <see cref="MaxSize"/>×<see cref="MaxSize"/>, which older player firmware can show.
/// </summary>
public static class PlayerCoverArt
{
    public const int MaxSize = 500;

    /// <summary>Whether the data is already a square baseline JPEG of at most <see cref="MaxSize"/>, to embed as is.</summary>
    public static bool IsReady(ReadOnlySpan<byte> data) =>
        ReadJpegFrame(data) is { IsBaseline: true } frame && frame.Width == frame.Height && frame.Width is > 0 and <= MaxSize;

    /// <summary>Reads a JPEG's frame header (SOF), or returns null if the data isn't a JPEG.</summary>
    public static JpegFrame? ReadJpegFrame(ReadOnlySpan<byte> data)
    {
        if (data is not [0xFF, 0xD8, ..]) return null;

        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) return null;

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // Fill byte
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                i += 2; // No length
                continue;
            }

            if (marker is 0xD9 or 0xDA) return null; // End of image or start of scan before any frame header

            // SOF0 to SOF15, except DHT, JPG and DAC, which share the range.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (i + 9 > data.Length) return null;
                return new JpegFrame(
                    IsBaseline: marker == 0xC0,
                    Width: (data[i + 7] << 8) | data[i + 8],
                    Height: (data[i + 5] << 8) | data[i + 6]);
            }

            var length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2) return null;
            i += 2 + length;
        }

        return null;
    }

    /// <summary>
    /// How to center-crop an image to a square of at most <see cref="MaxSize"/>: scale it to
    /// <see cref="CoverArtCrop.ScaledWidth"/>×<see cref="CoverArtCrop.ScaledHeight"/>, then take the
    /// <see cref="CoverArtCrop.Size"/>-pixel square at (<see cref="CoverArtCrop.X"/>, <see cref="CoverArtCrop.Y"/>).
    /// Images are never scaled up.
    /// </summary>
    public static CoverArtCrop PlanCrop(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var side = Math.Min(width, height);
        var size = Math.Min(side, MaxSize);
        var scaledWidth = width == side ? size : Math.Max(size, (int)Math.Round((double)width * size / side));
        var scaledHeight = height == side ? size : Math.Max(size, (int)Math.Round((double)height * size / side));
        return new CoverArtCrop(scaledWidth, scaledHeight, (scaledWidth - size) / 2, (scaledHeight - size) / 2, size);
    }
}

/// <param name="IsBaseline">Whether it's baseline DCT (SOF0), as opposed to progressive, lossless or extended.</param>
public readonly record struct JpegFrame(bool IsBaseline, int Width, int Height);

/// <summary>See <see cref="PlayerCoverArt.PlanCrop"/>.</summary>
public readonly record struct CoverArtCrop(int ScaledWidth, int ScaledHeight, int X, int Y, int Size);

/// <summary>
/// Turns downloaded cover art into <see cref="PlayerCoverArt"/>. It's implemented in the app with Windows' own imaging,
/// since this library doesn't target Windows.
/// </summary>
public interface IAlbumArtConverter
{
    /// <summary>
    /// Center-crops the image to a square (see <see cref="PlayerCoverArt.PlanCrop"/>) and encodes it as a baseline JPEG.
    /// Returns null if the image can't be decoded.
    /// </summary>
    Task<AlbumArt?> ConvertAsync(byte[] image, CancellationToken cancellationToken);
}
