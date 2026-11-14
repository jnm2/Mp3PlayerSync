using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeCoverArtTests
{
    private const string AlbumArt = "https://yt3.googleusercontent.com/VPsyelQLcIhSo_NCqDkcFAnwkDdxPWAvwrrXsureRJH5HJjr5ZapIildtXEObJJYcf-UeUfjXqiOCKVd";

    /// <summary>A Songs result's thumbnails, trimmed (yt-dlp 2026.10.26, "Africa" by Toto).</summary>
    private const string SongThumbnails = $$"""
        [
            {"url": "{{AlbumArt}}=w226-h226-l90-rj", "height": 226, "width": 226, "preference": -39, "id": "0"},
            {"url": "{{AlbumArt}}=w302-h302-l90-rj", "height": 302, "width": 302, "preference": -39, "id": "1"},
            {"url": "{{AlbumArt}}=w544-h544-l90-rj", "height": 544, "width": 544, "preference": -39, "id": "2"},
            {"url": "https://i.ytimg.com/vi/QAo_Ycocl1E/3.jpg", "preference": -37, "id": "3"},
            {"url": "https://i.ytimg.com/vi/QAo_Ycocl1E/hqdefault.jpg", "preference": -7, "id": "33"},
            {"url": "https://i.ytimg.com/vi/QAo_Ycocl1E/hq720.jpg", "height": 720, "width": 1280, "preference": -3, "id": "41"},
            {"url": "https://i.ytimg.com/vi/QAo_Ycocl1E/maxresdefault.jpg", "height": 1080, "width": 1920, "preference": -1, "id": "43"},
            {"url": "https://i.ytimg.com/vi_webp/QAo_Ycocl1E/maxresdefault.webp", "preference": 0, "id": "44"}
        ]
        """;

    /// <summary>A music video's thumbnails, which are all 16:9 frames.</summary>
    private const string VideoThumbnails = """
        [
            {"url": "https://i.ytimg.com/vi/vid00000001/hqdefault.jpg", "preference": -7},
            {"url": "https://i.ytimg.com/vi/vid00000001/hqdefault.jpg?sqp=-oaymwEc&rs=AOn4", "height": 188, "width": 336, "preference": -7},
            {"url": "https://i.ytimg.com/vi_webp/vid00000001/hq720.webp", "height": 720, "width": 1280, "preference": -2},
            {"url": "https://i.ytimg.com/vi/vid00000001/hq720.jpg", "height": 720, "width": 1280, "preference": -3},
            {"url": "https://i.ytimg.com/vi/vid00000001/maxresdefault.jpg", "height": 1080, "width": 1920, "preference": -1}
        ]
        """;

    private static IReadOnlyList<YouTubeThumbnail> Parse(string thumbnailsJson) =>
        YouTubeVideoInfo.Parse($$"""{"id": "QAo_Ycocl1E", "title": "Africa", "thumbnails": {{thumbnailsJson}}}""").Thumbnails;

    [Test]
    public async Task A_song_gets_its_square_album_art_at_500_pixels()
    {
        await Assert.That(YouTubeCoverArt.SelectUrl(Parse(SongThumbnails))).IsEqualTo(AlbumArt + "=w500-h500-l90-rj");
    }

    [Test]
    public async Task Square_art_is_recognized_by_its_url_when_its_size_is_not_given()
    {
        var thumbnails = Parse("""[{"url": "https://lh3.googleusercontent.com/abc=w120-h120-l90-rj"}, {"url": "https://lh3.googleusercontent.com/banner=w1060-h175-p"}]""");

        await Assert.That(YouTubeCoverArt.SelectUrl(thumbnails)).IsEqualTo("https://lh3.googleusercontent.com/abc=w500-h500-l90-rj");
    }

    [Test]
    public async Task A_music_video_gets_the_smallest_16_9_jpeg_that_is_tall_enough_to_crop()
    {
        await Assert.That(YouTubeCoverArt.SelectUrl(Parse(VideoThumbnails))).IsEqualTo("https://i.ytimg.com/vi/vid00000001/hq720.jpg");
    }

    [Test]
    public async Task A_video_with_only_small_frames_gets_the_largest()
    {
        var thumbnails = Parse("""
            [
                {"url": "https://i.ytimg.com/vi/vid00000001/hqdefault.jpg?sqp=a", "height": 94, "width": 168},
                {"url": "https://i.ytimg.com/vi/vid00000001/hqdefault.jpg?sqp=b", "height": 188, "width": 336}
            ]
            """);

        await Assert.That(YouTubeCoverArt.SelectUrl(thumbnails)).IsEqualTo("https://i.ytimg.com/vi/vid00000001/hqdefault.jpg?sqp=b");
    }

    [Test]
    public async Task Thumbnails_of_unknown_size_or_shape_are_not_used()
    {
        var thumbnails = Parse("""
            [
                {"url": "https://i.ytimg.com/vi/vid00000001/hqdefault.jpg"},
                {"url": "https://i.ytimg.com/vi/vid00000001/sddefault.jpg", "height": 480, "width": 640},
                {"url": "https://example.com/art.jpg", "height": 500, "width": 500}
            ]
            """);

        await Assert.That(YouTubeCoverArt.SelectUrl(thumbnails)).IsNull();
        await Assert.That(YouTubeCoverArt.SelectUrl(Parse("[]"))).IsNull();
        await Assert.That(YouTubeVideoInfo.Parse("""{"id": "QAo_Ycocl1E"}""").Thumbnails).IsEmpty();
    }
}
