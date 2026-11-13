using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Scanning;
using Mp3PlayerSync.Core.Sync;
using Mp3PlayerSync.Core.Tagging;
using static Mp3PlayerSync.Tests.Placement.PlaylistPlannerTests;

namespace Mp3PlayerSync.Tests.Sync;

public class PlayerSyncerTests
{
    private string root = null!;
    private string work = null!;

    [SetUp]
    public void SetUp()
    {
        var temp = Path.Combine(Path.GetTempPath(), "Mp3PlayerSyncTests", Guid.NewGuid().ToString("N"));
        root = Directory.CreateDirectory(Path.Combine(temp, "Player")).FullName;
        work = Directory.CreateDirectory(Path.Combine(temp, "Work")).FullName;
    }

    [TearDown]
    public void TearDown() => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);

    [Test]
    public async Task Only_resolved_songs_are_downloaded_and_placed()
    {
        ExportedTrack[] songs = [Song("One"), Song("Two"), Song("Three")];
        var finder = new FakeFinder();
        var progress = new List<TrackSyncProgress>();

        using var library = await StartLibraryAsync();
        var result = await new PlayerSyncer(finder, new TrackTagWriter(), work).SyncAsync(
            library,
            Request(songs, resolutions: [Resolved(songs[0]), new TrackResolution(songs[1], TrackResolutionState.Skipped, null, [], null, FromCache: true)]),
            new Collector(progress),
            CancellationToken.None);

        Assert.That(finder.Downloaded, Is.EqualTo(new[] { "One" }));
        Assert.That(AudioFilesOnPlayer(), Is.EqualTo(new[] { Path.Combine("Mix", "01. Queen - One.m4a") }));
        Assert.That(progress.Where(p => p.State == TrackSyncState.Done).Select(p => p.Track.Title), Is.EqualTo(new[] { "One" }));
        Assert.That(result.Added, Is.EqualTo(new[] { songs[0] }).Using((IEqualityComparer<ExportedTrack>)ReferenceEqualityComparer.Instance), "The result says what was added, without relying on progress reports");
    }

    [Test]
    public async Task A_video_that_has_gone_is_sent_back_for_review()
    {
        ExportedTrack[] songs = [Song("One"), Song("Two")];
        var finder = new FakeFinder { Gone = { "Two" } };
        var progress = new List<TrackSyncProgress>();

        using var library = await StartLibraryAsync();
        var result = await new PlayerSyncer(finder, new TrackTagWriter(), work).SyncAsync(
            library, Request(songs, resolutions: songs.Select(Resolved)), new Collector(progress), CancellationToken.None);

        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Added, Is.EqualTo(new[] { songs[0] }).Using((IEqualityComparer<ExportedTrack>)ReferenceEqualityComparer.Instance));
        Assert.That(progress.Last(p => p.Track == songs[1]).State, Is.EqualTo(TrackSyncState.NeedsReview));
        Assert.That(result.NeedsReview, Is.EqualTo(new[] { songs[1] }).Using((IEqualityComparer<ExportedTrack>)ReferenceEqualityComparer.Instance));
        Assert.That(result.Failed, Is.Empty);
        Assert.That(progress.Last(p => p.Track == songs[0]).State, Is.EqualTo(TrackSyncState.Done));
    }

    [Test]
    public async Task Cancelling_before_anything_is_copied_leaves_the_player_untouched_and_keeps_the_downloads()
    {
        ExportedTrack[] songs = [Song("One"), Song("Two")];
        using var cancellation = new CancellationTokenSource();
        var finder = new FakeFinder { AfterDownloads = cancellation.Cancel };
        var cache = new DownloadCache(Path.Combine(work, "..", "Cache"));

        using var library = await StartLibraryAsync();
        Assert.That(
            async () => await new PlayerSyncer(finder, new TrackTagWriter(), work) { Cache = cache }.SyncAsync(
                library, Request(songs, resolutions: songs.Select(Resolved)), new Collector([]), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());

        Assert.That(Directory.GetFileSystemEntries(root), Is.Empty, "Nothing, not even the playlist's folder, is written to the player");
        Assert.That(Directory.GetFiles(work, "*", SearchOption.AllDirectories), Is.Empty, "Staging is cleaned up");
        Assert.That(songs.Select(s => cache.TryGet(VideoIdOf(s)) is not null), Is.All.True, "The downloads are kept for next time");
    }

    [Test]
    public async Task A_sync_cancelled_mid_download_resumes_without_downloading_again()
    {
        ExportedTrack[] songs = [Song("One"), Song("Two"), Song("Three")];
        var cache = new DownloadCache(Path.Combine(work, "..", "Cache"));
        using var cancellation = new CancellationTokenSource();
        var interrupted = new FakeFinder { CancelBefore = "Two", Cancellation = cancellation };

        using var library = await StartLibraryAsync();
        Assert.That(
            async () => await new PlayerSyncer(interrupted, new TrackTagWriter(), work) { Cache = cache }.SyncAsync(
                library, Request(songs, resolutions: songs.Select(Resolved)), new Collector([]), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(interrupted.Downloaded, Is.EqualTo(new[] { "One" }));

        var resumed = new FakeFinder();
        var progress = new List<TrackSyncProgress>();
        await new PlayerSyncer(resumed, new TrackTagWriter(), work) { Cache = cache }.SyncAsync(
            library, Request(songs, resolutions: songs.Select(Resolved)), new Collector(progress), CancellationToken.None);

        Assert.That(resumed.Downloaded, Is.EqualTo(new[] { "Two", "Three" }));
        Assert.That(AudioFilesOnPlayer(), Has.Length.EqualTo(3));
        Assert.That(progress.Where(p => p.State == TrackSyncState.Done).Select(p => p.Track.Title), Is.EquivalentTo(new[] { "One", "Two", "Three" }));
        Assert.That(Directory.GetFiles(cache.Directory), Is.Empty, "Songs on the player aren't kept");
    }

    [Test]
    public async Task A_player_locked_since_the_sync_started_gets_nothing_written()
    {
        ExportedTrack[] songs = [Song("One"), Song("Two")];
        var progress = new List<TrackSyncProgress>();

        using var library = await StartLibraryAsync();
        var result = await new PlayerSyncer(new FakeFinder(), new TrackTagWriter(), work) { IsWriteProtected = _ => true }.SyncAsync(
            library, Request(songs, resolutions: songs.Select(Resolved)), new Collector(progress), CancellationToken.None);

        Assert.That(result.Errors, Is.EqualTo(new[] { "This card is locked" }));
        Assert.That(result.Added, Is.Empty);
        Assert.That(result.Failed.Select(f => (f.Track.Title, f.Message)), Is.EqualTo(new[] { ("One", "This card is locked"), ("Two", "This card is locked") }));
        Assert.That(Directory.GetFileSystemEntries(root), Is.Empty);
        Assert.That(songs.Select(s => progress.Last(p => p.Track == s).State), Is.All.EqualTo(TrackSyncState.Failed));
    }

    [Test]
    public async Task Songs_get_the_cover_art_of_their_youtube_match()
    {
        ExportedTrack[] songs = [Song("One")];
        var fetcher = new RecordingArtFetcher();

        using var library = await StartLibraryAsync();
        await new PlayerSyncer(new FakeFinder { CoverArtUrl = "https://lh3.googleusercontent.com/art=w500-h500-l90-rj" }, new TrackTagWriter(fetcher), work).SyncAsync(
            library, Request(songs, resolutions: songs.Select(Resolved)), new Collector([]), CancellationToken.None);

        Assert.That(fetcher.Urls, Is.EqualTo(new[] { "https://lh3.googleusercontent.com/art=w500-h500-l90-rj" }));
        using var file = TagLib.File.Create(Path.Combine(root, AudioFilesOnPlayer().Single()));
        Assert.That(file.Tag.Pictures.Single().Data.Data, Is.EqualTo(RecordingArtFetcher.Jpeg));
    }

    private static SyncRequest Request(IReadOnlyList<ExportedTrack> songs, IEnumerable<TrackResolution> resolutions)
    {
        var byTrack = new Dictionary<ExportedTrack, TrackResolution>(ReferenceEqualityComparer.Instance);
        foreach (var resolution in resolutions) byTrack[resolution.Track] = resolution;
        return new SyncRequest(LibraryItemKind.Playlist, "https://open.spotify.com/playlist/mix", "Mix", "me", songs, songs, [], byTrack);
    }

    private static TrackResolution Resolved(ExportedTrack track) => new(
        track,
        TrackResolutionState.Resolved,
        new ResolvedVideo(VideoIdOf(track), track.Title, track.Artists, track.Album, track.Duration, 1, ResolutionSource.Automatic, DateTimeOffset.UtcNow),
        [],
        null,
        FromCache: true);

    private static string VideoIdOf(ExportedTrack track) => "video" + track.Title;

    private async Task<PlayerLibrary> StartLibraryAsync()
    {
        var library = new PlayerLibrary(root, new PlayerLibraryOptions { WatchForChanges = false });
        library.Start();
        await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30));
        return library;
    }

    private string[] AudioFilesOnPlayer() =>
        Directory.GetFiles(root, "*.m4a", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).ToArray();

    private sealed class Collector(List<TrackSyncProgress> list) : IProgress<TrackSyncProgress>
    {
        public void Report(TrackSyncProgress value)
        {
            lock (list) list.Add(value);
        }
    }

    private sealed class RecordingArtFetcher : IAlbumArtFetcher
    {
        public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0xFF, 0xD9];

        public List<string> Urls { get; } = [];

        public Task<AlbumArt?> FetchAsync(string url, CancellationToken cancellationToken = default)
        {
            lock (Urls) Urls.Add(url);
            return Task.FromResult<AlbumArt?>(new AlbumArt(Jpeg, "image/jpeg"));
        }
    }

    private sealed class FakeFinder : ITrackFinder
    {
        public List<string> Downloaded { get; } = [];

        public HashSet<string> Gone { get; } = [];

        public Action? AfterDownloads { get; init; }

        public string? CancelBefore { get; init; }

        public CancellationTokenSource? Cancellation { get; init; }

        public string? CoverArtUrl { get; init; }

        public Task<IReadOnlyList<TrackDownloadResult>> DownloadTracksAsync(IReadOnlyList<TrackDownloadRequest> requests, Action<TrackDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            var results = new List<TrackDownloadResult>();
            foreach (var request in requests)
            {
                if (Gone.Contains(request.Track.Title))
                {
                    results.Add(new(request, null, null, "The video is no longer available.", NeedsReview: true));
                    continue;
                }

                if (request.Track.Title == CancelBefore)
                {
                    Cancellation!.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Tagging", "Assets", "silence.m4a"), request.DestinationPath, overwrite: true);
                Downloaded.Add(request.Track.Title);
                var result = new TrackDownloadResult(request, null, null, null) { CoverArtUrl = CoverArtUrl };
                progress?.Invoke(new(request, TrackDownloadStage.Succeeded, 1) { Result = result });
                results.Add(result);
            }
            AfterDownloads?.Invoke();
            return Task.FromResult<IReadOnlyList<TrackDownloadResult>>(results);
        }

        public Task<IReadOnlyList<TrackResolution>> ResolveTracksAsync(IReadOnlyList<ExportedTrack> tracks, IProgress<TrackResolutionProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManualChoiceResult> ChooseVideoAsync(ExportedTrack track, string urlOrVideoId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public TrackResolution Skip(ExportedTrack track) => throw new NotSupportedException();

        public bool Forget(ExportedTrack track) => throw new NotSupportedException();

        public TrackResolution? GetCachedResolution(ExportedTrack track) => null;

        public IReadOnlyList<TrackResolution?> FindInBackground(IReadOnlyList<ExportedTrack> tracks) => throw new NotSupportedException();

        public event EventHandler<TrackResolutionProgress>? LookupProgress { add { } remove { } }

        public Task<TrackResolution> RetryAsync(ExportedTrack track, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
