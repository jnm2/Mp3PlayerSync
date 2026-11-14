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
    public async Task NormalizeText_ignores_case_punctuation_and_diacritics(string text, string expected)
    {
        await Assert.That(TrackNormalizer.NormalizeText(text)).IsEqualTo(expected);
    }

    [Test]
    public async Task Keys_ignore_spacing_so_initialisms_match()
    {
        await Assert.That(TrackNormalizer.ArtistKey("R.E.M.")).IsEqualTo(TrackNormalizer.ArtistKey("REM"));
    }

    [Test]
    public async Task Artist_keys_ignore_a_leading_The()
    {
        await Assert.That(TrackNormalizer.ArtistKey("The Beatles")).IsEqualTo(TrackNormalizer.ArtistKey("Beatles"));
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
    public async Task StripTitleDecorations_removes_version_suffixes_but_keeps_different_recordings(string title, string expected)
    {
        await Assert.That(TrackNormalizer.StripTitleDecorations(title, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Song (feat. Artist B)")]
    [Arguments("Song [ft. Artist B]")]
    [Arguments("Song (with Artist B)")]
    [Arguments("Song feat. Artist B")]
    [Arguments("Song - featuring Artist B")]
    [Arguments("Song (feat. Artist B) - Remastered 2011")]
    public async Task StripTitleDecorations_moves_featured_artists_out_of_the_title(string title)
    {
        await Assert.That(TrackNormalizer.StripTitleDecorations(title, out var featured)).IsEqualTo("Song");
        await Assert.That(featured).IsEquivalentTo(new[] { "Artist B" }, CollectionOrdering.Matching);
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
    public async Task ExplicitMarker_reads_explicit_and_clean_suffixes(string text, bool? expected)
    {
        await Assert.That(TrackNormalizer.ExplicitMarker(text)).IsEqualTo(expected);
        if (expected is not null) await Assert.That(TrackNormalizer.TitleKey(text)).DoesNotContain("explicit").And.DoesNotContain("clean");
    }

    [Test]
    public async Task BaseTitleKey_drops_any_trailing_part()
    {
        await Assert.That(TrackNormalizer.BaseTitleKey("Song - From \"The Movie\" Soundtrack")).IsEqualTo(TrackNormalizer.TitleKey("Song"));
        await Assert.That(TrackNormalizer.BaseTitleKey("Song (Radio Edit)")).IsEqualTo(TrackNormalizer.TitleKey("Song"));
    }

    [Test]
    [Arguments("A feat. B", new[] { "A", "B" })]
    [Arguments("A ft. B & C", new[] { "A", "B", "C" })]
    [Arguments("A; B", new[] { "A", "B" })]
    [Arguments("A with B", new[] { "A", "B" })]
    [Arguments("A vs. B", new[] { "A", "B" })]
    public async Task SplitArtists_splits_on_credit_separators(string credit, string[] expected)
    {
        await Assert.That(TrackNormalizer.SplitArtists(credit)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Artist_keys_overlap_across_credit_styles()
    {
        var spotify = TrackNormalizer.ArtistKeys(["Queen", "David Bowie"]);

        await Assert.That(TrackNormalizer.ArtistKeys(["Queen & David Bowie"]).Overlaps(spotify)).IsTrue();
        await Assert.That(TrackNormalizer.ArtistKeys(["David Bowie feat. Queen"]).Overlaps(spotify)).IsTrue();
        await Assert.That(TrackNormalizer.ArtistKeys(["Earth Wind and Fire"]).Overlaps(TrackNormalizer.ArtistKeys(["Earth, Wind & Fire"]))).IsTrue();
        await Assert.That(TrackNormalizer.ArtistKeys(["AC", "DC"]).Overlaps(TrackNormalizer.ArtistKeys(["AC/DC"]))).IsTrue();
        await Assert.That(TrackNormalizer.ArtistKeys(["Metallica"]).Overlaps(spotify)).IsFalse();
    }

    [Test]
    public async Task IsrcKey_ignores_case_and_separators()
    {
        await Assert.That(TrackNormalizer.IsrcKey("gb-umw-07-12345")).IsEqualTo("GBUMW0712345");
        await Assert.That(TrackNormalizer.IsrcKey(null)).IsEmpty();
    }

    [Test]
    [Arguments("01 - Song.mp3", "", "Song")]
    [Arguments("01. Artist - Song.mp3", "Artist", "Song")]
    [Arguments("1-02 - Artist - Song.flac", "Artist", "Song")]
    [Arguments("05 Song.mp3", "", "Song")]
    [Arguments("99 Luftballons.mp3", "", "99 Luftballons")]
    [Arguments("Artist_-_Song.mp3", "Artist", "Song")]
    public async Task ParseFileName_reads_artist_and_title(string fileName, string artist, string title)
    {
        await Assert.That(AudioTagReader.ParseFileName(fileName)).IsEqualTo((artist, title));
    }
}
