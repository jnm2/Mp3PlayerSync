using System.Net;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Tagging;

namespace Mp3PlayerSync.Tests.Tagging;

public class TrackMetadataTests
{
    [Test]
    public async Task Maps_exportify_net_row()
    {
        const string csv =
            "Track URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms),Popularity,Explicit,Added By,Added At,Genres,Record Label,Danceability,Energy,Key,Loudness,Mode,Speechiness,Acousticness,Instrumentalness,Liveness,Valence,Tempo,Time Signature\n" +
            "spotify:track:2,USRC2,Under Pressure,Hot Space,Queen;David Bowie,1982-05-21,248440,77,true,me,2024-03-02T00:00:00Z,\"glam rock,classic rock,rock\",EMI,0.67,0.71,9,-10.4,0,0.04,0.2,0,0.1,0.5,113.4,4\n";

        var metadata = TrackMetadata.FromExportedTrack(ExportifyCsv.Parse(csv).Tracks.Single());

        using (Assert.Multiple())
        {
            await Assert.That(metadata.Title).IsEqualTo("Under Pressure");
            await Assert.That(metadata.Artists).IsEquivalentTo(new[] { "Queen", "David Bowie" }, CollectionOrdering.Matching);
            await Assert.That(metadata.AlbumArtists).IsEmpty().Because("One track can't tell its album's artist.");
            await Assert.That(metadata.Album).IsEqualTo("Hot Space");
            await Assert.That(metadata.Year).IsEqualTo(1982);
            await Assert.That(metadata.NormalizedReleaseDate).IsEqualTo("1982-05-21");
            await Assert.That(metadata.TrackNumber).IsNull();
            await Assert.That(metadata.DiscNumber).IsNull();
            await Assert.That(metadata.Genres).IsEquivalentTo(new[] { "glam rock", "classic rock", "rock" }, CollectionOrdering.Matching);
            await Assert.That(metadata.Isrc).IsEqualTo("USRC2");
            await Assert.That(metadata.Label).IsEqualTo("EMI");
            await Assert.That(metadata.Explicit).IsTrue();
            await Assert.That(metadata.BeatsPerMinute).IsEqualTo(113);
            await Assert.That(metadata.InitialKey).IsEqualTo("Am");
            await Assert.That(metadata.AlbumImageUrl).IsEmpty();
        }
    }

    [Test]
    public async Task Maps_exportify_app_row_with_album_and_audio_features_data()
    {
        const string csv =
            "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\",\"Artist Genres\",\"Key\",\"Mode\",\"Tempo\",\"Album Genres\",\"Label\",\"Copyrights\"\n" +
            "\"spotify:track:1\",\"Song\",\"a\",\"Earth\\, Wind & Fire, Chaka Khan\",\"spotify:album:1\",\"Album\",\"x\",\"Earth\\, Wind & Fire, Various\",\"1979-06\",\"https://i.scdn.co/image/abc\",\"2\",\"7\",\"1000\",\"\",\"false\",\"1\",\"X\",\"\",\"2020-01-01T00:00:00Z\",\"funk,disco\",\"1\",\"1\",\"120.0\",\"Disco\",\"Columbia\",\"C 1979 Columbia, P 1979 Columbia\"\n";

        var metadata = TrackMetadata.FromExportedTrack(ExportifyCsv.Parse(csv).Tracks.Single());

        using (Assert.Multiple())
        {
            await Assert.That(metadata.AlbumArtists).IsEquivalentTo(new[] { "Earth, Wind & Fire", "Various" }, CollectionOrdering.Matching);
            await Assert.That(metadata.PrimaryAlbumArtist).IsEqualTo("Earth, Wind & Fire");
            await Assert.That(metadata.NormalizedReleaseDate).IsEqualTo("1979-06");
            await Assert.That(metadata.AlbumImageUrl).IsEqualTo("https://i.scdn.co/image/abc");
            await Assert.That(metadata.DiscNumber).IsEqualTo(2);
            await Assert.That(metadata.TrackNumber).IsEqualTo(7);
            await Assert.That(metadata.Explicit).IsFalse();
            await Assert.That(metadata.Genres).IsEquivalentTo(new[] { "funk", "disco" }, CollectionOrdering.Matching).Because("Duplicates across artist and album genres are dropped.");
            await Assert.That(metadata.InitialKey).IsEqualTo("C#");
            await Assert.That(metadata.BeatsPerMinute).IsEqualTo(120);
            await Assert.That(metadata.Label).IsEqualTo("Columbia");
            await Assert.That(metadata.Copyright).IsEqualTo("C 1979 Columbia, P 1979 Columbia");
        }
    }

