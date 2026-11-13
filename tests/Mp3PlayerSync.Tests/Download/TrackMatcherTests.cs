using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class TrackMatcherTests
{
    private static TrackQuery Query(string title, string artists, string? album, double seconds) =>
        new(title, artists.Split(';'), album, TimeSpan.FromSeconds(seconds));

    private static YouTubeVideoInfo Candidate(string title, string artists, double seconds, string? album = null, string? channel = null, int rank = 1) =>
        YouTubeVideoInfo.Parse(YtDlpSamples.Video("id" + rank, title, title, artists.Split(';'), album ?? "", seconds, rank, channel ?? artists.Split(';')[0]));

    [Test]
    public void Picks_the_studio_recording_over_live_versions_and_tribute_bands()
    {
        var query = Query("Under Pressure - Remastered 2011", "Queen;David Bowie", "Hot Space (2011 Remaster)", 248.44);
        var candidates = YouTubeVideoInfo.ParseLines(YtDlpSamples.UnderPressureSearch);

        var ranked = TrackMatcher.Rank(query, candidates);

        Assert.That(ranked[0].Video.Id, Is.EqualTo("BaxnrepwXck"));
        Assert.That(ranked[0].Score.IsAcceptable);

        var byId = ranked.ToDictionary(c => c.Video.Id);
        Assert.That(byId["C4P4u0p88i0"].Score.IsAcceptable, Is.False, "Live, by a tribute band");
        Assert.That(byId["rt7r-Zo7iJo"].Score.IsAcceptable, Is.False, "Live, with a different singer");
        Assert.That(byId["DJxCsVcZL2I"].Score.IsAcceptable, Is.False, "Different song");
    }

    [Test]
    public void Accepts_live_recording_when_the_track_is_live()
    {
        var query = Query("Under Pressure - Live at Wembley Stadium", "Queen", null, 236);

        var score = TrackMatcher.Score(query, Candidate("Under Pressure (Live at Wembley Stadium / July 1986)", "Queen", 237));

        Assert.That(score.VersionMismatches, Is.Zero);
        Assert.That(score.IsAcceptable);
    }

    [Test]
    public void Matches_despite_diacritics_featured_artists_and_spelling_variants()
    {
        var query = Query("Déjà Vu (feat. Jay-Z)", "Beyoncé;JAY-Z", "B'Day", 240);

        var score = TrackMatcher.Score(query, Candidate("Deja Vu (Album Version)", "Beyoncé;JAŸ-Z", 240, album: "B'Day"));

        Assert.That(score.IsAcceptable, score.ToString());
        Assert.That(score.Title, Is.EqualTo(1));
    }

    [Test]
    public void Uses_topic_channel_when_artists_are_missing()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "September", "duration": 215, "channel": "Earth, Wind & Fire - Topic"}""");

        var score = TrackMatcher.Score(Query("September", "Earth, Wind & Fire", null, 215.093), video);

        Assert.That(score.Artist, Is.EqualTo(1));
        Assert.That(score.IsAcceptable);
    }

    [Test]
    public void Rejects_a_different_edit_by_duration()
    {
        var score = TrackMatcher.Score(Query("Song", "Artist", null, 200), Candidate("Song", "Artist", 260));

        Assert.That(score.IsAcceptable, Is.False);
    }

    [Test]
    public void Rejects_a_different_performance_more_than_a_few_seconds_off()
    {
        var query = Query("Hotel California - Live On MTV, 1994", "Eagles", "Hell Freezes Over", 432);

        var score = TrackMatcher.Score(query, Candidate("Hotel California (Live at the Millennium Concert, 12/31/1999)", "Eagles", 417));

        Assert.That(score.IsAcceptable, Is.False);
    }

    [Test]
    public void Flat_results_are_ranked_by_title_then_search_position()
    {
        var query = Query("Under Pressure - Remastered 2011", "Queen;David Bowie", null, 248);
        YouTubeVideoInfo Flat(string title, int rank) => new("id" + rank, "", title, null, [], null, null, null, rank, []);

        var ranked = TrackMatcher.RankFlat(query,
        [
            Flat("Rebel Rebel", 1),
            Flat("Under Pressure (Live)", 2),
            Flat("Queen & David Bowie - Under Pressure (Official Video)", 3),
            Flat("Under Pressure (feat. David Bowie)", 4),
            Flat("Under Pressure (Single Version; 2017 Remaster)", 5),
        ]);

        Assert.That(ranked.Select(c => c.Video.Title), Is.EqualTo(new[]
        {
            "Under Pressure (feat. David Bowie)",
            "Under Pressure (Single Version; 2017 Remaster)",
            "Queen & David Bowie - Under Pressure (Official Video)",
        }));
    }

    [TestCase("Song (Karaoke Version)")]
    [TestCase("Song (Instrumental)")]
    [TestCase("Song - Sped Up")]
    [TestCase("Song (Acoustic)")]
    [TestCase("Song (Remix)")]
    public void Rejects_other_versions(string title)
    {
        var score = TrackMatcher.Score(Query("Song", "Artist", null, 200), Candidate(title, "Artist", 200));

        Assert.That(score.VersionMismatches, Is.EqualTo(1));
        Assert.That(score.IsAcceptable, Is.False);
    }

    [Test]
    public void Titles_containing_with_are_not_truncated()
    {
        var score = TrackMatcher.Score(Query("Stay With Me", "Sam Smith", null, 172), Candidate("Stay", "Sam Smith", 172));

        Assert.That(score.IsAcceptable, Is.False);
    }

    [TestCase("Song (Explicit)", true)]
    [TestCase("Song [Explicit Version]", true)]
    [TestCase("Song - Explicit", true)]
    [TestCase("Album (Clean)", false)]
    [TestCase("Song (Edited)", false)]
    [TestCase("Song - Clean Version", false)]
    [TestCase("Clean Bandit", null)]
    [TestCase("Explicit Content Warning", null)]
    [TestCase("Song", null)]
    public void Reads_explicit_and_clean_markers(string text, bool? expected)
    {
        Assert.That(TrackMatcher.GetExplicitMarker(text), Is.EqualTo(expected));
    }

    [Test]
    public void Query_takes_explicit_flag_from_the_playlist()
    {
        var track = new ExportedTrack(1, "", "Song (Clean)", ["Artist"], "", null, "", null, "", Explicit: true, "", "");

        Assert.That(TrackQuery.FromExportedTrack(track).Explicit, Is.True, "Spotify's flag wins over the title marker");
        Assert.That(TrackQuery.FromExportedTrack(track with { Explicit = null }).Explicit, Is.False);
    }

    [Test]
    public void Reads_explicit_badges_from_a_search_page()
    {
        const string page = """
            {"contents":{"musicShelfRenderer":{"contents":[
            {"musicResponsiveListItemRenderer":{"overlay":{"watchEndpoint":{"videoId":"explicit001"}},"badges":[{"musicInlineBadgeRenderer":{"icon":{"iconType":"MUSIC_EXPLICIT_BADGE"}}}]}},
            {"musicResponsiveListItemRenderer":{"overlay":{"watchEndpoint":{"videoId":"clean000001"}}}},
            {"musicResponsiveListItemRenderer":{"navigationEndpoint":{"browseId":"UCartist"}}}
            ]},"musicCardShelfRenderer":{"badges":[{"icon":{"iconType":"MUSIC_EXPLICIT_BADGE"}}]}}}
            """;

        Assert.That(YouTubeMusicSearchPage.ReadExplicitBadges(page), Is.EqualTo(new[] { ("explicit001", true), ("clean000001", false) }));
    }

    [Test]
    public void Search_text_drops_remaster_suffix_and_adds_artists()
    {
        Assert.That(Query("Bohemian Rhapsody - Remastered 2011", "Queen", null, 354).SearchText, Is.EqualTo("Bohemian Rhapsody Queen"));
        Assert.That(Query("Under Pressure", "Queen;David Bowie", null, 248).SearchText, Is.EqualTo("Under Pressure Queen David Bowie"));
        Assert.That(Query("Song - Live", "Artist", null, 1).SearchText, Is.EqualTo("Song - Live Artist"));
    }

    [Test]
    public void Query_from_exported_track()
    {
        var track = new ExportedTrack(1, "spotify:track:1", "September", ["Earth, Wind & Fire"], "", TimeSpan.FromMilliseconds(215093), "", null, "", null, "", "");

        Assert.That(TrackQuery.FromExportedTrack(track), Is.EqualTo(new TrackQuery("September", track.Artists, null, TimeSpan.FromMilliseconds(215093))));
    }
}
