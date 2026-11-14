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
    public void Keeps_a_download_by_video_and_format()
    {
        var cache = new DownloadCache(directory);

        cache.Add("dQw4w9WgXcQ", "140", "https://lh3.googleusercontent.com/art", source);

        cache.TryGet("dQw4w9WgXcQ").ShouldBe(new CachedDownload("dQw4w9WgXcQ", "140", Path.Combine(directory, "dQw4w9WgXcQ.140.m4a"), "https://lh3.googleusercontent.com/art"));
        cache.TryGet("other").ShouldBeNull();

        cache.Remove("dQw4w9WgXcQ");
        Directory.GetFiles(directory).ShouldBeEmpty();
    }

    [Test]
    [Arguments("..\\evil")]
    [Arguments("a/b")]
    [Arguments("")]
    public void Ignores_ids_that_are_not_file_name_safe(string videoId)
    {
        var cache = new DownloadCache(directory);

        cache.Add(videoId, "140", null, source);

        Directory.Exists(directory).ShouldBeFalse();
        cache.TryGet(videoId).ShouldBeNull();
    }

    [Test]
    public void Prunes_old_entries_then_the_oldest_until_it_fits()
    {
        var cache = new DownloadCache(directory) { MaxAge = TimeSpan.FromDays(30), MaxBytes = 2500 };
        string[] ids = ["old", "a", "b", "c"];
        foreach (var id in ids) cache.Add(id, "140", null, source);
        var now = DateTimeOffset.UtcNow;
        SetSavedAt("old", now.AddDays(-31));
        SetSavedAt("a", now.AddDays(-3));
        SetSavedAt("b", now.AddDays(-2));
        SetSavedAt("c", now.AddDays(-1));

        cache.Prune(now);

        ids.Where(id => cache.TryGet(id) is not null).ShouldBe(["b", "c"]);
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

        Directory.GetFiles(directory).Select(Path.GetFileName).ShouldBe(["y.140.m4a.4567.tmp"]);
    }

    private void SetSavedAt(string videoId, DateTimeOffset savedAt) =>
        File.WriteAllText(Path.Combine(directory, videoId + ".140.json"), $"{{\"coverArtUrl\":null,\"savedAt\":\"{savedAt:O}\"}}");
}
