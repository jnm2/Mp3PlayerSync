using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class ExportifyCsvTests
{
    // Shaped exactly like exportify.net's PlaylistExporter.csvData output (including the BOM it prepends).
    private const string ExportifyNetCsv =
        "\uFEFFTrack URI,ISRC,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms),Popularity,Explicit,Added By,Added At,Genres,Record Label,Danceability,Energy,Key,Loudness,Mode,Speechiness,Acousticness,Instrumentalness,Liveness,Valence,Tempo,Time Signature\n" +
        "spotify:track:1,USRC1,\"September\",\"The Best Of Earth, Wind & Fire, Vol. 1\",\"Earth, Wind & Fire\",1978-11-23,215093,80,false,someone,2024-03-01T12:34:56Z,\"disco,funk\",\"\"\"Legacy\"\" Recordings\"\n" +
        "spotify:track:2,USRC2,\"Under Pressure\",\"Hot Space\",\"Queen;David Bowie\",1982-05-21,248440,77,true,someone,2024-03-02T00:00:00Z,\"\",\"undefined\",0.67,0.71,2,-10.4,1,0.04,0.2,0,0.1,0.5,113.5,4\n" +
        ",,\"undefined\",\"undefined\",\"\",,,,,someone,2024-03-03T00:00:00Z,\"\",\"undefined\"\n";

    [Test]
    public void Reads_exportify_net_export()
    {
        var playlist = ExportifyCsv.Parse(ExportifyNetCsv);

        Assert.That(playlist.Tracks, Has.Count.EqualTo(3));

        var september = playlist.Tracks[0];
        Assert.Multiple(() =>
        {
            Assert.That(september.Number, Is.EqualTo(1));
            Assert.That(september.Uri, Is.EqualTo("spotify:track:1"));
            Assert.That(september.Title, Is.EqualTo("September"));
            Assert.That(september.Artists, Is.EqualTo(new[] { "Earth, Wind & Fire" }));
            Assert.That(september.Album, Is.EqualTo("The Best Of Earth, Wind & Fire, Vol. 1"));
            Assert.That(september.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(215093)));
            Assert.That(september.DurationText, Is.EqualTo("3:35"));
            Assert.That(september.AddedAt, Is.EqualTo(new DateTimeOffset(2024, 3, 1, 12, 34, 56, TimeSpan.Zero)));
            Assert.That(september.Explicit, Is.False);
            Assert.That(september.Genres, Is.EqualTo("disco,funk"));
        });

        Assert.That(playlist.Tracks[1].Artists, Is.EqualTo(new[] { "Queen", "David Bowie" }));
        Assert.That(playlist.Tracks[1].ArtistsText, Is.EqualTo("Queen, David Bowie"));
    }

    [Test]
    public void Missing_track_values_written_as_undefined_are_blank()
    {
        var removed = ExportifyCsv.Parse(ExportifyNetCsv).Tracks[2];

        Assert.Multiple(() =>
        {
            Assert.That(removed.Title, Is.Empty);
            Assert.That(removed.Album, Is.Empty);
            Assert.That(removed.Artists, Is.Empty);
            Assert.That(removed.Duration, Is.Null);
        });
    }

    [Test]
    public void Reads_exportify_app_export_with_escaped_commas_in_artist_names()
    {
        const string csv =
            "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\"\n" +
            "\"spotify:track:1\",\"Song\",\"a, b\",\"Earth\\, Wind & Fire, Chaka Khan\",\"spotify:album:1\",\"Album\",\"\",\"\",\"2000\",\"\",\"1\",\"1\",\"3725000\",\"\",\"false\",\"1\",\"X\",\"\",\"2020-01-01T00:00:00Z\"\n";

        var track = ExportifyCsv.Parse(csv).Tracks.Single();

        Assert.That(track.Artists, Is.EqualTo(new[] { "Earth, Wind & Fire", "Chaka Khan" }));
        Assert.That(track.DurationText, Is.EqualTo("1:02:05"));
        Assert.That(track.ReleaseDate, Is.EqualTo("2000"));
    }

    [Test]
    public void Total_duration_sums_known_durations()
    {
        Assert.That(ExportifyCsv.Parse(ExportifyNetCsv).TotalDuration, Is.EqualTo(TimeSpan.FromMilliseconds(215093 + 248440)));
    }

    [Test]
    public void Rejects_csv_without_track_name_column()
    {
        Assert.That(() => ExportifyCsv.Parse("Album URI,Album Name\nspotify:album:1,X\n"), Throws.TypeOf<FormatException>());
        Assert.That(() => ExportifyCsv.Parse(""), Throws.TypeOf<FormatException>());
    }
}