    [Test]
    public async Task Album_tracks_are_numbered_by_their_spotify_album_order()
    {
        const string csv =
            "Track URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms)\n" +
            "spotify:track:1,A,First,Hot Space,Queen,1982,1\n" +
            "spotify:track:2,B,Second,Hot Space,Queen,1982,1\n" +
            "spotify:track:3,C,Third,Hot Space,Queen,1982,1\n";

        var metadata = TrackMetadata.FromAlbum(ExportifyCsv.Parse(csv).Tracks);

        await Assert.That(metadata.Select(m => (m.Title, m.TrackNumber))).IsEquivalentTo(new[] { ("First", (int?)1), ("Second", 2), ("Third", 3) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Track_number_column_wins_over_album_order_and_playlist_tracks_have_none()
    {
        const string csv =
            "Track URI,Track Name,Artist Name(s),Album Name,Track Number,Track Duration (ms)\n" +
            "spotify:track:1,Song,A,Album,7,1\n" +
            "spotify:track:2,Other,A,Album,,1\n";
        var tracks = ExportifyCsv.Parse(csv).Tracks;

        await Assert.That(TrackMetadata.FromAlbum(tracks).Select(m => m.TrackNumber)).IsEquivalentTo(new int?[] { 7, 2 }, CollectionOrdering.Matching);
        await Assert.That(TrackMetadata.FromExportedTrack(tracks[1]).TrackNumber).IsNull();
    }

    private static IReadOnlyList<ExportedTrack> Album(params string[] artistCredits)
    {
        var csv = "Track URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms)\n" +
            string.Concat(artistCredits.Select((artists, i) => $"spotify:track:{i},X{i},Song {i + 1},The Album,\"{artists}\",2014,1\n"));
        return ExportifyCsv.Parse(csv).Tracks;
    }

    [Test]
    public async Task Compilation_gets_various_artists_on_every_track()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Blue Swede", "Redbone", "Raspberries"));

        await Assert.That(metadata.Select(m => m.AlbumArtists)).All(artists => artists.SequenceEqual(["Various Artists"]));
    }

    [Test]
    public async Task Soundtrack_by_a_composer_with_guest_tracks_gets_the_composer()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Hans Zimmer", "Hans Zimmer;Lisa Gerrard", "Lisa Gerrard;Hans Zimmer"));

        await Assert.That(metadata.Select(m => m.AlbumArtists)).All(artists => artists.SequenceEqual(["Hans Zimmer"]));
    }

    [Test]
    public async Task Guest_first_track_stays_with_the_albums_artist()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Queen", "David Bowie;Queen", "QUEEN"));

        await Assert.That(metadata.Select(m => m.AlbumArtists)).All(artists => artists.SequenceEqual(["Queen"]));
        await Assert.That(metadata[1].Artists).IsEquivalentTo(new[] { "David Bowie", "Queen" }, CollectionOrdering.Matching).Because("Track artists are unchanged.");
    }

    [Test]
    public async Task Album_artist_column_wins_over_the_derived_one()
    {
        const string csv =
            "Track URI,Track Name,Artist Name(s),Album Name,Album Artist Name(s),Track Duration (ms)\n" +
            "spotify:track:1,A,X,Album,Label Sampler,1\n" +
            "spotify:track:2,B,Y,Album,Label Sampler,1\n";

        await Assert.That(TrackMetadata.FromAlbum(ExportifyCsv.Parse(csv).Tracks).Select(m => m.AlbumArtists)).All(artists => artists.SequenceEqual(["Label Sampler"]));
    }

    [Test]
    [Arguments("", null)]
    [Arguments("0000", null)]
    [Arguments("1999", 1999)]
    [Arguments("1999-02", 1999)]
    [Arguments("1999-02-03", 1999)]
    [Arguments("19990203", null)]
    public async Task Parses_year(string releaseDate, int? year)
    {
        await Assert.That(TrackMetadata.ParseYear(releaseDate)).IsEqualTo(year);
    }

    [Test]
    public async Task Unknown_key_and_tempo_are_blank()
    {
        var metadata = new TrackMetadata { Key = -1, Mode = 1, Tempo = 0 };

        await Assert.That(metadata.InitialKey).IsEmpty();
        await Assert.That(metadata.BeatsPerMinute).IsNull();
    }
}

public class HttpAlbumArtFetcherTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Test]
    public async Task Fetches_from_the_image_cdn_once_per_url()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });
        var fetcher = new HttpAlbumArtFetcher(new HttpClient(handler));

        var first = await fetcher.FetchAsync("https://i.scdn.co/image/abc");
        var second = await fetcher.FetchAsync("https://i.scdn.co/image/abc");

        await Assert.That(first?.MimeType).IsEqualTo("image/png");
        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments("https://api.spotify.com/v1/albums/1")]
    [Arguments("http://i.scdn.co/image/abc")]
    [Arguments("not a url")]
    public async Task Refuses_anything_but_the_image_cdn(string url)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });

        var art = await new HttpAlbumArtFetcher(new HttpClient(handler)).FetchAsync(url);

        await Assert.That(art).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Failures_return_null_and_are_retried()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });
        var fetcher = new HttpAlbumArtFetcher(new HttpClient(handler));

        await Assert.That(await fetcher.FetchAsync("https://i.scdn.co/image/abc")).IsNull();
        await Assert.That(await fetcher.FetchAsync("https://i.scdn.co/image/abc")).IsNotNull();
    }

    [Test]
    public async Task Non_image_data_is_ignored()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });

        await Assert.That(await new HttpAlbumArtFetcher(new HttpClient(handler)).FetchAsync("https://i.scdn.co/image/abc")).IsNull();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
