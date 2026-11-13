using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mp3PlayerSync.Core.Download.Tools;

public static class DownloadResolvers
{
    /// <summary>
    /// Resolves the named asset of the latest GitHub release of a repository.
    /// </summary>
    public static Func<CancellationToken, Task<AvailableToolDownload>> GitHubReleaseAsset(
        string owner,
        string repo,
        string assetName,
        string userAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);

        return async cancellationToken =>
        {
            using var client = CreateClient(userAgent);
            client.BaseAddress = new Uri("https://api.github.com");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/repos/{owner}/{repo}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var tag = document.RootElement.GetProperty("tag_name").GetString()
                ?? throw new InvalidDataException($"The latest {owner}/{repo} release has no tag name.");

            // Tags like "v2.9.8" (Deno) are cached as version "2.9.8" so that version folders sort numerically.
            var version = tag.Length > 1 && tag[0] is 'v' or 'V' && char.IsAsciiDigit(tag[1]) ? tag[1..] : tag;

            foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (assetName.Equals(asset.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;

                    var isZipArchive = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                    return new AvailableToolDownload(version, isZipArchive: isZipArchive, downloadAsync: async downloadCancellationToken =>
                    {
                        var downloadClient = CreateClient(userAgent);
                        try
                        {
                            var downloadResponse = await downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadCancellationToken).ConfigureAwait(false);
                            downloadResponse.EnsureSuccessStatusCode();
                            var content = await downloadResponse.Content.ReadAsStreamAsync(downloadCancellationToken).ConfigureAwait(false);
                            return new OwningStream(content, downloadResponse, downloadClient);
                        }
                        catch
                        {
                            downloadClient.Dispose();
                            throw;
                        }
                    });
                }
            }

            throw new InvalidDataException($"The latest {owner}/{repo} release ({version}) has no {assetName} asset.");
        };
    }

    private static HttpClient CreateClient(string userAgent)
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    private sealed class OwningStream(Stream inner, params IDisposable[] owned) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                foreach (var item in owned) item.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
