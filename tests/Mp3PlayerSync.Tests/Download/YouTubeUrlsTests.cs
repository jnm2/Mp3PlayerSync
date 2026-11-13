using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeUrlsTests
{
    [TestCase("https://music.youtube.com/watch?v=BaxnrepwXck")]
    [TestCase("https://music.youtube.com/watch?v=BaxnrepwXck&list=RDAMVMBaxnrepwXck")]
    [TestCase("https://www.youtube.com/watch?feature=share&v=BaxnrepwXck")]
    [TestCase("https://m.youtube.com/watch?v=BaxnrepwXck")]
    [TestCase("https://youtu.be/BaxnrepwXck?si=abc")]
    [TestCase("https://www.youtube.com/shorts/BaxnrepwXck")]
    [TestCase("youtube.com/watch?v=BaxnrepwXck")]
    [TestCase("  BaxnrepwXck ")]
    public void Gets_video_id(string input)
    {
        Assert.That(YouTubeUrls.TryGetVideoId(input, out var videoId, out var error), error);
        Assert.That(videoId, Is.EqualTo("BaxnrepwXck"));
    }

    [TestCase("https://music.youtube.com/playlist?list=OLAK5uy_abc", "playlist")]
    [TestCase("https://music.youtube.com/browse/MPREb_abc", "playlist")]
    [TestCase("https://www.youtube.com/channel/UCabc", "playlist")]
    [TestCase("https://open.spotify.com/track/2grjqo0Frpf2okIBiifQKs", "isn't a YouTube")]
    [TestCase("https://www.youtube.com/watch?v=tooshort", "isn't to a YouTube video")]
    [TestCase("not a link", "isn't a YouTube")]
    public void Rejects_other_links(string input, string expectedError)
    {
        Assert.That(YouTubeUrls.TryGetVideoId(input, out _, out var error), Is.False);
        Assert.That(error, Does.Contain(expectedError));
    }
}
