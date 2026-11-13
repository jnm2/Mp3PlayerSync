using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class DownloadCacheTests
{
    private string directory = null!;
    private string source = null!;

    [SetUp]
    public void SetUp()
    {
        var temp = Path.Combine(Path.GetTempPath(), "Mp3PlayerSyncTests", Guid.NewGuid().ToString("N"));
        directory = Path.Combine(temp, "Cache");
        source = Path.Combine(Directory.CreateDirectory(temp).FullName, "download.m4a");
        File.WriteAllBytes(source, new byte[1000]);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(Path.GetDirectoryName(source)!, recursive: true);

    [Test]
    public void Keeps_a_download_by_video_and_format()
    {
        var cache = new DownloadCache(directory);

        cache.Add("dQw4w9WgXcQ", "140", "https://lh3.googleusercontent.com/art", source);

        Assert.That(cache.TryGet("dQw4w9WgXcQ"), Is.EqualTo(new CachedDownload("dQw4w9WgXcQ", "140", Path.Combine(directory, "dQw4w9WgXcQ.140.m4a"), "https://lh3.googleusercontent.com/art")));
        Assert.That(cache.TryGet("other"), Is.Null);

        cache.Remove("dQw4w9WgXcQ");
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [TestCase("..\\evil")]
    [TestCase("a/b")]
    [TestCase("")]
    public void Ignores_ids_that_are_not_file_name_safe(string videoId)
    {
        var cache = new DownloadCache(directory);

        cache.Add(videoId, "140", null, source);

        Assert.That(Directory.Exists(directory), Is.False);
        Assert.That(cache.TryGet(videoId), Is.Null);
    }

    [Test]
    public void Prunes_old_entries_then_the_oldest_until_it_fits()
    {
        var cache = new DownloadCache(directory) { MaxAge = TimeSpan.FromDays(30), MaxBytes = 2500 };
        foreach (var id in new[] { "old", "a", "b", "c" }) cache.Add(id, "140", null, source);
        var now = DateTimeOffset.UtcNow;
        SetSavedAt("old", now.AddDays(-31));
        SetSavedAt("a", now.AddDays(-3));
        SetSavedAt("b", now.AddDays(-2));
        SetSavedAt("c", now.AddDays(-1));

        cache.Prune(now);

        Assert.That(new[] { "old", "a", "b", "c" }.Select(id => cache.TryGet(id) is not null), Is.EqualTo(new[] { false, false, true, true }));
    }

    [Test]
    public void Prune_removes_leftovers_from_a_crash_but_not_a_copy_in_progress()
    {
        Directory.CreateDirectory(directory);
        var crashed = Path.Combine(directory, "x.140.m4a.0123.tmp");
        var inProgress = Path.Combine(directory, "y.140.m4a.4567.tmp");
        File.WriteAllText(crashed, "");
        File.WriteAllText(inProgress, "");
        File.SetLastWriteTimeUtc(crashed, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(directory, "z.140.m4a"), "no info file");

        new DownloadCache(directory).Prune(DateTimeOffset.UtcNow);

        Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName), Is.EqualTo(new[] { "y.140.m4a.4567.tmp" }));
    }

    private void SetSavedAt(string videoId, DateTimeOffset savedAt) =>
        File.WriteAllText(Path.Combine(directory, videoId + ".140.json"), $"{{\"coverArtUrl\":null,\"savedAt\":\"{savedAt:O}\"}}");
}
