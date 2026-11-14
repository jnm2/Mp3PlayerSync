using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Tests.Scanning;

public class TrackNormalizerTests
{
    [Test]
    [Arguments("Beyoncé", "beyonce")]
    [Arguments("Sigur Rós", "sigur ros")]
    [Arguments("Mötley Crüe", "motley crue")]
    [Arguments("Røyksopp", "royksopp")]
    [Arguments("Don't Stop Me Now!", "dont stop me now")]
    [Arguments("Don’t Stop Me Now", "dont stop me now")]
    [Arguments("Simon & Garfunkel", "simon and garfunkel")]
    [Arguments("  Hello,   World...  ", "hello world")]
    [Arguments("ＦＵＬＬ ｗｉｄｔｈ", "full width")]
    public void NormalizeText_ignores_case_punctuation_and_diacritics(string text, string expected)
    {
        TrackNormalizer.NormalizeText(text).ShouldBe(expected);
    }

    [Test]
    public void Keys_ignore_spacing_so_initialisms_match()
    {
        TrackNormalizer.ArtistKey("R.E.M.").ShouldBe(TrackNormalizer.ArtistKey("REM"));
    }

    [Test]
    public void Artist_keys_ignore_a_leading_The()
    {
        TrackNormalizer.ArtistKey("The Beatles").ShouldBe(TrackNormalizer.ArtistKey("Beatles"));
    }

    [Test]
    [Arguments("Bohemian Rhapsody - Remastered 2011", "Bohemian Rhapsody")]
    [Arguments("Bohemian Rhapsody (2011 Remaster)", "Bohemian Rhapsody")]
    [Arguments("Here Comes the Sun - 2019 Mix", "Here Comes the Sun - 2019 Mix")]
    [Arguments("Song [Remastered] - Single Version", "Song")]
    [Arguments("Song - Mono Version", "Song")]
    [Arguments("Song (Deluxe Edition)", "Song")]
    [Arguments("Song - Live at Wembley - Remastered", "Song - Live at Wembley")]
    [Arguments("Song (Live)", "Song (Live)")]
    [Arguments("Song - Radio Edit", "Song - Radio Edit")]
    [Arguments("(Remastered)", "(Remastered)")]
    [Arguments("Dancing with Myself", "Dancing with Myself")]
    [Arguments("Rather Be (Clean Bandit Remix)", "Rather Be (Clean Bandit Remix)")]
    [Arguments("Clean", "Clean")]
    public void StripTitleDecorations_removes_version_suffixes_but_keeps_different_recordings(string title, string expected)
    {
        TrackNormalizer.StripTitleDecorations(title, out _).ShouldBe(expected);
    }

    [Test]
    [Arguments("Song (feat. Artist B)")]
    [Arguments("Song [ft. Artist B]")]
    [Arguments("Song (with Artist B)")]
    [Arguments("Song feat. Artist B")]
    [Arguments("Song - featuring Artist B")]
    [Arguments("Song (feat. Artist B) - Remastered 2011")]
    public void StripTitleDecorations_moves_featured_artists_out_of_the_title(string title)
    {
        TrackNormalizer.StripTitleDecorations(title, out var featured).ShouldBe("Song");
        featured.ShouldBe(new[] { "Artist B" });
    }

    [Test]
    [Arguments("Song (Explicit)", true)]
    [Arguments("Song [Explicit Version]", true)]
    [Arguments("Song - Clean", false)]
    [Arguments("Song (Clean Edit)", false)]
    [Arguments("Album (Deluxe Clean Version)", false)]
    [Arguments("Song (Explicit) - Remastered 2011", true)]
    [Arguments("Song", null)]
    [Arguments("Clean", null)]
    [Arguments("Rather Be (Clean Bandit Remix)", null)]
    public void ExplicitMarker_reads_explicit_and_clean_suffixes(string text, bool? expected)
    {
        TrackNormalizer.ExplicitMarker(text).ShouldBe(expected);
        if (expected is not null)
        {
            var key = TrackNormalizer.TitleKey(text);
            key.ShouldNotContain("explicit", Case.Sensitive);
            key.ShouldNotContain("clean", Case.Sensitive);
        }
    }

    [Test]
    public void BaseTitleKey_drops_any_trailing_part()
    {
        TrackNormalizer.BaseTitleKey("Song - From \"The Movie\" Soundtrack").ShouldBe(TrackNormalizer.TitleKey("Song"));
        TrackNormalizer.BaseTitleKey("Song (Radio Edit)").ShouldBe(TrackNormalizer.TitleKey("Song"));
    }

    [Test]
    [Arguments("A feat. B", new[] { "A", "B" })]
    [Arguments("A ft. B & C", new[] { "A", "B", "C" })]
    [Arguments("A; B", new[] { "A", "B" })]
    [Arguments("A with B", new[] { "A", "B" })]
    [Arguments("A vs. B", new[] { "A", "B" })]
    public void SplitArtists_splits_on_credit_separators(string credit, string[] expected)
    {
        TrackNormalizer.SplitArtists(credit).ShouldBe(expected);
    }

    [Test]
    public void Artist_keys_overlap_across_credit_styles()
    {
        var spotify = TrackNormalizer.ArtistKeys(["Queen", "David Bowie"]);

        TrackNormalizer.ArtistKeys(["Queen & David Bowie"]).Overlaps(spotify).ShouldBeTrue();
        TrackNormalizer.ArtistKeys(["David Bowie feat. Queen"]).Overlaps(spotify).ShouldBeTrue();
        TrackNormalizer.ArtistKeys(["Earth Wind and Fire"]).Overlaps(TrackNormalizer.ArtistKeys(["Earth, Wind & Fire"])).ShouldBeTrue();
        TrackNormalizer.ArtistKeys(["AC", "DC"]).Overlaps(TrackNormalizer.ArtistKeys(["AC/DC"])).ShouldBeTrue();
        TrackNormalizer.ArtistKeys(["Metallica"]).Overlaps(spotify).ShouldBeFalse();
    }

    [Test]
    public void IsrcKey_ignores_case_and_separators()
    {
        TrackNormalizer.IsrcKey("gb-umw-07-12345").ShouldBe("GBUMW0712345");
        TrackNormalizer.IsrcKey(null).ShouldBeEmpty();
    }

    [Test]
    [Arguments("01 - Song.mp3", "", "Song")]
    [Arguments("01. Artist - Song.mp3", "Artist", "Song")]
    [Arguments("1-02 - Artist - Song.flac", "Artist", "Song")]
    [Arguments("05 Song.mp3", "", "Song")]
    [Arguments("99 Luftballons.mp3", "", "99 Luftballons")]
    [Arguments("Artist_-_Song.mp3", "Artist", "Song")]
    public void ParseFileName_reads_artist_and_title(string fileName, string artist, string title)
    {
        AudioTagReader.ParseFileName(fileName).ShouldBe((artist, title));
    }
}
