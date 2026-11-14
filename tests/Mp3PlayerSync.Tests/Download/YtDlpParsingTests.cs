using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeVideoInfoTests
{
    [Test]
    public void Parses_search_result_line()
    {
        var video = YouTubeVideoInfo.Parse(YtDlpSamples.UnderPressureSearch[0]);

        video.ShouldSatisfyAllConditions(
            v => v.Id.ShouldBe("BaxnrepwXck"),
            v => v.Url.ShouldBe("https://music.youtube.com/watch?v=BaxnrepwXck"),
            v => v.Track.ShouldBe("Under Pressure"),
            v => v.Artists.ShouldBe(new[] { "Queen", "David Bowie" }),
            v => v.Album.ShouldBe("Hot Space"),
            v => v.Duration.ShouldBe(TimeSpan.FromSeconds(248)),
            v => v.SearchRank.ShouldBe(1),
            v => v.Formats.Select(f => f.FormatId).ShouldBe(new[] { "sb0", "233", "139", "249", "140", "251", "616" }));

        var aac = video.Formats.Single(f => f.FormatId == "140");
        aac.ShouldBe(new YouTubeFormat("140", "m4a", "mp4a.40.2", "none", 129.573, 44100, "https", "medium", null, false));
        aac.ShouldSatisfyAllConditions(
            a => a.IsAudioOnly.ShouldBeTrue(),
            a => a.IsAac.ShouldBeTrue(),
            a => a.IsOpus.ShouldBeFalse());
    }

    [Test]
    public void Falls_back_to_comma_separated_artist_field()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "Song", "artist": "Earth, Wind & Fire, Chaka Khan"}""");

        // yt-dlp joins artists with ", ", so a name containing a comma can't be told apart here.
        video.Artists.ShouldBe(new[] { "Earth", "Wind & Fire", "Chaka Khan" });
        video.Duration.ShouldBeNull();
        video.Formats.ShouldBeEmpty();
    }

    [Test]
    public void Parses_one_object_per_line_and_skips_other_output()
    {
        var videos = YouTubeVideoInfo.ParseLines([YtDlpSamples.UnderPressureSearch[0], "", "WARNING: something", YtDlpSamples.UnderPressureSearch[1]]);

        videos.Select(v => v.Id).ShouldBe(new[] { "BaxnrepwXck", "nbwn21TU4ec" });
    }

    [Test]
    public void Non_ascii_text_round_trips()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "D\u00e9j\u00e0 Vu", "artists": ["Beyonc\u00e9", "JA\u0178-Z"]}""");

        video.Title.ShouldBe("Déjà Vu");
        video.Artists.ShouldBe(new[] { "Beyoncé", "JAŸ-Z" });
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

        selection.Format.ShouldNotBeNull().FormatId.ShouldBe("140");
    }

    [Test]
    public void Prefers_premium_256_kbps_aac()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140", "mp4a.40.2", 129.5), Audio("141", "mp4a.40.2", 255.8), Audio("251", "opus", 160, "webm")]);

        selection.Format.ShouldNotBeNull().FormatId.ShouldBe("141");
    }

    [Test]
    public void Avoids_dynamic_range_compressed_variant()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140-drc", "mp4a.40.2", 130, note: "medium, DRC"), Audio("140", "mp4a.40.2", 129)]);

        selection.Format.ShouldNotBeNull().FormatId.ShouldBe("140");
    }

    [Test]
    public void Prefers_original_audio_track_over_dubs()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140-1", "mp4a.40.2", 130, languagePreference: -1), Audio("140-0", "mp4a.40.2", 129, languagePreference: 10)]);

        selection.Format.ShouldNotBeNull().FormatId.ShouldBe("140-0");
    }

    [Test]
    public void Fails_when_only_opus_is_available()
    {
        var formats = YouTubeVideoInfo.Parse(YtDlpSamples.Video("x", "t", null, ["a"], "b", 1, 1, "c", YtDlpSamples.OpusOnlyFormats)).Formats;

        var selection = AudioFormatSelector.SelectBestAac(formats);

        selection.Format.ShouldBeNull();
        selection.FailureReason.ShouldNotBeNull().ShouldContain("no AAC", Case.Sensitive);
    }

    [Test]
    public void Fails_when_only_low_quality_aac_is_available()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("139", "mp4a.40.5", 48.9), Audio("251", "opus", 135, "webm")]);

        selection.Format.ShouldBeNull();
        selection.FailureReason.ShouldNotBeNull().ShouldContain("low-quality AAC audio for this track (49 kbps", Case.Sensitive);
    }

    [Test]
    public void Ignores_streamed_and_video_formats()
    {
        YouTubeFormat[] formats =
        [
            Audio("233", "mp4a.40.2", 130, "mp4", protocol: "m3u8_native"),
            new("18", "mp4", "mp4a.40.2", "avc1.42001E", 96, 44100, "https", "360p", null, false),
        ];

        AudioFormatSelector.SelectBestAac(formats).Format.ShouldBeNull();
    }
}

public class YtDlpTests
{
    [Test]
    [Arguments("[mp3playersync-progress] 1024 4096 NA", 0.25)]
    [Arguments("[mp3playersync-progress] 2048 NA 4096.5", 2048 / 4096.5)]
    [Arguments("[mp3playersync-progress] 5000 4096 NA", 1.0)]
    public void Parses_progress_lines(string line, double expected)
    {
        YtDlp.TryParseProgressLine(line, out var fraction).ShouldBeTrue();
        fraction.ShouldBe(expected, 1e-9);
    }

    [Test]
    [Arguments("[mp3playersync-progress] 1024 NA NA")]
    [Arguments("[download]  52.1% of    3.84MiB at   15.47MiB/s ETA 00:00")]
    [Arguments("[download] Destination: C:\\x.m4a")]
    public void Ignores_other_lines(string line)
    {
        YtDlp.TryParseProgressLine(line, out _).ShouldBeFalse();
    }

    [Test]
    public void Search_url_selects_section()
    {
        YtDlp.GetSearchUrl("Déjà Vu Beyoncé", YouTubeMusicSearchSection.Songs).ShouldBe("https://music.youtube.com/search?q=D%C3%A9j%C3%A0%20Vu%20Beyonc%C3%A9#songs");
        YtDlp.GetSearchUrl("a&b", YouTubeMusicSearchSection.Videos).ShouldBe("https://music.youtube.com/search?q=a%26b#videos");
    }
}
