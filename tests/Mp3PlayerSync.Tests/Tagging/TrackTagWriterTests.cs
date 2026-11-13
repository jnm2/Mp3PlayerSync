using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Tagging;
using TagLib;

namespace Mp3PlayerSync.Tests.Tagging;

public class TrackTagWriterTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0xFF, 0xD9];

    private static readonly ReadOnlyByteVector RatingBox = "rtng";

    private static readonly TrackMetadata Full = new()
    {
        Title = "Under Pressure",
        Artists = ["Queen", "David Bowie"],
        Album = "Hot Space",
        AlbumArtists = ["Queen"],
        ReleaseDate = "1982-05-21",
        TrackNumber = 11,
        DiscNumber = 1,
        Genres = ["classic rock", "glam rock"],
        Isrc = "GBUM71029604",
        Label = "EMI",
        Copyright = "© 1982 Queen Productions Ltd",
        Explicit = true,
        Tempo = 113.5,
        Key = 2,
        Mode = 1,
        AlbumImageUrl = "https://i.scdn.co/image/abc",
    };

    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "Mp3PlayerSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    private string CopyAsset(string name)
    {
        var path = Path.Combine(directory, name);
        System.IO.File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Tagging", "Assets", name), path);
        return path;
    }

    [TestCase("silence.m4a")]
    [TestCase("silence-fragmented.m4a")]
    [TestCase("silence.mp3")]
    public void Writes_every_mapped_field(string asset)
    {
        var path = CopyAsset(asset);

        TrackTagWriter.Write(path, Full, new AlbumArt(JpegBytes, "image/jpeg"));

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        Assert.Multiple(() =>
        {
            Assert.That(tag.Title, Is.EqualTo("Under Pressure"));
            Assert.That(tag.Performers, Is.EqualTo(new[] { "Queen, David Bowie" }));
            Assert.That(tag.Album, Is.EqualTo("Hot Space"));
            Assert.That(tag.AlbumArtists, Is.EqualTo(new[] { "Queen" }));
            Assert.That(tag.Year, Is.EqualTo(1982));
            Assert.That(tag.Track, Is.EqualTo(11));
            Assert.That(tag.Disc, Is.EqualTo(1));
            Assert.That(tag.Genres, Is.EqualTo(new[] { "classic rock", "glam rock" }));
            Assert.That(tag.ISRC, Is.EqualTo("GBUM71029604"));
            Assert.That(tag.Publisher, Is.EqualTo("EMI"));
            Assert.That(tag.Copyright, Is.EqualTo("© 1982 Queen Productions Ltd"));
            Assert.That(tag.BeatsPerMinute, Is.EqualTo(114));
            Assert.That(tag.InitialKey, Is.EqualTo("D"));
            Assert.That(tag.Pictures, Has.Length.EqualTo(1));
            Assert.That(tag.Pictures[0].Type, Is.EqualTo(PictureType.FrontCover));
            Assert.That(tag.Pictures[0].MimeType, Is.EqualTo("image/jpeg"));
            Assert.That(tag.Pictures[0].Data.Data, Is.EqualTo(JpegBytes));
        });

        if (file is TagLib.Mpeg4.File)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple);
            Assert.That(apple.GetText(new ReadOnlyByteVector(0xA9, (byte)'d', (byte)'a', (byte)'y')), Is.EqualTo(new[] { "1982-05-21" }));
            Assert.That(apple.DataBoxes(RatingBox).Single().Data.Data, Is.EqualTo(new byte[] { 1 }));
        }
        else
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
            Assert.That(id3.Version, Is.EqualTo(4));
            Assert.That(id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TDRC").Single().Text, Is.EqualTo(new[] { "1982-05-21" }));
        }

        Assert.That(file.Properties.Codecs, Is.Not.Empty, "The audio stream must still be readable.");
    }

    [TestCase("silence.m4a")]
    [TestCase("silence.mp3")]
    public void Missing_fields_are_removed_and_existing_art_is_kept(string asset)
    {
        var path = CopyAsset(asset);
        TrackTagWriter.Write(path, Full, new AlbumArt(JpegBytes, "image/jpeg"));

        TrackTagWriter.Write(path, new TrackMetadata { Title = "Untitled", Artists = ["Solo"] }, art: null);

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        Assert.Multiple(() =>
        {
            Assert.That(tag.Title, Is.EqualTo("Untitled"));
            Assert.That(tag.Performers, Is.EqualTo(new[] { "Solo" }));
            Assert.That(tag.Album, Is.Null);
            Assert.That(tag.AlbumArtists, Is.Empty);
            Assert.That(tag.Year, Is.Zero);
            Assert.That(tag.Track, Is.Zero);
            Assert.That(tag.Disc, Is.Zero);
            Assert.That(tag.Genres, Is.Empty);
            Assert.That(tag.ISRC, Is.Null);
            Assert.That(tag.Publisher, Is.Null);
            Assert.That(tag.Copyright, Is.Null);
            Assert.That(tag.BeatsPerMinute, Is.Zero);
            Assert.That(tag.InitialKey, Is.Null);
            Assert.That(tag.Pictures, Has.Length.EqualTo(1));
        });

        if (file is TagLib.Mpeg4.File)
            Assert.That(((TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple)).DataBoxes(RatingBox), Is.Empty);
    }

    [Test]
    public void Unsupported_file_type_is_rejected()
    {
        var path = Path.Combine(directory, "notes.txt");
        System.IO.File.WriteAllText(path, "hello");

        Assert.That(() => TrackTagWriter.Write(path, Full, null), Throws.InstanceOf<Exception>());
    }

    [Test]
    public async Task WriteAsync_embeds_art_from_the_fetcher()
    {
        var path = CopyAsset("silence.m4a");
        var fetcher = new FakeArtFetcher(new AlbumArt(JpegBytes, "image/jpeg"));

        var result = await new TrackTagWriter(fetcher).WriteAsync(path, Full);

        Assert.That(result.CoverArtEmbedded, Is.True);
        Assert.That(fetcher.RequestedUrls, Is.EqualTo(new[] { "https://i.scdn.co/image/abc" }));
        using var file = TagLib.File.Create(path);
        Assert.That(file.Tag.Pictures, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task WriteAsync_still_tags_when_art_is_unavailable()
    {
        var path = CopyAsset("silence.mp3");

        var result = await new TrackTagWriter(new FakeArtFetcher(null)).WriteAsync(path, Full);

        Assert.That(result.CoverArtEmbedded, Is.False);
        using var file = TagLib.File.Create(path);
        Assert.That(file.Tag.Title, Is.EqualTo("Under Pressure"));
        Assert.That(file.Tag.Pictures, Is.Empty);
    }

    [Test]
    public async Task WriteAsync_does_not_fetch_without_an_image_url()
    {
        var path = CopyAsset("silence.m4a");
        var fetcher = new FakeArtFetcher(new AlbumArt(JpegBytes, "image/jpeg"));

        var result = await new TrackTagWriter(fetcher).WriteAsync(path, Full with { AlbumImageUrl = "" });

        Assert.That(result.CoverArtEmbedded, Is.False);
        Assert.That(fetcher.RequestedUrls, Is.Empty);
    }

    private sealed class FakeArtFetcher(AlbumArt? art) : IAlbumArtFetcher
    {
        public List<string> RequestedUrls { get; } = [];

        public Task<AlbumArt?> FetchAsync(string url, CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(url);
            return Task.FromResult(art);
        }
    }
}
