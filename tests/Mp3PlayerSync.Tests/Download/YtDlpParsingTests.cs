using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeVideoInfoTests
{
    [Test]
    public async Task Parses_search_result_line()
    {
        var video = YouTubeVideoInfo.Parse(YtDlpSamples.UnderPressureSearch[0]);

        using (Assert.Multiple())
        {
            await Assert.That(video.Id).IsEqualTo("BaxnrepwXck");
            await Assert.That(video.Url).IsEqualTo("https://music.youtube.com/watch?v=BaxnrepwXck");
            await Assert.That(video.Track).IsEqualTo("Under Pressure");
            await Assert.That(video.Artists).IsEquivalentTo(new[] { "Queen", "David Bowie" }, CollectionOrdering.Matching);
            await Assert.That(video.Album).IsEqualTo("Hot Space");
            await Assert.That(video.Duration).IsEqualTo(TimeSpan.FromSeconds(248));
            await Assert.That(video.SearchRank).IsEqualTo(1);
            await Assert.That(video.Formats.Select(f => f.FormatId)).IsEquivalentTo(new[] { "sb0", "233", "139", "249", "140", "251", "616" }, CollectionOrdering.Matching);
        }

        var aac = video.Formats.Single(f => f.FormatId == "140");
        await Assert.That(aac).IsEqualTo(new YouTubeFormat("140", "m4a", "mp4a.40.2", "none", 129.573, 44100, "https", "medium", null, false));
        await Assert.That(aac.IsAudioOnly && aac.IsAac && !aac.IsOpus).IsTrue();
    }

    [Test]
    public async Task Falls_back_to_comma_separated_artist_field()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "Song", "artist": "Earth, Wind & Fire, Chaka Khan"}""");

        // yt-dlp joins artists with ", ", so a name containing a comma can't be told apart here.
        await Assert.That(video.Artists).IsEquivalentTo(new[] { "Earth", "Wind & Fire", "Chaka Khan" }, CollectionOrdering.Matching);
        await Assert.That(video.Duration).IsNull();
        await Assert.That(video.Formats).IsEmpty();
    }

    [Test]
    public async Task Parses_one_object_per_line_and_skips_other_output()
    {
        var videos = YouTubeVideoInfo.ParseLines([YtDlpSamples.UnderPressureSearch[0], "", "WARNING: something", YtDlpSamples.UnderPressureSearch[1]]);

        await Assert.That(videos.Select(v => v.Id)).IsEquivalentTo(new[] { "BaxnrepwXck", "nbwn21TU4ec" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Non_ascii_text_round_trips()
    {
        var video = YouTubeVideoInfo.Parse("""{"id": "x", "title": "D\u00e9j\u00e0 Vu", "artists": ["Beyonc\u00e9", "JA\u0178-Z"]}""");

        await Assert.That(video.Title).IsEqualTo("Déjà Vu");
        await Assert.That(video.Artists).IsEquivalentTo(new[] { "Beyoncé", "JAŸ-Z" }, CollectionOrdering.Matching);
    }
}

public class AudioFormatSelectorTests
{
    private static YouTubeFormat Audio(string id, string codec, double? abr, string ext = "m4a", string protocol = "https", string? note = null, int? languagePreference = null) =>
        new(id, ext, codec, "none", abr, 44100, protocol, note, languagePreference, HasDrm: false);

    [Test]
    public async Task Picks_aac_140_even_when_opus_has_a_higher_bitrate()
    {
        var formats = YouTubeVideoInfo.Parse(YtDlpSamples.UnderPressureSearch[0]).Formats;

        var selection = AudioFormatSelector.SelectBestAac(formats);

        await Assert.That(selection.Format?.FormatId).IsEqualTo("140");
    }

    [Test]
    public async Task Prefers_premium_256_kbps_aac()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140", "mp4a.40.2", 129.5), Audio("141", "mp4a.40.2", 255.8), Audio("251", "opus", 160, "webm")]);

        await Assert.That(selection.Format?.FormatId).IsEqualTo("141");
    }

    [Test]
    public async Task Avoids_dynamic_range_compressed_variant()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140-drc", "mp4a.40.2", 130, note: "medium, DRC"), Audio("140", "mp4a.40.2", 129)]);

        await Assert.That(selection.Format?.FormatId).IsEqualTo("140");
    }

    [Test]
    public async Task Prefers_original_audio_track_over_dubs()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("140-1", "mp4a.40.2", 130, languagePreference: -1), Audio("140-0", "mp4a.40.2", 129, languagePreference: 10)]);

        await Assert.That(selection.Format?.FormatId).IsEqualTo("140-0");
    }

    [Test]
    public async Task Fails_when_only_opus_is_available()
    {
        var formats = YouTubeVideoInfo.Parse(YtDlpSamples.Video("x", "t", null, ["a"], "b", 1, 1, "c", YtDlpSamples.OpusOnlyFormats)).Formats;

        var selection = AudioFormatSelector.SelectBestAac(formats);

        await Assert.That(selection.Format).IsNull();
        await Assert.That(selection.FailureReason).Contains("no AAC");
    }

    [Test]
    public async Task Fails_when_only_low_quality_aac_is_available()
    {
        var selection = AudioFormatSelector.SelectBestAac([Audio("139", "mp4a.40.5", 48.9), Audio("251", "opus", 135, "webm")]);

        await Assert.That(selection.Format).IsNull();
        await Assert.That(selection.FailureReason).Contains("low-quality AAC audio for this track (49 kbps");
    }

    [Test]
    public async Task Ignores_streamed_and_video_formats()
    {
        YouTubeFormat[] formats =
        [
            Audio("233", "mp4a.40.2", 130, "mp4", protocol: "m3u8_native"),
            new("18", "mp4", "mp4a.40.2", "avc1.42001E", 96, 44100, "https", "360p", null, false),
        ];

        await Assert.That(AudioFormatSelector.SelectBestAac(formats).Format).IsNull();
    }
}

public class YtDlpTests
{
    [Test]
    [Arguments("[mp3playersync-progress] 1024 4096 NA", 0.25)]
    [Arguments("[mp3playersync-progress] 2048 NA 4096.5", 2048 / 4096.5)]
    [Arguments("[mp3playersync-progress] 5000 4096 NA", 1.0)]
    public async Task Parses_progress_lines(string line, double expected)
    {
        await Assert.That(YtDlp.TryParseProgressLine(line, out var fraction)).IsTrue();
        await Assert.That(fraction).IsEqualTo(expected).Within(1e-9);
    }

    [Test]
    [Arguments("[mp3playersync-progress] 1024 NA NA")]
    [Arguments("[download]  52.1% of    3.84MiB at   15.47MiB/s ETA 00:00")]
    [Arguments("[download] Destination: C:\\x.m4a")]
    public async Task Ignores_other_lines(string line)
    {
        await Assert.That(YtDlp.TryParseProgressLine(line, out _)).IsFalse();
    }

    [Test]
    public async Task Search_url_selects_section()
    {
        await Assert.That(YtDlp.GetSearchUrl("Déjà Vu Beyoncé", YouTubeMusicSearchSection.Songs)).IsEqualTo("https://music.youtube.com/search?q=D%C3%A9j%C3%A0%20Vu%20Beyonc%C3%A9#songs");
        await Assert.That(YtDlp.GetSearchUrl("a&b", YouTubeMusicSearchSection.Videos)).IsEqualTo("https://music.youtube.com/search?q=a%26b#videos");
    }
}
