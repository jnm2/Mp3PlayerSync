using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeUrlsTests
{
    [Test]
    [Arguments("https://music.youtube.com/watch?v=BaxnrepwXck")]
    [Arguments("https://music.youtube.com/watch?v=BaxnrepwXck&list=RDAMVMBaxnrepwXck")]
    [Arguments("https://www.youtube.com/watch?feature=share&v=BaxnrepwXck")]
    [Arguments("https://m.youtube.com/watch?v=BaxnrepwXck")]
    [Arguments("https://youtu.be/BaxnrepwXck?si=abc")]
    [Arguments("https://www.youtube.com/shorts/BaxnrepwXck")]
    [Arguments("youtube.com/watch?v=BaxnrepwXck")]
    [Arguments("  BaxnrepwXck ")]
    public async Task Gets_video_id(string input)
    {
        var found = YouTubeUrls.TryGetVideoId(input, out var videoId, out var error);
        await Assert.That(found).IsTrue().Because(error ?? "");
        await Assert.That(videoId).IsEqualTo("BaxnrepwXck");
    }

    [Test]
    [Arguments("https://music.youtube.com/playlist?list=OLAK5uy_abc", "playlist")]
    [Arguments("https://music.youtube.com/browse/MPREb_abc", "playlist")]
    [Arguments("https://www.youtube.com/channel/UCabc", "playlist")]
    [Arguments("https://open.spotify.com/track/2grjqo0Frpf2okIBiifQKs", "isn't a YouTube")]
    [Arguments("https://www.youtube.com/watch?v=tooshort", "isn't to a YouTube video")]
    [Arguments("not a link", "isn't a YouTube")]
    public async Task Rejects_other_links(string input, string expectedError)
    {
        await Assert.That(YouTubeUrls.TryGetVideoId(input, out _, out var error)).IsFalse();
        await Assert.That(error).Contains(expectedError);
    }
}
