namespace Mp3PlayerSync.Core.Download.Tools;

/// <summary>
/// The latest available version of a tool and a way to download it.
/// </summary>
/// <param name="isZipArchive">
/// Whether the download is a zip archive to extract into the version folder, rather than the tool file itself.
/// </param>
public sealed class AvailableToolDownload(string version, Func<CancellationToken, Task<Stream>> downloadAsync, bool isZipArchive = false)
{
    public string Version { get; } = version;

    public bool IsZipArchive { get; } = isZipArchive;

    public Task<Stream> DownloadAsync(CancellationToken cancellationToken) => downloadAsync(cancellationToken);
}
