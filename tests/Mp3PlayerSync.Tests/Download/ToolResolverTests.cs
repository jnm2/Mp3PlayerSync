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

        lease1.FilePath.ShouldBe(Path.Join(directory, "v1.0", "tool.exe"));
        File.ReadAllText(lease1.FilePath).ShouldBe("tool 1.0");
        File.Exists(Path.Join(directory, "v1.0", "_internal", "lib.dll")).ShouldBeTrue();
        Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ShouldBe(["v1.0"], customMessage: "No temporary files are left behind");

        latest = "2.0";
        await resolver.CheckForUpdatesAsync(CancellationToken.None);
        using (var lease2 = await resolver.LeaseToolAsync(CancellationToken.None))
            lease2.FilePath.ShouldBe(Path.Join(directory, "v2.0", "tool.exe"));

        resolver.PurgeOldVersions();
        File.Exists(lease1.FilePath).ShouldBeTrue(customMessage: "A version in use is not deleted");

        lease1.Dispose();
        resolver.PurgeOldVersions();
        Directory.Exists(Path.Join(directory, "v1.0")).ShouldBeFalse();
        downloads.ShouldBe(2);
    }

    [Test]
    public async Task Uses_cached_version_without_checking_for_updates()
    {
        using (var first = new ToolResolver(directory, "tool.exe", _ => Task.FromResult(ZipDownload("1.0"))))
            (await first.LeaseToolAsync(CancellationToken.None)).Dispose();

        using var second = new ToolResolver(directory, "tool.exe", _ => throw new HttpRequestException("Offline"));
        using var lease = await second.LeaseToolAsync(CancellationToken.None);

        lease.Version.ShouldBe("1.0");
    }
}
