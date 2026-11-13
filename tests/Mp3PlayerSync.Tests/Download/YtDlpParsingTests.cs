using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeVideoInfoTests
{
    [Test]
    public void Parses_search_result_line()
    {
        var video = YouTubeVideoInfo.Parse(YtDlpSamples.UnderPressureSearch[0]);

        Assert.Multiple(() =>
        {
            Assert.That(video.Id, Is.EqualTo("BaxnrepwXck"));
            Assert.That(video.Url, Is.EqualTo("https://music.youtube.com/watch?v=BaxnrepwXck"));
            Assert.That(video.Track, Is.EqualTo("Under Pressure"));
            Assert.That(video.Artists, Is.EqualTo(new[] { "Queen", "David Bowie" }));
            Assert.That(video.Album, Is.EqualTo("Hot Space"));
            Assert.That(video.Duration, Is.EqualTo(TimeSpan.FromSeconds(248)));
            Assert.That(video.SearchRank, Is.EqualTo(1));
            Assert.That(video.Formats.Select(f => f.FormatId), Is.EqualTo(new[] { "sb0", "233", "139", "249", "140", "251", "616" }));
        });

        var aac = video.Formats.Single(f => f.FormatId == "140");
        Assert.That(aac, Is.EqualTo(new YouTubeFormat("140", "m4a", "mp4a.40.2", "none", 129.573, 44100, "https", "medium", null, false)));
        Assert.That(aac.IsAudioOnly && aac.IsAac && !aac.IsOpus);
    }

    [Test]
    public void Falls_back_to_comma_separated_artist_field()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "Song", "artist": "Earth, Wind & Fire, Chaka Khan"}""");

        // yt-dlp joins artists with ", ", so a name containing a comma can't be told apart here.
        Assert.That(video.Artists, Is.EqualTo(new[] { "Earth", "Wind & Fire", "Chaka Khan" }));
        Assert.That(video.Duration, Is.Null);
        Assert.That(video.Formats, Is.Empty);
    }

    [Test]
    public void Parses_one_object_per_line_and_skips_other_output()
    {
        var videos = YouTubeVideoInfo.ParseLines([YtDlpSamples.UnderPressureSearch[0], "", "WARNING: something", YtDlpSamples.UnderPressureSearch[1]]);

        Assert.That(videos.Select(v => v.Id), Is.EqualTo(new[] { "BaxnrepwXck", "nbwn21TU4ec" }));
    }

    [Test]
    public void Non_ascii_text_round_trips()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "D\u00e9j\u00e0 Vu", "artists": ["Beyonc\u00e9", "JA\u0178-Z"]}""");

        Assert.That(video.Title, Is.EqualTo("Déjà Vu"));
        Assert.That(video.Artists, Is.EqualTo(new[] { "Beyoncé", "JAŸ-Z" }));
    }
}

public class AudioFormatSelectorTests
{
    private static YouTubeFormat Audio(string id, string codec, double? abr, string ext = "m4a", string protocol = "https", string? note = null, int? languagePreference = null) =>
        new(id, ext, codec, "none", abr, 44100, protocol, note, languagePreference, HasDrm: false);

    [Test]
    public void Picks_aac_140_even_when_opus_has_a_higher_bitrate()
    {
        var formats = YouTubeVideoInfo.Parse(YtDlpSamples.UnderPressureSearch[0]).Formats;

        var selection = AudioFormatSelector.SelectBestAac(formats);

        Assert.That(selection.Format?.FormatId, Is.EqualTo("140"));
    }

    [Test]
    public void Prefers_premium_256_kbps_aac()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140", "mp4a.40.2", 129.5), Audio("141", "mp4a.40.2", 255.8), Audio("251", "opus", 160, "webm")]);

        Assert.That(selection.Format?.FormatId, Is.EqualTo("141"));
    }

    [Test]
    public void Avoids_dynamic_range_compressed_variant()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140-drc", "mp4a.40.2", 130, note: "medium, DRC"), Audio("140", "mp4a.40.2", 129)]);

        Assert.That(selection.Format?.FormatId, Is.EqualTo("140"));
    }

    [Test]
    public void Prefers_original_audio_track_over_dubs()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140-1", "mp4a.40.2", 130, languagePreference: -1), Audio("140-0", "mp4a.40.2", 129, languagePreference: 10)]);

        Assert.That(selection.Format?.FormatId, Is.EqualTo("140-0"));
    }

    [Test]
    public void Fails_when_only_opus_is_available()
    {
        var formats = YouTubeVideoInfo.Parse(YtDlpSamples.Video("x", "t", null, ["a"], "b", 1, 1, "c", YtDlpSamples.OpusOnlyFormats)).Formats;

        var selection = AudioFormatSelector.SelectBestAac(formats);

        Assert.That(selection.Format, Is.Null);
        Assert.That(selection.FailureReason, Does.Contain("no AAC"));
    }

    [Test]
    public void Fails_when_only_low_quality_aac_is_available()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("139", "mp4a.40.5", 48.9), Audio("251", "opus", 135, "webm")]);

        Assert.That(selection.Format, Is.Null);
        Assert.That(selection.FailureReason, Does.Contain("low-quality AAC audio for this track (49 kbps"));
    }

    [Test]
    public void Ignores_streamed_and_video_formats()
    {
        YouTubeFormat[] formats =
        [
            Audio("233", "mp4a.40.2", 130, "mp4", protocol: "m3u8_native"),
            new("18", "mp4", "mp4a.40.2", "avc1.42001E", 96, 44100, "https", "360p", null, false),
        ];

        Assert.That(AudioFormatSelector.SelectBestAac(formats).Format, Is.Null);
    }
}

public class YtDlpTests
{
    [TestCase("[mp3playersync-progress] 1024 4096 NA", 0.25)]
    [TestCase("[mp3playersync-progress] 2048 NA 4096.5", 2048 / 4096.5)]
    [TestCase("[mp3playersync-progress] 5000 4096 NA", 1.0)]
    public void Parses_progress_lines(string line, double expected)
    {
        Assert.That(YtDlp.TryParseProgressLine(line, out var fraction));
        Assert.That(fraction, Is.EqualTo(expected).Within(1e-9));
    }

    [TestCase("[mp3playersync-progress] 1024 NA NA")]
    [TestCase("[download]  52.1% of    3.84MiB at   15.47MiB/s ETA 00:00")]
    [TestCase("[download] Destination: C:\\x.m4a")]
    public void Ignores_other_lines(string line)
    {
        Assert.That(YtDlp.TryParseProgressLine(line, out _), Is.False);
    }

    [Test]
    public void Search_url_selects_section()
    {
        Assert.That(YtDlp.GetSearchUrl("Déjà Vu Beyoncé", YouTubeMusicSearchSection.Songs),
            Is.EqualTo("https://music.youtube.com/search?q=D%C3%A9j%C3%A0%20Vu%20Beyonc%C3%A9#songs"));
        Assert.That(YtDlp.GetSearchUrl("a&b", YouTubeMusicSearchSection.Videos),
            Is.EqualTo("https://music.youtube.com/search?q=a%26b#videos"));
    }
}
