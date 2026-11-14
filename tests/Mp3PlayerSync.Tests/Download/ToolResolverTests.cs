using System.IO.Compression;
using Mp3PlayerSync.Core.Download.Tools;

namespace Mp3PlayerSync.Tests.Download;

public class ToolResolverTests
{
    private string directory = null!;

    [Before(Test)]
    public void SetUp()
    {
        directory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
    }

    [After(Test)]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    private static AvailableToolDownload ZipDownload(string version)
    {
        return new AvailableToolDownload(version, isZipArchive: true, downloadAsync: _ =>
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("tool.exe").Open())) writer.Write("tool " + version);
                using (var writer = new StreamWriter(zip.CreateEntry("_internal/lib.dll").Open())) writer.Write("lib");
            }

            stream.Position = 0;
            return Task.FromResult<Stream>(stream);
        });
    }

    [Test]
    public async Task Extracts_zip_into_version_folder_and_keeps_leased_versions_when_purging()
    {
        var latest = "1.0";
        var downloads = 0;
        using var resolver = new ToolResolver(directory, "tool.exe", _ =>
        {
            Interlocked.Increment(ref downloads);
            return Task.FromResult(ZipDownload(latest));
        });

        using var lease1 = await resolver.LeaseToolAsync(CancellationToken.None);

        await Assert.That(lease1.FilePath).IsEqualTo(Path.Join(directory, "v1.0", "tool.exe"));
        await Assert.That(File.ReadAllText(lease1.FilePath)).IsEqualTo("tool 1.0");
        await Assert.That(File.Exists(Path.Join(directory, "v1.0", "_internal", "lib.dll"))).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(directory).Select(Path.GetFileName)).IsEquivalentTo(new string?[] { "v1.0" }, CollectionOrdering.Matching).Because("No temporary files are left behind");

        latest = "2.0";
        await resolver.CheckForUpdatesAsync(CancellationToken.None);
        using (var lease2 = await resolver.LeaseToolAsync(CancellationToken.None))
            await Assert.That(lease2.FilePath).IsEqualTo(Path.Join(directory, "v2.0", "tool.exe"));

        resolver.PurgeOldVersions();
        await Assert.That(File.Exists(lease1.FilePath)).IsTrue().Because("A version in use is not deleted");

        lease1.Dispose();
        resolver.PurgeOldVersions();
        await Assert.That(Directory.Exists(Path.Join(directory, "v1.0"))).IsFalse();
        await Assert.That(downloads).IsEqualTo(2);
    }

    [Test]
    public async Task Uses_cached_version_without_checking_for_updates()
    {
        using (var first = new ToolResolver(directory, "tool.exe", _ => Task.FromResult(ZipDownload("1.0"))))
            (await first.LeaseToolAsync(CancellationToken.None)).Dispose();

        using var second = new ToolResolver(directory, "tool.exe", _ => throw new HttpRequestException("Offline"));
        using var lease = await second.LeaseToolAsync(CancellationToken.None);

        await Assert.That(lease.Version).IsEqualTo("1.0");
    }
}
