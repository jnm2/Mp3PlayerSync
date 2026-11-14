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
    public async Task Reads_exportify_net_export()
    {
        var playlist = ExportifyCsv.Parse(ExportifyNetCsv);

        await Assert.That(playlist.Tracks).Count().IsEqualTo(3);

        var september = playlist.Tracks[0];
        using (Assert.Multiple())
        {
            await Assert.That(september.Number).IsEqualTo(1);
            await Assert.That(september.Uri).IsEqualTo("spotify:track:1");
            await Assert.That(september.Title).IsEqualTo("September");
            await Assert.That(september.Artists).IsEquivalentTo(new[] { "Earth, Wind & Fire" }, CollectionOrdering.Matching);
            await Assert.That(september.Album).IsEqualTo("The Best Of Earth, Wind & Fire, Vol. 1");
            await Assert.That(september.Duration).IsEqualTo(TimeSpan.FromMilliseconds(215093));
            await Assert.That(september.DurationText).IsEqualTo("3:35");
            await Assert.That(september.AddedAt).IsEqualTo(new DateTimeOffset(2024, 3, 1, 12, 34, 56, TimeSpan.Zero));
            await Assert.That(september.Explicit).IsFalse();
            await Assert.That(september.Genres).IsEqualTo("disco,funk");
        }

        await Assert.That(playlist.Tracks[1].Artists).IsEquivalentTo(new[] { "Queen", "David Bowie" }, CollectionOrdering.Matching);
        await Assert.That(playlist.Tracks[1].ArtistsText).IsEqualTo("Queen, David Bowie");
    }

    [Test]
    public async Task Missing_track_values_written_as_undefined_are_blank()
    {
        var removed = ExportifyCsv.Parse(ExportifyNetCsv).Tracks[2];

        using (Assert.Multiple())
        {
            await Assert.That(removed.Title).IsEmpty();
            await Assert.That(removed.Album).IsEmpty();
            await Assert.That(removed.Artists).IsEmpty();
            await Assert.That(removed.Duration).IsNull();
        }
    }

    [Test]
    public async Task Reads_exportify_app_export_with_escaped_commas_in_artist_names()
    {
        const string csv =
            "\"Track URI\",\"Track Name\",\"Artist URI(s)\",\"Artist Name(s)\",\"Album URI\",\"Album Name\",\"Album Artist URI(s)\",\"Album Artist Name(s)\",\"Album Release Date\",\"Album Image URL\",\"Disc Number\",\"Track Number\",\"Track Duration (ms)\",\"Track Preview URL\",\"Explicit\",\"Popularity\",\"ISRC\",\"Added By\",\"Added At\"\n" +
            "\"spotify:track:1\",\"Song\",\"a, b\",\"Earth\\, Wind & Fire, Chaka Khan\",\"spotify:album:1\",\"Album\",\"\",\"\",\"2000\",\"\",\"1\",\"1\",\"3725000\",\"\",\"false\",\"1\",\"X\",\"\",\"2020-01-01T00:00:00Z\"\n";

        var track = ExportifyCsv.Parse(csv).Tracks.Single();

        await Assert.That(track.Artists).IsEquivalentTo(new[] { "Earth, Wind & Fire", "Chaka Khan" }, CollectionOrdering.Matching);
        await Assert.That(track.DurationText).IsEqualTo("1:02:05");
        await Assert.That(track.ReleaseDate).IsEqualTo("2000");
    }

    [Test]
    public async Task Total_duration_sums_known_durations()
    {
        await Assert.That(ExportifyCsv.Parse(ExportifyNetCsv).TotalDuration).IsEqualTo(TimeSpan.FromMilliseconds(215093 + 248440));
    }

    [Test]
    public async Task Rejects_csv_without_track_name_column()
    {
        await Assert.That(() => ExportifyCsv.Parse("Album URI,Album Name\nspotify:album:1,X\n")).ThrowsExactly<FormatException>();
        await Assert.That(() => ExportifyCsv.Parse("")).ThrowsExactly<FormatException>();
    }
}
