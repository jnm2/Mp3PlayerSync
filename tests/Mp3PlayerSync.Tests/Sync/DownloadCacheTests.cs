using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class DownloadCacheTests
{
    private string directory = null!;
    private string source = null!;

    [Before(Test)]
    public void SetUp()
    {
        var temp = Path.Combine(Path.GetTempPath(), "Mp3PlayerSyncTests", Guid.NewGuid().ToString("N"));
        directory = Path.Combine(temp, "Cache");
        source = Path.Combine(Directory.CreateDirectory(temp).FullName, "download.m4a");
        File.WriteAllBytes(source, new byte[1000]);
    }

    [After(Test)]
    public void TearDown() => Directory.Delete(Path.GetDirectoryName(source)!, recursive: true);

    [Test]
    public async Task Keeps_a_download_by_video_and_format()
    {
        var cache = new DownloadCache(directory);

        cache.Add("dQw4w9WgXcQ", "140", "https://lh3.googleusercontent.com/art", source);

        await Assert.That(cache.TryGet("dQw4w9WgXcQ")).IsEqualTo(new CachedDownload("dQw4w9WgXcQ", "140", Path.Combine(directory, "dQw4w9WgXcQ.140.m4a"), "https://lh3.googleusercontent.com/art"));
        await Assert.That(cache.TryGet("other")).IsNull();

        cache.Remove("dQw4w9WgXcQ");
        await Assert.That(Directory.GetFiles(directory)).IsEmpty();
    }

    [Test]
    [Arguments("..\\evil")]
    [Arguments("a/b")]
    [Arguments("")]
    public async Task Ignores_ids_that_are_not_file_name_safe(string videoId)
    {
        var cache = new DownloadCache(directory);

        cache.Add(videoId, "140", null, source);

        await Assert.That(Directory.Exists(directory)).IsFalse();
        await Assert.That(cache.TryGet(videoId)).IsNull();
    }

    [Test]
    public async Task Prunes_old_entries_then_the_oldest_until_it_fits()
    {
        var cache = new DownloadCache(directory) { MaxAge = TimeSpan.FromDays(30), MaxBytes = 2500 };
        foreach (var id in new[] { "old", "a", "b", "c" }) cache.Add(id, "140", null, source);
        var now = DateTimeOffset.UtcNow;
        SetSavedAt("old", now.AddDays(-31));
        SetSavedAt("a", now.AddDays(-3));
        SetSavedAt("b", now.AddDays(-2));
        SetSavedAt("c", now.AddDays(-1));

        cache.Prune(now);

        await Assert.That(new[] { "old", "a", "b", "c" }.Select(id => cache.TryGet(id) is not null)).IsEquivalentTo(new[] { false, false, true, true }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Prune_removes_leftovers_from_a_crash_but_not_a_copy_in_progress()
    {
        Directory.CreateDirectory(directory);
        var crashed = Path.Combine(directory, "x.140.m4a.0123.tmp");
        var inProgress = Path.Combine(directory, "y.140.m4a.4567.tmp");
        File.WriteAllText(crashed, "");
        File.WriteAllText(inProgress, "");
        File.SetLastWriteTimeUtc(crashed, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(directory, "z.140.m4a"), "no info file");

        new DownloadCache(directory).Prune(DateTimeOffset.UtcNow);

        await Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName)).IsEquivalentTo(new string?[] { "y.140.m4a.4567.tmp" }, CollectionOrdering.Matching);
    }

    private void SetSavedAt(string videoId, DateTimeOffset savedAt) =>
        File.WriteAllText(Path.Combine(directory, videoId + ".140.json"), $"{{\"coverArtUrl\":null,\"savedAt\":\"{savedAt:O}\"}}");
}
