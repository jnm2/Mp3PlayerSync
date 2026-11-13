using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Tests.Scanning;

public class PlayerIndexSnapshotTests
{
    private static ExportedTrack Track(string title, string[] artists, string album = "", int? seconds = null, string isrc = "", bool? isExplicit = null) =>
        new(1, "spotify:track:x", title, artists, album, seconds is { } s ? TimeSpan.FromSeconds(s) : null, "", null, "", isExplicit, isrc, "");

    private static IndexedFile File(string path, string title, string[] artists, string album = "", string[]? albumArtists = null, int? seconds = null, string isrc = "", bool? isExplicit = null) =>
        new(path, 1000, DateTime.UnixEpoch, new AudioTags(title, artists, album, albumArtists ?? [], null, null, null, seconds is { } s ? TimeSpan.FromSeconds(s) : null, isrc, isExplicit), HasTags: true);

    [Test]
    public void Explicit_and_clean_versions_are_different_tracks()
    {
        var explicitFile = File(@"E:\explicit.m4a", "Song", ["Artist"], isExplicit: true);
        var cleanFile = File(@"E:\clean.m4a", "Song", ["Artist"], isExplicit: false);
        var snapshot = new PlayerIndexSnapshot([explicitFile, cleanFile]);

        Assert.That(snapshot.Match(Track("Song", ["Artist"], isExplicit: true)).Files, Is.EqualTo(new[] { explicitFile }));
        Assert.That(snapshot.Match(Track("Song", ["Artist"], isExplicit: false)).Files, Is.EqualTo(new[] { cleanFile }));
    }

    [Test]
    public void Explicit_or_clean_markers_in_the_title_or_album_count_as_the_version()
    {
        var snapshot = new PlayerIndexSnapshot(
        [
            File(@"E:\a.mp3", "Song A (Clean)", ["Artist"]),
            File(@"E:\b.mp3", "Song B", ["Artist"], album: "Album [Explicit]"),
        ]);

        Assert.That(snapshot.Match(Track("Song A", ["Artist"], isExplicit: true)).IsOnPlayer, Is.False);
        Assert.That(snapshot.Match(Track("Song A", ["Artist"], isExplicit: false)).IsOnPlayer, Is.True);
        Assert.That(snapshot.Match(Track("Song B", ["Artist"], isExplicit: false)).IsOnPlayer, Is.False);
        Assert.That(snapshot.Match(Track("Song B", ["Artist"], isExplicit: true)).IsOnPlayer, Is.True);
    }

    [Test]
    public void Files_or_tracks_of_unknown_version_match_either_version()
    {
        var snapshot = new PlayerIndexSnapshot([File(@"E:\a.mp3", "Song", ["Artist"])]);

        Assert.That(snapshot.Match(Track("Song", ["Artist"], isExplicit: true)).IsOnPlayer);
        Assert.That(snapshot.Match(Track("Song", ["Artist"], isExplicit: false)).IsOnPlayer);
        Assert.That(new PlayerIndexSnapshot([File(@"E:\a.mp3", "Song", ["Artist"], isExplicit: true)]).Match(Track("Song", ["Artist"])).IsOnPlayer);
    }

    [Test]
    public void Isrc_match_is_kept_whatever_the_advisory_says()
    {
        var file = File(@"E:\a.mp3", "Song", ["Artist"], isrc: "USX", isExplicit: false);

        Assert.That(new PlayerIndexSnapshot([file]).Match(Track("Song", ["Artist"], isrc: "USX", isExplicit: true)).Kind, Is.EqualTo(TrackMatchKind.Isrc));
    }

    [Test]
    public void Matches_by_isrc_even_when_tags_differ()
    {
        var file = File(@"E:\x.mp3", "Something else", ["Someone"], isrc: "GBUM71029604");
        var match = new PlayerIndexSnapshot([file]).Match(Track("Bohemian Rhapsody", ["Queen"], isrc: "gbum71029604"));

        Assert.That(match.IsOnPlayer);
        Assert.That(match.Kind, Is.EqualTo(TrackMatchKind.Isrc));
        Assert.That(match.Files, Is.EqualTo(new[] { file }));
    }

    [Test]
    public void Matches_by_normalized_artist_and_title()
    {
        var file = File(@"E:\Queen\01 Bohemian Rhapsody.mp3", "Bohemian Rhapsody (2011 Remaster)", ["QUEEN"], seconds: 355);
        var match = new PlayerIndexSnapshot([file]).Match(Track("Bohemian Rhapsody - Remastered 2011", ["Queen"], seconds: 354));

        Assert.That(match.Kind, Is.EqualTo(TrackMatchKind.ArtistAndTitle));
        Assert.That(match.Files, Is.EqualTo(new[] { file }));
    }

    [Test]
    public void Matches_featured_artist_credits_in_either_field()
    {
        var file = File(@"E:\a.mp3", "Señorita", ["Shawn Mendes feat. Camila Cabello"]);
        var snapshot = new PlayerIndexSnapshot([file]);

        Assert.That(snapshot.Match(Track("Senorita", ["Camila Cabello"])).IsOnPlayer);
        Assert.That(snapshot.Match(Track("Señorita (with Shawn Mendes)", ["Camila Cabello"])).IsOnPlayer);
    }

