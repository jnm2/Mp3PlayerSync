using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

/// <summary>
/// Downloads yt-dlp and a real track from YouTube Music. Run explicitly, e.g.
/// <c>dotnet test --project tests\Mp3PlayerSync.Tests --treenode-filter "/*/*/*/*[Category=Live]"</c>.
/// </summary>
[Explicit("Uses the network: downloads yt-dlp from GitHub and a track from YouTube Music.")]
[Category("Live")]
public class YouTubeMusicLiveTests
{
    [Test]
    public async Task Tools_work_with_a_javascript_runtime()
    {
        using var downloader = new YouTubeMusicDownloader(new() { ToolCacheDirectory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", "Tools") });
        await downloader.UpdateToolAsync(CancellationToken.None);

        var check = await downloader.CheckToolsAsync(CancellationToken.None);

        Console.Out.WriteLine($"yt-dlp {check.YtDlpVersion}, Deno {check.DenoVersion ?? "(none)"}");
        Console.Out.WriteLine("Audio formats: " + string.Join(", ", check.AudioFormats));
        foreach (var message in check.Messages) Console.Out.WriteLine(message);

        check.DenoVersion.ShouldNotBeNull(customMessage: "Deno couldn't be downloaded");
        check.HasJavaScriptRuntimeWarning.ShouldBeFalse(customMessage: "yt-dlp didn't find its JavaScript runtime");
        check.BestAac.ShouldNotBeNull().FormatId.ShouldBeOneOf(["140", "141"], "No good AAC format; YouTube may have changed");
    }

    [Test]
    public async Task Downloads_a_well_known_track_as_aac()
    {
        var root = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests");
        using var downloader = new YouTubeMusicDownloader(new() { ToolCacheDirectory = Path.Join(root, "Tools") });
        await downloader.UpdateToolAsync(CancellationToken.None);

        var track = new ExportedTrack(1, "spotify:track:2grjqo0Frpf2okIBiifQKs", "September", ["Earth, Wind & Fire"], "The Best Of Earth, Wind & Fire, Vol. 1",
            TimeSpan.FromMilliseconds(215093), "1978-11-23", null, "", false, "USSM17800845", "");

        var resolution = (await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem();
        resolution.State.ShouldBe(TrackResolutionState.Resolved, customMessage: resolution.Reason);
        Console.Out.WriteLine(resolution.Video);

        var request = new TrackDownloadRequest(resolution, Path.Join(root, Path.GetRandomFileName() + ".m4a"));

        try
        {
            var result = await downloader.DownloadTrackAsync(request, null, CancellationToken.None);

            result.FailureReason.ShouldBeNull();
            Console.Out.WriteLine($"{result.Format} {result.Audio}");
            result.Audio.ShouldNotBeNull().IsAac.ShouldBeTrue();
            result.Audio.Duration.TotalSeconds.ShouldBe(215, 5);
            M4aRemuxer.ReadAudioInfo(request.DestinationPath).ShouldBe(result.Audio);
        }
        finally
        {
            File.Delete(request.DestinationPath);
        }
    }
}
