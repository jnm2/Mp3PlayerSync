using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Sync;

public class OpenPlaylistFindingTests
{
    private static ExportedTrack Song(string title) => new(0, "spotify:track:" + title, title, ["Artist"], "", TimeSpan.FromMinutes(3), "", null, "", null, "", "");

    private static string Key(ExportedTrack track) => track.Uri;

    private static (OpenPlaylistFinding Finding, RecordingFinder Finder) Create()
    {
        var finder = new RecordingFinder();
        return (new OpenPlaylistFinding(() => finder), finder);
    }

    [Test]
    public void Only_the_open_playlists_songs_are_looked_for()
    {
        var (finding, finder) = Create();

        finding.Find([Song("A1"), Song("A2")], Key);
        finding.Find([Song("B1")], Key);

        finder.Calls.ShouldBe(new[] { "A1,A2", "B1" }, ignoreOrder: false, customMessage: "Opening another playlist replaces what was queued");
    }

    [Test]
    public void Leaving_the_playlist_drops_its_queued_lookups()
    {
        var (finding, finder) = Create();
        finding.Find([Song("A1"), Song("A2")], Key);

        finding.Stop();

        finder.Calls.ShouldBe(new[] { "A1,A2", "" });
    }

    [Test]
    public void Nothing_is_looked_for_in_the_background_during_a_sync()
    {
        var (finding, finder) = Create();
        finding.Find([Song("A1"), Song("A2")], Key);

        finding.PauseForSync();
        var during = finding.Find([Song("B1")], Key);
        finding.ResumeAfterSync();
        finding.Find([Song("B1")], Key);

        during.ShouldBeNull();
        finder.Calls.ShouldBe(new[] { "A1,A2", "", "B1" });
    }

    [Test]
    public void The_same_songs_are_not_queued_again_until_stopped()
    {
        var (finding, finder) = Create();

        finding.Find([Song("A1")], Key).ShouldNotBeNull();
        finding.Find([Song("A1")], Key).ShouldBeNull();
        finding.Stop();
        finding.Find([Song("A1")], Key).ShouldNotBeNull(customMessage: "A reloaded playlist is looked for again");

        finder.Calls.ShouldBe(new[] { "A1", "", "A1" });
    }

    [Test]
    public void A_playlist_with_nothing_to_look_for_does_not_start_the_finder()
    {
        var created = false;
        var finding = new OpenPlaylistFinding(() => { created = true; return new RecordingFinder(); });

        finding.Find([], Key).ShouldBeEmpty();
        finding.Stop();
        finding.PauseForSync();

        created.ShouldBeFalse();
    }

    private sealed class RecordingFinder : ITrackFinder
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<TrackResolution?> FindInBackground(IReadOnlyList<ExportedTrack> tracks)
        {
            Calls.Add(string.Join(",", tracks.Select(t => t.Title)));
            return tracks.Select(_ => (TrackResolution?)null).ToList();
        }

        public event EventHandler<TrackResolutionProgress>? LookupProgress { add { } remove { } }

        public Task<IReadOnlyList<TrackResolution>> ResolveTracksAsync(IReadOnlyList<ExportedTrack> tracks, IProgress<TrackResolutionProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManualChoiceResult> ChooseVideoAsync(ExportedTrack track, string urlOrVideoId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public TrackResolution Skip(ExportedTrack track) => throw new NotSupportedException();
        public bool Forget(ExportedTrack track) => throw new NotSupportedException();
        public TrackResolution? GetCachedResolution(ExportedTrack track) => throw new NotSupportedException();
        public Task<TrackResolution> RetryAsync(ExportedTrack track, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrackDownloadResult>> DownloadTracksAsync(IReadOnlyList<TrackDownloadRequest> requests, Action<TrackDownloadProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
