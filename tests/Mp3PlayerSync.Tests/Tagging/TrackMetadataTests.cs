using System.Net;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Tagging;

namespace Mp3PlayerSync.Tests.Tagging;

public class TrackMetadataTests
{
    [Test]
    public void Maps_exportify_net_row()
    {
        const string csv =
            "Track URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms),Popularity,Explicit,Added By,Added At,Genres,Record Label,Danceability,Energy,Key,Loudness,Mode,Speechiness,Acousticness,Instrumentalness,Liveness,Valence,Tempo,Time Signature\n" +
            "spotify:track:2,USRC2,Under Pressure,Hot Space,Queen;David Bowie,1982-05-21,248440,77,true,me,2024-03-02T00:00:00Z,\"glam rock,classic rock,rock\",EMI,0.67,0.71,9,-10.4,0,0.04,0.2,0,0.1,0.5,113.4,4\n";

        var metadata = TrackMetadata.FromExportedTrack(ExportifyCsv.Parse(csv).Tracks.Single());

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Title, Is.EqualTo("Under Pressure"));
            Assert.That(metadata.Artists, Is.EqualTo(new[] { "Queen", "David Bowie" }));
            Assert.That(metadata.AlbumArtists, Is.Empty, "One track can't tell its album's artist.");
            Assert.That(metadata.Album, Is.EqualTo("Hot Space"));
            Assert.That(metadata.Year, Is.EqualTo(1982));
            Assert.That(metadata.NormalizedReleaseDate, Is.EqualTo("1982-05-21"));
            Assert.That(metadata.TrackNumber, Is.Null);
            Assert.That(metadata.DiscNumber, Is.Null);
            Assert.That(metadata.Genres, Is.EqualTo(new[] { "glam rock", "classic rock", "rock" }));
            Assert.That(metadata.Isrc, Is.EqualTo("USRC2"));
            Assert.That(metadata.Label, Is.EqualTo("EMI"));
            Assert.That(metadata.Explicit, Is.True);
            Assert.That(metadata.BeatsPerMinute, Is.EqualTo(113));
            Assert.That(metadata.InitialKey, Is.EqualTo("Am"));
            Assert.That(metadata.AlbumImageUrl, Is.Empty);
        });
    }

    [Test]
    public void Maps_exportify_app_row_with_album_and_audio_features_data()
    {
        const string csv =
            "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\",\"Artist Genres\",\"Key\",\"Mode\",\"Tempo\",\"Album Genres\",\"Label\",\"Copyrights\"\n" +
            "\"spotify:track:1\",\"Song\",\"a\",\"Earth\\, Wind & Fire, Chaka Khan\",\"spotify:album:1\",\"Album\",\"x\",\"Earth\\, Wind & Fire, Various\",\"1979-06\",\"https://i.scdn.co/image/abc\",\"2\",\"7\",\"1000\",\"\",\"false\",\"1\",\"X\",\"\",\"2020-01-01T00:00:00Z\",\"funk,disco\",\"1\",\"1\",\"120.0\",\"Disco\",\"Columbia\",\"C 1979 Columbia, P 1979 Columbia\"\n";

        var metadata = TrackMetadata.FromExportedTrack(ExportifyCsv.Parse(csv).Tracks.Single());

        Assert.Multiple(() =>
        {
            Assert.That(metadata.AlbumArtists, Is.EqualTo(new[] { "Earth, Wind & Fire", "Various" }));
            Assert.That(metadata.PrimaryAlbumArtist, Is.EqualTo("Earth, Wind & Fire"));
            Assert.That(metadata.NormalizedReleaseDate, Is.EqualTo("1979-06"));
            Assert.That(metadata.AlbumImageUrl, Is.EqualTo("https://i.scdn.co/image/abc"));
            Assert.That(metadata.DiscNumber, Is.EqualTo(2));
            Assert.That(metadata.TrackNumber, Is.EqualTo(7));
            Assert.That(metadata.Explicit, Is.False);
            Assert.That(metadata.Genres, Is.EqualTo(new[] { "funk", "disco" }), "Duplicates across artist and album genres are dropped.");
            Assert.That(metadata.InitialKey, Is.EqualTo("C#"));
            Assert.That(metadata.BeatsPerMinute, Is.EqualTo(120));
            Assert.That(metadata.Label, Is.EqualTo("Columbia"));
            Assert.That(metadata.Copyright, Is.EqualTo("C 1979 Columbia, P 1979 Columbia"));
        });
    }

    [Test]
    public void Album_tracks_are_numbered_by_their_spotify_album_order()
    {
        const string csv =
            "Track URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms)\n" +
            "spotify:track:1,A,First,Hot Space,Queen,1982,1\n" +
            "spotify:track:2,B,Second,Hot Space,Queen,1982,1\n" +
            "spotify:track:3,C,Third,Hot Space,Queen,1982,1\n";

        var metadata = TrackMetadata.FromAlbum(ExportifyCsv.Parse(csv).Tracks);

        Assert.That(metadata.Select(m => (m.Title, m.TrackNumber)), Is.EqualTo(new[] { ("First", (int?)1), ("Second", 2), ("Third", 3) }));
    }

    [Test]
    public void Track_number_column_wins_over_album_order_and_playlist_tracks_have_none()
    {
        const string csv =
            "Track URI,Track Name,Artist Name(s),Album Name,Track Number,Track Duration (ms)\n" +
            "spotify:track:1,Song,A,Album,7,1\n" +
            "spotify:track:2,Other,A,Album,,1\n";
        var tracks = ExportifyCsv.Parse(csv).Tracks;

        Assert.That(TrackMetadata.FromAlbum(tracks).Select(m => m.TrackNumber), Is.EqualTo(new int?[] { 7, 2 }));
        Assert.That(TrackMetadata.FromExportedTrack(tracks[1]).TrackNumber, Is.Null);
    }

    private static IReadOnlyList<ExportedTrack> Album(params string[] artistCredits)
    {
        var csv = "Track URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms)\n" +
            string.Concat(artistCredits.Select((artists, i) => $"spotify:track:{i},X{i},Song {i + 1},The Album,\"{artists}\",2014,1\n"));
        return ExportifyCsv.Parse(csv).Tracks;
    }

    [Test]
    public void Compilation_gets_various_artists_on_every_track()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Blue Swede", "Redbone", "Raspberries"));

        Assert.That(metadata.Select(m => m.AlbumArtists), Is.All.EqualTo(new[] { "Various Artists" }));
    }

    [Test]
    public void Soundtrack_by_a_composer_with_guest_tracks_gets_the_composer()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Hans Zimmer", "Hans Zimmer;Lisa Gerrard", "Lisa Gerrard;Hans Zimmer"));

        Assert.That(metadata.Select(m => m.AlbumArtists), Is.All.EqualTo(new[] { "Hans Zimmer" }));
    }

    [Test]
    public void Guest_first_track_stays_with_the_albums_artist()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Queen", "David Bowie;Queen", "QUEEN"));

        Assert.That(metadata.Select(m => m.AlbumArtists), Is.All.EqualTo(new[] { "Queen" }));
        Assert.That(metadata[1].Artists, Is.EqualTo(new[] { "David Bowie", "Queen" }), "Track artists are unchanged.");
    }

    [Test]
    public void Album_artist_column_wins_over_the_derived_one()
    {
        const string csv =
            "Track URI,Track Name,Artist Name(s),Album Name,Album Artist Name(s),Track Duration (ms)\n" +
            "spotify:track:1,A,X,Album,Label Sampler,1\n" +
            "spotify:track:2,B,Y,Album,Label Sampler,1\n";

        Assert.That(TrackMetadata.FromAlbum(ExportifyCsv.Parse(csv).Tracks).Select(m => m.AlbumArtists), Is.All.EqualTo(new[] { "Label Sampler" }));
    }

    [TestCase("", null)]
    [TestCase("0000", null)]
    [TestCase("1999", 1999)]
    [TestCase("1999-02", 1999)]
    [TestCase("1999-02-03", 1999)]
    [TestCase("19990203", null)]
    public void Parses_year(string releaseDate, int? year)
    {
        Assert.That(TrackMetadata.ParseYear(releaseDate), Is.EqualTo(year));
    }

    [Test]
    public void Unknown_key_and_tempo_are_blank()
    {
        var metadata = new TrackMetadata { Key = -1, Mode = 1, Tempo = 0 };

        Assert.That(metadata.InitialKey, Is.Empty);
        Assert.That(metadata.BeatsPerMinute, Is.Null);
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

        Assert.That(first?.MimeType, Is.EqualTo("image/png"));
        Assert.That(second, Is.SameAs(first));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [TestCase("https://api.spotify.com/v1/albums/1")]
    [TestCase("http://i.scdn.co/image/abc")]
    [TestCase("not a url")]
    public async Task Refuses_anything_but_the_image_cdn(string url)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });

        var art = await new HttpAlbumArtFetcher(new HttpClient(handler)).FetchAsync(url);

        Assert.That(art, Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Failures_return_null_and_are_retried()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });
        var fetcher = new HttpAlbumArtFetcher(new HttpClient(handler));

        Assert.That(await fetcher.FetchAsync("https://i.scdn.co/image/abc"), Is.Null);
        Assert.That(await fetcher.FetchAsync("https://i.scdn.co/image/abc"), Is.Not.Null);
    }

    [Test]
    public async Task Non_image_data_is_ignored()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });

        Assert.That(await new HttpAlbumArtFetcher(new HttpClient(handler)).FetchAsync("https://i.scdn.co/image/abc"), Is.Null);
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
