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

        var metadata = TrackMetadata.FromExportedTrack(ExportifyCsv.Parse(csv).Tracks.ShouldHaveSingleItem());

        metadata.ShouldSatisfyAllConditions(
            m => m.Title.ShouldBe("Under Pressure"),
            m => m.Artists.ShouldBe(["Queen", "David Bowie"]),
            m => m.AlbumArtists.ShouldBeEmpty(customMessage: "One track can't tell its album's artist."),
            m => m.Album.ShouldBe("Hot Space"),
            m => m.Year.ShouldBe(1982),
            m => m.NormalizedReleaseDate.ShouldBe("1982-05-21"),
            m => m.TrackNumber.ShouldBeNull(),
            m => m.DiscNumber.ShouldBeNull(),
            m => m.Genres.ShouldBe(["glam rock", "classic rock", "rock"]),
            m => m.Isrc.ShouldBe("USRC2"),
            m => m.Label.ShouldBe("EMI"),
            m => m.Explicit.ShouldBe(true),
            m => m.BeatsPerMinute.ShouldBe(113),
            m => m.InitialKey.ShouldBe("Am"),
            m => m.AlbumImageUrl.ShouldBeEmpty());
    }

    [Test]
    public void Maps_exportify_app_row_with_album_and_audio_features_data()
    {
        const string csv =
            "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\",\"Artist Genres\",\"Key\",\"Mode\",\"Tempo\",\"Album Genres\",\"Label\",\"Copyrights\"\n" +
            "\"spotify:track:1\",\"Song\",\"a\",\"Earth\\, Wind & Fire, Chaka Khan\",\"spotify:album:1\",\"Album\",\"x\",\"Earth\\, Wind & Fire, Various\",\"1979-06\",\"https://i.scdn.co/image/abc\",\"2\",\"7\",\"1000\",\"\",\"false\",\"1\",\"X\",\"\",\"2020-01-01T00:00:00Z\",\"funk,disco\",\"1\",\"1\",\"120.0\",\"Disco\",\"Columbia\",\"C 1979 Columbia, P 1979 Columbia\"\n";

        var metadata = TrackMetadata.FromExportedTrack(ExportifyCsv.Parse(csv).Tracks.ShouldHaveSingleItem());

        metadata.ShouldSatisfyAllConditions(
            m => m.AlbumArtists.ShouldBe(["Earth, Wind & Fire", "Various"]),
            m => m.PrimaryAlbumArtist.ShouldBe("Earth, Wind & Fire"),
            m => m.NormalizedReleaseDate.ShouldBe("1979-06"),
            m => m.AlbumImageUrl.ShouldBe("https://i.scdn.co/image/abc"),
            m => m.DiscNumber.ShouldBe(2),
            m => m.TrackNumber.ShouldBe(7),
            m => m.Explicit.ShouldBe(false),
            m => m.Genres.ShouldBe(["funk", "disco"], customMessage: "Duplicates across artist and album genres are dropped."),
            m => m.InitialKey.ShouldBe("C#"),
            m => m.BeatsPerMinute.ShouldBe(120),
            m => m.Label.ShouldBe("Columbia"),
            m => m.Copyright.ShouldBe("C 1979 Columbia, P 1979 Columbia"));
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

        metadata.Select(m => (m.Title, m.TrackNumber)).ShouldBe([("First", (int?)1), ("Second", 2), ("Third", 3)]);
    }

    [Test]
    public void Track_number_column_wins_over_album_order_and_playlist_tracks_have_none()
    {
        const string csv =
            "Track URI,Track Name,Artist Name(s),Album Name,Track Number,Track Duration (ms)\n" +
            "spotify:track:1,Song,A,Album,7,1\n" +
            "spotify:track:2,Other,A,Album,,1\n";
        var tracks = ExportifyCsv.Parse(csv).Tracks;

        TrackMetadata.FromAlbum(tracks).Select(m => m.TrackNumber).ShouldBe([7, 2]);
        TrackMetadata.FromExportedTrack(tracks[1]).TrackNumber.ShouldBeNull();
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

        metadata.Select(m => m.AlbumArtists).ShouldAllBe(artists => artists.SequenceEqual(new[] { "Various Artists" }));
    }

    [Test]
    public void Soundtrack_by_a_composer_with_guest_tracks_gets_the_composer()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Hans Zimmer", "Hans Zimmer;Lisa Gerrard", "Lisa Gerrard;Hans Zimmer"));

        metadata.Select(m => m.AlbumArtists).ShouldAllBe(artists => artists.SequenceEqual(new[] { "Hans Zimmer" }));
    }

    [Test]
    public void Guest_first_track_stays_with_the_albums_artist()
    {
        var metadata = TrackMetadata.FromAlbum(Album("Queen", "David Bowie;Queen", "QUEEN"));

        metadata.Select(m => m.AlbumArtists).ShouldAllBe(artists => artists.SequenceEqual(new[] { "Queen" }));
        metadata[1].Artists.ShouldBe(["David Bowie", "Queen"], customMessage: "Track artists are unchanged.");
    }

    [Test]
    public void Album_artist_column_wins_over_the_derived_one()
    {
        const string csv =
            "Track URI,Track Name,Artist Name(s),Album Name,Album Artist Name(s),Track Duration (ms)\n" +
            "spotify:track:1,A,X,Album,Label Sampler,1\n" +
            "spotify:track:2,B,Y,Album,Label Sampler,1\n";

        TrackMetadata.FromAlbum(ExportifyCsv.Parse(csv).Tracks).Select(m => m.AlbumArtists).ShouldAllBe(artists => artists.SequenceEqual(new[] { "Label Sampler" }));
    }

    [Test]
    [Arguments("", null)]
    [Arguments("0000", null)]
    [Arguments("1999", 1999)]
    [Arguments("1999-02", 1999)]
    [Arguments("1999-02-03", 1999)]
    [Arguments("19990203", null)]
    public void Parses_year(string releaseDate, int? year)
    {
        TrackMetadata.ParseYear(releaseDate).ShouldBe(year);
    }

    [Test]
    public void Unknown_key_and_tempo_are_blank()
    {
        var metadata = new TrackMetadata { Key = -1, Mode = 1, Tempo = 0 };

        metadata.InitialKey.ShouldBeEmpty();
        metadata.BeatsPerMinute.ShouldBeNull();
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

        first.ShouldNotBeNull().MimeType.ShouldBe("image/png");
        second.ShouldBeSameAs(first);
        handler.Requests.ShouldHaveSingleItem();
    }

    [Test]
    [Arguments("https://api.spotify.com/v1/albums/1")]
    [Arguments("http://i.scdn.co/image/abc")]
    [Arguments("not a url")]
    public async Task Refuses_anything_but_the_image_cdn(string url)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });

        var art = await new HttpAlbumArtFetcher(new HttpClient(handler)).FetchAsync(url);

        art.ShouldBeNull();
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task Failures_return_null_and_are_retried()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });
        var fetcher = new HttpAlbumArtFetcher(new HttpClient(handler));

        (await fetcher.FetchAsync("https://i.scdn.co/image/abc")).ShouldBeNull();
        (await fetcher.FetchAsync("https://i.scdn.co/image/abc")).ShouldNotBeNull();
    }

    [Test]
    public async Task Non_image_data_is_ignored()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });

        (await new HttpAlbumArtFetcher(new HttpClient(handler)).FetchAsync("https://i.scdn.co/image/abc")).ShouldBeNull();
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
