using System.Net;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Tagging;
using Windows.Graphics.Imaging;

namespace Mp3PlayerSync.Tests.Tagging;

public class PlayerCoverArtTests
{
    private static readonly (byte R, byte G, byte B) Red = (255, 0, 0), Green = (0, 255, 0), Blue = (0, 0, 255);

    [Test]
    [Arguments(1280, 720, 889, 500, 194, 0, 500)]
    [Arguments(720, 1280, 500, 889, 0, 194, 500)]
    [Arguments(544, 544, 500, 500, 0, 0, 500)]
    [Arguments(300, 300, 300, 300, 0, 0, 300)]
    [Arguments(336, 188, 336, 188, 74, 0, 188)]
    public void Crops_the_center_square_and_only_ever_scales_down(int width, int height, int scaledWidth, int scaledHeight, int x, int y, int size)
    {
        PlayerCoverArt.PlanCrop(width, height).ShouldBe(new CoverArtCrop(scaledWidth, scaledHeight, x, y, size));
    }

    [Test]
    public void Reads_whether_a_jpeg_is_baseline_and_its_size()
    {
        PlayerCoverArt.ReadJpegFrame(Jpeg(0xC0, 500, 400)).ShouldBe(new JpegFrame(true, 500, 400));
        PlayerCoverArt.ReadJpegFrame(Jpeg(0xC2, 500, 500)).ShouldBe(new JpegFrame(false, 500, 500));
        PlayerCoverArt.ReadJpegFrame([0x89, 0x50, 0x4E, 0x47]).ShouldBeNull();
    }

    [Test]
    public void Only_square_baseline_jpegs_of_at_most_500_pixels_are_ready()
    {
        PlayerCoverArt.IsReady(Jpeg(0xC0, 500, 500)).ShouldBeTrue();
        PlayerCoverArt.IsReady(Jpeg(0xC2, 500, 500)).ShouldBeFalse(customMessage: "Progressive");
        PlayerCoverArt.IsReady(Jpeg(0xC0, 640, 640)).ShouldBeFalse(customMessage: "Too big");
        PlayerCoverArt.IsReady(Jpeg(0xC0, 1280, 720)).ShouldBeFalse(customMessage: "Not square");
    }

    [Test]
    public async Task Converts_a_16_9_frame_to_its_center_square_as_a_500_pixel_baseline_jpeg()
    {
        // Blue and red bands at the sides, which the crop cuts off.
        var png = await EncodeAsync(BitmapEncoder.PngEncoderId, 1280, 720, x => x < 260 ? Blue : x >= 1020 ? Red : Green);

        var art = await new WindowsAlbumArtConverter().ConvertAsync(png, CancellationToken.None);

        art.ShouldNotBeNull().MimeType.ShouldBe("image/jpeg");
        PlayerCoverArt.ReadJpegFrame(art.Data).ShouldBe(new JpegFrame(IsBaseline: true, 500, 500));
        var (left, right) = await EdgePixelsAsync(art.Data);
        (byte B, byte G, byte R)[] edges = [left, right];
        edges.ShouldAllBe(p => p.G > 200 && p.R < 50 && p.B < 50, customMessage: "Only the green center is left");
    }

    [Test]
    public async Task Small_art_is_not_scaled_up()
    {
        var png = await EncodeAsync(BitmapEncoder.PngEncoderId, 300, 300, _ => Green);

        var art = await new WindowsAlbumArtConverter().ConvertAsync(png, CancellationToken.None);

        PlayerCoverArt.ReadJpegFrame(art.ShouldNotBeNull().Data).ShouldBe(new JpegFrame(IsBaseline: true, 300, 300));
    }

    [Test]
    public async Task Data_that_is_not_an_image_converts_to_nothing()
    {
        (await new WindowsAlbumArtConverter().ConvertAsync([0xFF, 0xD8, 0xFF, 0xE0, 0, 0], CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task Fetched_art_is_converted_unless_it_is_ready()
    {
        var ready = await EncodeAsync(BitmapEncoder.JpegEncoderId, 500, 500, _ => Green);
        var progressive = Jpeg(0xC2, 500, 500);
        var converter = new FakeConverter();
        var fetcher = new HttpAlbumArtFetcher(
            new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath.Contains("ready", StringComparison.Ordinal) ? ready : progressive)),
            converter);

        (await fetcher.FetchAsync("https://lh3.googleusercontent.com/ready=w500-h500-l90-rj")).ShouldNotBeNull().Data.ShouldBe(ready);
        converter.Converted.ShouldBeEmpty(customMessage: "Art that's ready is embedded as is");

        (await fetcher.FetchAsync("https://i.ytimg.com/vi/vid00000001/hq720.jpg")).ShouldNotBeNull().Data.ShouldBe(FakeConverter.Output);
        converter.Converted.ShouldBe([progressive]);
    }

    [Test]
    public async Task A_synced_song_gets_a_500_pixel_baseline_jpeg_cover()
    {
        var frame = await EncodeAsync(BitmapEncoder.PngEncoderId, 1280, 720, _ => Green);
        var directory = Directory.CreateTempSubdirectory("Mp3PlayerSync.Tests").FullName;
        try
        {
            var path = Path.Combine(directory, "song.m4a");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Tagging", "Assets", "silence.m4a"), path);
            var writer = new TrackTagWriter(new HttpAlbumArtFetcher(new HttpClient(new FakeHandler(_ => frame)), new WindowsAlbumArtConverter()));

            var result = await writer.WriteAsync(path, new TrackMetadata { Title = "Song", AlbumImageUrl = "https://i.ytimg.com/vi/vid00000001/hq720.jpg" });

            result.CoverArtEmbedded.ShouldBeTrue();
            using var file = TagLib.File.Create(path);
            var picture = file.Tag.Pictures.ShouldHaveSingleItem();
            picture.MimeType.ShouldBe("image/jpeg");
            PlayerCoverArt.ReadJpegFrame(picture.Data.Data).ShouldBe(new JpegFrame(IsBaseline: true, 500, 500));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The start of a JPEG, up to its frame header.</summary>
    private static byte[] Jpeg(byte sof, int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, sof, 0x00, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
    ];

    private static async Task<byte[]> EncodeAsync(Guid encoderId, int width, int height, Func<int, (byte R, byte G, byte B)> colorAtX)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colorAtX(x);
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        }

        using var output = new MemoryStream();
        using (var stream = output.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
        }

        return output.ToArray();
    }

    /// <summary>The pixels in the middle of the left and right edges, as (B, G, R).</summary>
    private static async Task<((byte B, byte G, byte R) Left, (byte B, byte G, byte R) Right)> EdgePixelsAsync(byte[] image)
    {
        using var stream = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var width = (int)decoder.PixelWidth;
        var row = (int)decoder.PixelHeight / 2 * width;
        (byte, byte, byte) At(int x) => (pixels[(row + x) * 4], pixels[(row + x) * 4 + 1], pixels[(row + x) * 4 + 2]);
        return (At(2), At(width - 3));
    }

    private sealed class FakeConverter : IAlbumArtConverter
    {
        public static readonly byte[] Output = [0xFF, 0xD8, 0xFF, 0xD9];

        public List<byte[]> Converted { get; } = [];

        public Task<AlbumArt?> ConvertAsync(byte[] image, CancellationToken cancellationToken)
        {
            Converted.Add(image);
            return Task.FromResult<AlbumArt?>(new AlbumArt(Output, "image/jpeg"));
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[]> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(respond(request)) });
    }
}
