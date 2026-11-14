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
    public void Writes_every_mapped_field(string asset)
    {
        var path = CopyAsset(asset);

        TrackTagWriter.Write(path, Full, new AlbumArt(JpegBytes, "image/jpeg"));

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.ShouldSatisfyAllConditions(
            t => t.Title.ShouldBe("Under Pressure"),
            t => t.Performers.ShouldBe(new[] { "Queen, David Bowie" }),
            t => t.Album.ShouldBe("Hot Space"),
            t => t.AlbumArtists.ShouldBe(new[] { "Queen" }),
            t => t.Year.ShouldBe(1982u),
            t => t.Track.ShouldBe(11u),
            t => t.Disc.ShouldBe(1u),
            t => t.Genres.ShouldBe(new[] { "classic rock", "glam rock" }),
            t => t.ISRC.ShouldBe("GBUM71029604"),
            t => t.Publisher.ShouldBe("EMI"),
            t => t.Copyright.ShouldBe("© 1982 Queen Productions Ltd"),
            t => t.BeatsPerMinute.ShouldBe(114u),
            t => t.InitialKey.ShouldBe("D"),
            t => t.Pictures.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                p => p.Type.ShouldBe(PictureType.FrontCover),
                p => p.MimeType.ShouldBe("image/jpeg"),
                p => p.Data.Data.ShouldBe(JpegBytes)));

        if (file is TagLib.Mpeg4.File)
        {
            var apple = file.GetTag(TagTypes.Apple).ShouldBeOfType<TagLib.Mpeg4.AppleTag>();
            apple.GetText(new ReadOnlyByteVector(0xA9, (byte)'d', (byte)'a', (byte)'y')).ShouldBe(new[] { "1982-05-21" });
            apple.DataBoxes(RatingBox).ShouldHaveSingleItem().Data.Data.ShouldBe(new byte[] { 1 });
        }
        else
        {
            var id3 = file.GetTag(TagTypes.Id3v2).ShouldBeOfType<TagLib.Id3v2.Tag>();
            id3.Version.ShouldBe((byte)4);
            id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TDRC").ShouldHaveSingleItem().Text.ShouldBe(new[] { "1982-05-21" });
        }

        file.Properties.Codecs.ShouldNotBeEmpty(customMessage: "The audio stream must still be readable.");
    }

    [Test]
    [Arguments("silence.m4a")]
    [Arguments("silence.mp3")]
    public void Missing_fields_are_removed_and_existing_art_is_kept(string asset)
    {
        var path = CopyAsset(asset);
        TrackTagWriter.Write(path, Full, new AlbumArt(JpegBytes, "image/jpeg"));

        TrackTagWriter.Write(path, new TrackMetadata { Title = "Untitled", Artists = ["Solo"] }, art: null);

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.ShouldSatisfyAllConditions(
            t => t.Title.ShouldBe("Untitled"),
            t => t.Performers.ShouldBe(new[] { "Solo" }),
            t => t.Album.ShouldBeNull(),
            t => t.AlbumArtists.ShouldBeEmpty(),
            t => t.Year.ShouldBe(0u),
            t => t.Track.ShouldBe(0u),
            t => t.Disc.ShouldBe(0u),
            t => t.Genres.ShouldBeEmpty(),
            t => t.ISRC.ShouldBeNull(),
            t => t.Publisher.ShouldBeNull(),
            t => t.Copyright.ShouldBeNull(),
            t => t.BeatsPerMinute.ShouldBe(0u),
            t => t.InitialKey.ShouldBeNull(),
            t => t.Pictures.ShouldHaveSingleItem());

        if (file is TagLib.Mpeg4.File)
            file.GetTag(TagTypes.Apple).ShouldBeOfType<TagLib.Mpeg4.AppleTag>().DataBoxes(RatingBox).ShouldBeEmpty();
    }

    [Test]
    public void Unsupported_file_type_is_rejected()
    {
        var path = Path.Combine(directory, "notes.txt");
        System.IO.File.WriteAllText(path, "hello");

        Should.Throw<Exception>(() => TrackTagWriter.Write(path, Full, null));
    }

    [Test]
    public async Task WriteAsync_embeds_art_from_the_fetcher()
    {
        var path = CopyAsset("silence.m4a");
        var fetcher = new FakeArtFetcher(new AlbumArt(JpegBytes, "image/jpeg"));

        var result = await new TrackTagWriter(fetcher).WriteAsync(path, Full);

        result.CoverArtEmbedded.ShouldBeTrue();
        fetcher.RequestedUrls.ShouldBe(new[] { "https://i.scdn.co/image/abc" });
        using var file = TagLib.File.Create(path);
        file.Tag.Pictures.ShouldHaveSingleItem();
    }

    [Test]
    public async Task WriteAsync_still_tags_when_art_is_unavailable()
    {
        var path = CopyAsset("silence.mp3");

        var result = await new TrackTagWriter(new FakeArtFetcher(null)).WriteAsync(path, Full);

        result.CoverArtEmbedded.ShouldBeFalse();
        using var file = TagLib.File.Create(path);
        file.Tag.Title.ShouldBe("Under Pressure");
        file.Tag.Pictures.ShouldBeEmpty();
    }

    [Test]
    public async Task WriteAsync_does_not_fetch_without_an_image_url()
    {
        var path = CopyAsset("silence.m4a");
        var fetcher = new FakeArtFetcher(new AlbumArt(JpegBytes, "image/jpeg"));

        var result = await new TrackTagWriter(fetcher).WriteAsync(path, Full with { AlbumImageUrl = "" });

        result.CoverArtEmbedded.ShouldBeFalse();
        fetcher.RequestedUrls.ShouldBeEmpty();
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
