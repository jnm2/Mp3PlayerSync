using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Tests.Scanning;

public class TrackNormalizerTests
{
    [TestCase("Beyoncé", "beyonce")]
    [TestCase("Sigur Rós", "sigur ros")]
    [TestCase("Mötley Crüe", "motley crue")]
    [TestCase("Røyksopp", "royksopp")]
    [TestCase("Don't Stop Me Now!", "dont stop me now")]
    [TestCase("Don’t Stop Me Now", "dont stop me now")]
    [TestCase("Simon & Garfunkel", "simon and garfunkel")]
    [TestCase("  Hello,   World...  ", "hello world")]
    [TestCase("ＦＵＬＬ ｗｉｄｔｈ", "full width")]
    public void NormalizeText_ignores_case_punctuation_and_diacritics(string text, string expected)
    {
        Assert.That(TrackNormalizer.NormalizeText(text), Is.EqualTo(expected));
    }

    [Test]
    public void Keys_ignore_spacing_so_initialisms_match()
    {
        Assert.That(TrackNormalizer.ArtistKey("R.E.M."), Is.EqualTo(TrackNormalizer.ArtistKey("REM")));
    }

    [Test]
    public void Artist_keys_ignore_a_leading_The()
    {
        Assert.That(TrackNormalizer.ArtistKey("The Beatles"), Is.EqualTo(TrackNormalizer.ArtistKey("Beatles")));
    }

    [TestCase("Bohemian Rhapsody - Remastered 2011", "Bohemian Rhapsody")]
    [TestCase("Bohemian Rhapsody (2011 Remaster)", "Bohemian Rhapsody")]
    [TestCase("Here Comes the Sun - 2019 Mix", "Here Comes the Sun - 2019 Mix")]
    [TestCase("Song [Remastered] - Single Version", "Song")]
    [TestCase("Song - Mono Version", "Song")]
    [TestCase("Song (Deluxe Edition)", "Song")]
    [TestCase("Song - Live at Wembley - Remastered", "Song - Live at Wembley")]
    [TestCase("Song (Live)", "Song (Live)")]
    [TestCase("Song - Radio Edit", "Song - Radio Edit")]
    [TestCase("(Remastered)", "(Remastered)")]
    [TestCase("Dancing with Myself", "Dancing with Myself")]
    [TestCase("Rather Be (Clean Bandit Remix)", "Rather Be (Clean Bandit Remix)")]
    [TestCase("Clean", "Clean")]
    public void StripTitleDecorations_removes_version_suffixes_but_keeps_different_recordings(string title, string expected)
    {
        Assert.That(TrackNormalizer.StripTitleDecorations(title, out _), Is.EqualTo(expected));
    }

    [TestCase("Song (feat. Artist B)")]
    [TestCase("Song [ft. Artist B]")]
    [TestCase("Song (with Artist B)")]
    [TestCase("Song feat. Artist B")]
    [TestCase("Song - featuring Artist B")]
    [TestCase("Song (feat. Artist B) - Remastered 2011")]
    public void StripTitleDecorations_moves_featured_artists_out_of_the_title(string title)
    {
        Assert.That(TrackNormalizer.StripTitleDecorations(title, out var featured), Is.EqualTo("Song"));
        Assert.That(featured, Is.EqualTo(new[] { "Artist B" }));
    }

    [TestCase("Song (Explicit)", true)]
    [TestCase("Song [Explicit Version]", true)]
    [TestCase("Song - Clean", false)]
    [TestCase("Song (Clean Edit)", false)]
    [TestCase("Album (Deluxe Clean Version)", false)]
    [TestCase("Song (Explicit) - Remastered 2011", true)]
    [TestCase("Song", null)]
    [TestCase("Clean", null)]
    [TestCase("Rather Be (Clean Bandit Remix)", null)]
    public void ExplicitMarker_reads_explicit_and_clean_suffixes(string text, bool? expected)
    {
        Assert.That(TrackNormalizer.ExplicitMarker(text), Is.EqualTo(expected));
        if (expected is not null) Assert.That(TrackNormalizer.TitleKey(text), Does.Not.Contain("explicit").And.Not.Contain("clean"));
    }

    [Test]
    public void BaseTitleKey_drops_any_trailing_part()
    {
        Assert.That(TrackNormalizer.BaseTitleKey("Song - From \"The Movie\" Soundtrack"), Is.EqualTo(TrackNormalizer.TitleKey("Song")));
        Assert.That(TrackNormalizer.BaseTitleKey("Song (Radio Edit)"), Is.EqualTo(TrackNormalizer.TitleKey("Song")));
    }

    [TestCase("A feat. B", new[] { "A", "B" })]
    [TestCase("A ft. B & C", new[] { "A", "B", "C" })]
    [TestCase("A; B", new[] { "A", "B" })]
    [TestCase("A with B", new[] { "A", "B" })]
    [TestCase("A vs. B", new[] { "A", "B" })]
    public void SplitArtists_splits_on_credit_separators(string credit, string[] expected)
    {
        Assert.That(TrackNormalizer.SplitArtists(credit), Is.EqualTo(expected));
    }

    [Test]
    public void Artist_keys_overlap_across_credit_styles()
    {
        var spotify = TrackNormalizer.ArtistKeys(["Queen", "David Bowie"]);

        Assert.That(TrackNormalizer.ArtistKeys(["Queen & David Bowie"]).Overlaps(spotify));
        Assert.That(TrackNormalizer.ArtistKeys(["David Bowie feat. Queen"]).Overlaps(spotify));
        Assert.That(TrackNormalizer.ArtistKeys(["Earth Wind and Fire"]).Overlaps(TrackNormalizer.ArtistKeys(["Earth, Wind & Fire"])));
        Assert.That(TrackNormalizer.ArtistKeys(["AC", "DC"]).Overlaps(TrackNormalizer.ArtistKeys(["AC/DC"])));
        Assert.That(TrackNormalizer.ArtistKeys(["Metallica"]).Overlaps(spotify), Is.False);
    }

    [Test]
    public void IsrcKey_ignores_case_and_separators()
    {
        Assert.That(TrackNormalizer.IsrcKey("gb-umw-07-12345"), Is.EqualTo("GBUMW0712345"));
        Assert.That(TrackNormalizer.IsrcKey(null), Is.Empty);
    }

    [TestCase("01 - Song.mp3", "", "Song")]
    [TestCase("01. Artist - Song.mp3", "Artist", "Song")]
    [TestCase("1-02 - Artist - Song.flac", "Artist", "Song")]
    [TestCase("05 Song.mp3", "", "Song")]
    [TestCase("99 Luftballons.mp3", "", "99 Luftballons")]
    [TestCase("Artist_-_Song.mp3", "Artist", "Song")]
    public void ParseFileName_reads_artist_and_title(string fileName, string artist, string title)
    {
        Assert.That(AudioTagReader.ParseFileName(fileName), Is.EqualTo((artist, title)));
    }
}
