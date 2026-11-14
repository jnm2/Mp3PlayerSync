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

        ranked[0].Video.Id.ShouldBe("BaxnrepwXck");
        ranked[0].Score.IsAcceptable.ShouldBeTrue();

        var byId = ranked.ToDictionary(c => c.Video.Id);
        byId["C4P4u0p88i0"].Score.IsAcceptable.ShouldBeFalse(customMessage: "Live, by a tribute band");
        byId["rt7r-Zo7iJo"].Score.IsAcceptable.ShouldBeFalse(customMessage: "Live, with a different singer");
        byId["DJxCsVcZL2I"].Score.IsAcceptable.ShouldBeFalse(customMessage: "Different song");
    }

    [Test]
    public void Accepts_live_recording_when_the_track_is_live()
    {
        var query = Query("Under Pressure - Live at Wembley Stadium", "Queen", null, 236);

        var score = TrackMatcher.Score(query, Candidate("Under Pressure (Live at Wembley Stadium / July 1986)", "Queen", 237));

        score.VersionMismatches.ShouldBe(0);
        score.IsAcceptable.ShouldBeTrue();
    }

    [Test]
    public void Matches_despite_diacritics_featured_artists_and_spelling_variants()
    {
        var query = Query("Déjà Vu (feat. Jay-Z)", "Beyoncé;JAY-Z", "B'Day", 240);

        var score = TrackMatcher.Score(query, Candidate("Deja Vu (Album Version)", "Beyoncé;JAŸ-Z", 240, album: "B'Day"));

        score.IsAcceptable.ShouldBeTrue(customMessage: score.ToString());
        score.Title.ShouldBe(1);
    }

    [Test]
    public void Uses_topic_channel_when_artists_are_missing()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "September", "duration": 215, "channel": "Earth, Wind & Fire - Topic"}""");

        var score = TrackMatcher.Score(Query("September", "Earth, Wind & Fire", null, 215.093), video);

        score.Artist.ShouldBe(1);
        score.IsAcceptable.ShouldBeTrue();
    }

    [Test]
    public void Rejects_a_different_edit_by_duration()
    {
        var score = TrackMatcher.Score(Query("Song", "Artist", null, 200), Candidate("Song", "Artist", 260));

        score.IsAcceptable.ShouldBeFalse();
    }

    [Test]
    public void Rejects_a_different_performance_more_than_a_few_seconds_off()
    {
        var query = Query("Hotel California - Live On MTV, 1994", "Eagles", "Hell Freezes Over", 432);

        var score = TrackMatcher.Score(query, Candidate("Hotel California (Live at the Millennium Concert, 12/31/1999)", "Eagles", 417));

        score.IsAcceptable.ShouldBeFalse();
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

        ranked.Select(c => c.Video.Title).ShouldBe(new[]
        {
            "Under Pressure (feat. David Bowie)",
            "Under Pressure (Single Version; 2017 Remaster)",
            "Queen & David Bowie - Under Pressure (Official Video)",
        });
    }

    [Test]
    [Arguments("Song (Karaoke Version)")]
    [Arguments("Song (Instrumental)")]
    [Arguments("Song - Sped Up")]
    [Arguments("Song (Acoustic)")]
    [Arguments("Song (Remix)")]
    public void Rejects_other_versions(string title)
    {
        var score = TrackMatcher.Score(Query("Song", "Artist", null, 200), Candidate(title, "Artist", 200));

        score.VersionMismatches.ShouldBe(1);
        score.IsAcceptable.ShouldBeFalse();
    }

    [Test]
    public void Titles_containing_with_are_not_truncated()
    {
        var score = TrackMatcher.Score(Query("Stay With Me", "Sam Smith", null, 172), Candidate("Stay", "Sam Smith", 172));

        score.IsAcceptable.ShouldBeFalse();
    }

    [Test]
    [Arguments("Song (Explicit)", true)]
    [Arguments("Song [Explicit Version]", true)]
    [Arguments("Song - Explicit", true)]
    [Arguments("Album (Clean)", false)]
    [Arguments("Song (Edited)", false)]
    [Arguments("Song - Clean Version", false)]
    [Arguments("Clean Bandit", null)]
    [Arguments("Explicit Content Warning", null)]
    [Arguments("Song", null)]
    public void Reads_explicit_and_clean_markers(string text, bool? expected)
    {
        TrackMatcher.GetExplicitMarker(text).ShouldBe(expected);
    }

    [Test]
    public void Query_takes_explicit_flag_from_the_playlist()
    {
        var track = new ExportedTrack(1, "", "Song (Clean)", ["Artist"], "", null, "", null, "", Explicit: true, "", "");

        TrackQuery.FromExportedTrack(track).Explicit.ShouldBe(true, customMessage: "Spotify's flag wins over the title marker");
        TrackQuery.FromExportedTrack(track with { Explicit = null }).Explicit.ShouldBe(false);
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

        YouTubeMusicSearchPage.ReadExplicitBadges(page).ShouldBe(new[] { ("explicit001", true), ("clean000001", false) });
    }

    [Test]
    public void Search_text_drops_remaster_suffix_and_adds_artists()
    {
        Query("Bohemian Rhapsody - Remastered 2011", "Queen", null, 354).SearchText.ShouldBe("Bohemian Rhapsody Queen");
        Query("Under Pressure", "Queen;David Bowie", null, 248).SearchText.ShouldBe("Under Pressure Queen David Bowie");
        Query("Song - Live", "Artist", null, 1).SearchText.ShouldBe("Song - Live Artist");
    }

    [Test]
    public void Query_from_exported_track()
    {
        var track = new ExportedTrack(1, "spotify:track:1", "September", ["Earth, Wind & Fire"], "", TimeSpan.FromMilliseconds(215093), "", null, "", null, "", "");

        TrackQuery.FromExportedTrack(track).ShouldBe(new TrackQuery("September", track.Artists, null, TimeSpan.FromMilliseconds(215093)));
    }
}
