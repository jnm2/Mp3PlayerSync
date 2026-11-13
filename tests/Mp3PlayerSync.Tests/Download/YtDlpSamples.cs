namespace Mp3PlayerSync.Tests.Download;

/// <summary>
/// yt-dlp <c>--dump-json</c> output, trimmed to the relevant fields but otherwise shaped like real YouTube Music
/// search results (yt-dlp 2026.10.26).
/// </summary>
internal static class YtDlpSamples
{
    public static string Video(
        string id,
        string title,
        string? track,
        string[] artists,
        string album,
        double duration,
        int playlistIndex,
        string channel,
        string? formats = null)
    {
        var artistsJson = string.Join(", ", artists.Select(a => $"\"{a}\""));
        var trackJson = track is null ? "" : $"\"track\": \"{track}\", ";

        return $$"""{"id": "{{id}}", "title": "{{title}}", {{trackJson}}"artists": [{{artistsJson}}], "artist": "{{string.Join(", ", artists)}}", "album": "{{album}}", "duration": {{duration}}, "channel": "{{channel}}", "uploader": "{{channel}}", "playlist_index": {{playlistIndex}}, "n_entries": 5, "webpage_url": "https://www.youtube.com/watch?v={{id}}", "ext": "webm", "format_id": "616+251", "formats": [{{formats ?? TypicalFormats}}], "automatic_captions": {}, "_type": "video"}""";
    }

    public const string TypicalFormats = """
        {"format_id": "sb0", "format_note": "storyboard", "ext": "mhtml", "protocol": "mhtml", "acodec": "none", "vcodec": "none", "url": "https://i.ytimg.com/sb/x"},
        {"format_id": "233", "format_note": "Default, low", "ext": "mp4", "protocol": "m3u8_native", "acodec": "unknown", "vcodec": "none", "url": "https://manifest.googlevideo.com/x"},
        {"format_id": "139", "format_note": "low", "ext": "m4a", "protocol": "https", "acodec": "mp4a.40.5", "vcodec": "none", "abr": 48.872, "asr": 22050, "audio_ext": "m4a", "url": "https://rr.googlevideo.com/139"},
        {"format_id": "249", "format_note": "low", "ext": "webm", "protocol": "https", "acodec": "opus", "vcodec": "none", "abr": 48.684, "asr": 48000, "audio_ext": "webm", "url": "https://rr.googlevideo.com/249"},
        {"format_id": "140", "format_note": "medium", "ext": "m4a", "protocol": "https", "acodec": "mp4a.40.2", "vcodec": "none", "abr": 129.573, "asr": 44100, "audio_ext": "m4a", "url": "https://rr.googlevideo.com/140"},
        {"format_id": "251", "format_note": "medium", "ext": "webm", "protocol": "https", "acodec": "opus", "vcodec": "none", "abr": 135.429, "asr": 48000, "audio_ext": "webm", "url": "https://rr.googlevideo.com/251"},
        {"format_id": "616", "format_note": "Premium", "ext": "mp4", "protocol": "m3u8_native", "acodec": "none", "vcodec": "vp09.00.40.08", "url": "https://manifest.googlevideo.com/616"}
        """;

    public const string OpusOnlyFormats = """
        {"format_id": "249", "format_note": "low", "ext": "webm", "protocol": "https", "acodec": "opus", "vcodec": "none", "abr": 48.684, "asr": 48000},
        {"format_id": "251", "format_note": "medium", "ext": "webm", "protocol": "https", "acodec": "opus", "vcodec": "none", "abr": 135.429, "asr": 48000}
        """;

    /// <summary>The top five "Songs" results for "Under Pressure Queen David Bowie".</summary>
    public static string[] UnderPressureSearch =>
    [
        Video("BaxnrepwXck", "Under Pressure", "Under Pressure", ["Queen", "David Bowie"], "Hot Space", 248, 1, "Queen Official"),
        Video("nbwn21TU4ec", "Under Pressure (2017 Remaster)", "Under Pressure (2017 Remaster)", ["Queen", "David Bowie"], "A New Career in a New Town (1977 - 1982)", 249, 2, "Queen Official"),
        Video("C4P4u0p88i0", "Under Pressure (Live)", "Under Pressure (Live)", ["God Save the Queen"], "Live at Luna Park Stadium", 226, 3, "God Save the Queen - Topic"),
        Video("rt7r-Zo7iJo", "Under Pressure (Live At Global Citizen Festival, New York, USA, 2019)", "Under Pressure (Live At Global Citizen Festival, New York, USA, 2019)", ["Queen", "Adam Lambert"], "Live Around the World", 225, 4, "Queen Official"),
        Video("DJxCsVcZL2I", "Rebel Rebel (2014 Remaster)", "Rebel Rebel (2014 Remaster)", ["David Bowie"], "Legacy", 270, 5, "David Bowie"),
    ];
}