    [Test]
    public void Durations_must_agree_when_both_are_known()
    {
        var snapshot = new PlayerIndexSnapshot([File(@"E:\a.mp3", "Song", ["Artist"], seconds: 200)]);

        Assert.That(snapshot.Match(Track("Song", ["Artist"], seconds: 203)).IsOnPlayer, Is.True);
        Assert.That(snapshot.Match(Track("Song", ["Artist"], seconds: 260)).IsOnPlayer, Is.False);
        Assert.That(snapshot.Match(Track("Song", ["Artist"])).IsOnPlayer, Is.True);
    }

    [Test]
    public void Different_artist_is_not_a_match()
    {
        var snapshot = new PlayerIndexSnapshot([File(@"E:\a.mp3", "Hurt", ["Nine Inch Nails"], seconds: 300)]);

        Assert.That(snapshot.Match(Track("Hurt", ["Johnny Cash"], seconds: 218)).IsOnPlayer, Is.False);
    }

    [Test]
    public void Similar_title_matches_only_with_agreeing_duration()
    {
        var file = File(@"E:\a.mp3", "Song", ["Artist"], seconds: 200);
        var snapshot = new PlayerIndexSnapshot([file]);

        var similar = snapshot.Match(Track("Song - From \"The Movie\"", ["Artist"], seconds: 201));
        Assert.That(similar.Kind, Is.EqualTo(TrackMatchKind.SimilarTitle));

        Assert.That(snapshot.Match(Track("Song - Radio Edit", ["Artist"], seconds: 170)).IsOnPlayer, Is.False);
        Assert.That(snapshot.Match(Track("Song - From \"The Movie\"", ["Artist"])).IsOnPlayer, Is.False);
    }

    [Test]
    public void Untagged_artist_matches_on_title_and_duration()
    {
        var snapshot = new PlayerIndexSnapshot([File(@"E:\Song.mp3", "Song", [], seconds: 200)]);

        Assert.That(snapshot.Match(Track("Song", ["Artist"], seconds: 199)).Kind, Is.EqualTo(TrackMatchKind.TitleAndDuration));
        Assert.That(snapshot.Match(Track("Song", ["Artist"])).IsOnPlayer, Is.False);
    }

    [Test]
    public void Returns_every_matching_file()
    {
        var a = File(@"E:\A\Song.mp3", "Song", ["Artist"]);
        var b = File(@"E:\B\Song.mp3", "Song", ["Artist"], isrc: "USX");
        var match = new PlayerIndexSnapshot([a, b]).Match(Track("Song", ["Artist"], isrc: "USX"));

        Assert.That(match.Kind, Is.EqualTo(TrackMatchKind.Isrc));
        Assert.That(match.Files, Is.EqualTo(new[] { b, a }));
    }

    [Test]
    public void Removed_track_with_no_title_is_not_on_the_player()
    {
        var snapshot = new PlayerIndexSnapshot([File(@"E:\a.mp3", "", [])]);

        Assert.That(snapshot.Match(Track("", [])).IsOnPlayer, Is.False);
    }

    [Test]
    public void FindAlbum_matches_album_artist_or_track_artist_ignoring_edition_suffixes()
    {
        var one = File(@"E:\Queen\Hot Space\01.mp3", "Staying Power", ["Queen"], "Hot Space (Deluxe Remastered Version)", ["Queen"]);
        var two = File(@"E:\Queen\Hot Space\11.mp3", "Under Pressure", ["Queen", "David Bowie"], "Hot Space");
        var other = File(@"E:\Other\Hot Space.mp3", "Hot Space", ["Someone"], "Hot Space");
        var snapshot = new PlayerIndexSnapshot([one, two, other]);

        Assert.That(snapshot.FindAlbum("Queen", "Hot Space"), Is.EquivalentTo(new[] { one, two }));
        Assert.That(snapshot.FindAlbum(Track("Under Pressure", ["Queen", "David Bowie"], "Hot Space")), Is.EquivalentTo(new[] { one, two }));
        Assert.That(snapshot.FindAlbum([], "Hot Space"), Has.Count.EqualTo(3));
        Assert.That(snapshot.FindAlbum("Queen", "Greatest Hits"), Is.Empty);
    }

    [Test]
    public void Albums_groups_by_album_artist_falling_back_to_first_artist()
    {
        var one = File(@"E:\1.mp3", "A", ["Queen feat. Someone"], "Hot Space");
        var two = File(@"E:\2.mp3", "B", ["Someone Else"], "Hot Space (Remastered)", ["The Queen"]);
        var snapshot = new PlayerIndexSnapshot([one, two, File(@"E:\3.mp3", "C", ["X"])]);

        Assert.That(snapshot.Albums.Keys, Is.EqualTo(new[] { AlbumKey.Create("Queen", "Hot Space") }));
        Assert.That(snapshot.Albums[AlbumKey.Create("Queen", "Hot Space")], Is.EquivalentTo(new[] { one, two }));
    }
}
