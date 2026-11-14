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

    [Before(Test)]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "Mp3PlayerSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [After(Test)]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    private string CopyAsset(string name)
    {
        var path = Path.Combine(directory, name);
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "Tagging", "Assets", name), path);
        return path;
    }

    [Test]
    [Arguments("silence.m4a")]
    [Arguments("silence-fragmented.m4a")]
    [Arguments("silence.mp3")]
    public async Task Writes_every_mapped_field(string asset)
    {
        var path = CopyAsset(asset);

        TrackTagWriter.Write(path, Full, new AlbumArt(JpegBytes, "image/jpeg"));

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        using (Assert.Multiple())
        {
            await Assert.That(tag.Title).IsEqualTo("Under Pressure");
            await Assert.That(tag.Performers).IsEquivalentTo(new[] { "Queen, David Bowie" }, CollectionOrdering.Matching);
            await Assert.That(tag.Album).IsEqualTo("Hot Space");
            await Assert.That(tag.AlbumArtists).IsEquivalentTo(new[] { "Queen" }, CollectionOrdering.Matching);
            await Assert.That(tag.Year).IsEqualTo(1982);
            await Assert.That(tag.Track).IsEqualTo(11);
            await Assert.That(tag.Disc).IsEqualTo(1);
            await Assert.That(tag.Genres).IsEquivalentTo(new[] { "classic rock", "glam rock" }, CollectionOrdering.Matching);
            await Assert.That(tag.ISRC).IsEqualTo("GBUM71029604");
            await Assert.That(tag.Publisher).IsEqualTo("EMI");
            await Assert.That(tag.Copyright).IsEqualTo("© 1982 Queen Productions Ltd");
            await Assert.That(tag.BeatsPerMinute).IsEqualTo(114);
            await Assert.That(tag.InitialKey).IsEqualTo("D");
            await Assert.That(tag.Pictures).Count().IsEqualTo(1);
            await Assert.That(tag.Pictures[0].Type).IsEqualTo(PictureType.FrontCover);
            await Assert.That(tag.Pictures[0].MimeType).IsEqualTo("image/jpeg");
            await Assert.That(tag.Pictures[0].Data.Data).IsEquivalentTo(JpegBytes, CollectionOrdering.Matching);
        }

        if (file is TagLib.Mpeg4.File)
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple);
            await Assert.That(apple.GetText(new ReadOnlyByteVector(0xA9, (byte)'d', (byte)'a', (byte)'y'))).IsEquivalentTo(new[] { "1982-05-21" }, CollectionOrdering.Matching);
            await Assert.That(apple.DataBoxes(RatingBox).Single().Data.Data).IsEquivalentTo(new byte[] { 1 }, CollectionOrdering.Matching);
        }
        else
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
            await Assert.That(id3.Version).IsEqualTo(4);
            await Assert.That(id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TDRC").Single().Text).IsEquivalentTo(new[] { "1982-05-21" }, CollectionOrdering.Matching);
        }

        await Assert.That(file.Properties.Codecs).IsNotEmpty().Because("The audio stream must still be readable.");
    }

    [Test]
    [Arguments("silence.m4a")]
    [Arguments("silence.mp3")]
    public async Task Missing_fields_are_removed_and_existing_art_is_kept(string asset)
    {
        var path = CopyAsset(asset);
        TrackTagWriter.Write(path, Full, new AlbumArt(JpegBytes, "image/jpeg"));

        TrackTagWriter.Write(path, new TrackMetadata { Title = "Untitled", Artists = ["Solo"] }, art: null);

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        using (Assert.Multiple())
        {
            await Assert.That(tag.Title).IsEqualTo("Untitled");
            await Assert.That(tag.Performers).IsEquivalentTo(new[] { "Solo" }, CollectionOrdering.Matching);
            await Assert.That(tag.Album).IsNull();
            await Assert.That(tag.AlbumArtists).IsEmpty();
            await Assert.That(tag.Year).IsZero();
            await Assert.That(tag.Track).IsZero();
            await Assert.That(tag.Disc).IsZero();
            await Assert.That(tag.Genres).IsEmpty();
            await Assert.That(tag.ISRC).IsNull();
            await Assert.That(tag.Publisher).IsNull();
            await Assert.That(tag.Copyright).IsNull();
            await Assert.That(tag.BeatsPerMinute).IsZero();
            await Assert.That(tag.InitialKey).IsNull();
            await Assert.That(tag.Pictures).Count().IsEqualTo(1);
        }

        if (file is TagLib.Mpeg4.File)
            await Assert.That(((TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple)).DataBoxes(RatingBox)).IsEmpty();
    }

    [Test]
    public async Task Unsupported_file_type_is_rejected()
    {
        var path = Path.Combine(directory, "notes.txt");
        System.IO.File.WriteAllText(path, "hello");

        await Assert.That(() => TrackTagWriter.Write(path, Full, null)).Throws<Exception>();
    }

    [Test]
    public async Task WriteAsync_embeds_art_from_the_fetcher()
    {
        var path = CopyAsset("silence.m4a");
        var fetcher = new FakeArtFetcher(new AlbumArt(JpegBytes, "image/jpeg"));

        var result = await new TrackTagWriter(fetcher).WriteAsync(path, Full);

        await Assert.That(result.CoverArtEmbedded).IsTrue();
        await Assert.That(fetcher.RequestedUrls).IsEquivalentTo(new[] { "https://i.scdn.co/image/abc" }, CollectionOrdering.Matching);
        using var file = TagLib.File.Create(path);
        await Assert.That(file.Tag.Pictures).Count().IsEqualTo(1);
    }

    [Test]
    public async Task WriteAsync_still_tags_when_art_is_unavailable()
    {
        var path = CopyAsset("silence.mp3");

        var result = await new TrackTagWriter(new FakeArtFetcher(null)).WriteAsync(path, Full);

        await Assert.That(result.CoverArtEmbedded).IsFalse();
        using var file = TagLib.File.Create(path);
        await Assert.That(file.Tag.Title).IsEqualTo("Under Pressure");
        await Assert.That(file.Tag.Pictures).IsEmpty();
    }

    [Test]
    public async Task WriteAsync_does_not_fetch_without_an_image_url()
    {
        var path = CopyAsset("silence.m4a");
        var fetcher = new FakeArtFetcher(new AlbumArt(JpegBytes, "image/jpeg"));

        var result = await new TrackTagWriter(fetcher).WriteAsync(path, Full with { AlbumImageUrl = "" });

        await Assert.That(result.CoverArtEmbedded).IsFalse();
        await Assert.That(fetcher.RequestedUrls).IsEmpty();
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
