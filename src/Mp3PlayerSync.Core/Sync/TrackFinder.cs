using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Core.Sync;

/// <summary>
/// Finds tracks to download, lets the user settle uncertain matches, and downloads them.
/// </summary>
public interface ITrackFinder
{
    /// <inheritdoc cref="YouTubeMusicDownloader.ResolveTracksAsync"/>
    Task<IReadOnlyList<TrackResolution>> ResolveTracksAsync(IReadOnlyList<ExportedTrack> tracks, IProgress<TrackResolutionProgress>? progress, CancellationToken cancellationToken);

    /// <inheritdoc cref="YouTubeMusicDownloader.ChooseVideoAsync"/>
    Task<ManualChoiceResult> ChooseVideoAsync(ExportedTrack track, string urlOrVideoId, CancellationToken cancellationToken);

    /// <inheritdoc cref="YouTubeMusicDownloader.Skip"/>
    TrackResolution Skip(ExportedTrack track);

    /// <inheritdoc cref="YouTubeMusicDownloader.Forget"/>
    bool Forget(ExportedTrack track);

    /// <inheritdoc cref="YouTubeMusicDownloader.GetCachedResolution"/>
    TrackResolution? GetCachedResolution(ExportedTrack track);

    /// <inheritdoc cref="YouTubeMusicDownloader.FindInBackground"/>
    IReadOnlyList<TrackResolution?> FindInBackground(IReadOnlyList<ExportedTrack> tracks);

    /// <inheritdoc cref="YouTubeMusicDownloader.LookupProgress"/>
    event EventHandler<TrackResolutionProgress>? LookupProgress;

    /// <inheritdoc cref="YouTubeMusicDownloader.RetryAsync"/>
    Task<TrackResolution> RetryAsync(ExportedTrack track, CancellationToken cancellationToken);
    /// <inheritdoc cref="YouTubeMusicDownloader.DownloadTracksAsync"/>
    Task<IReadOnlyList<TrackDownloadResult>> DownloadTracksAsync(IReadOnlyList<TrackDownloadRequest> requests, Action<TrackDownloadProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Finds tracks on YouTube Music.
/// </summary>
public sealed class YouTubeTrackFinder(YouTubeMusicDownloader downloader) : ITrackFinder
{
    public Task<IReadOnlyList<TrackResolution>> ResolveTracksAsync(IReadOnlyList<ExportedTrack> tracks, IProgress<TrackResolutionProgress>? progress, CancellationToken cancellationToken) =>
        downloader.ResolveTracksAsync(tracks, progress, cancellationToken);

    public Task<ManualChoiceResult> ChooseVideoAsync(ExportedTrack track, string urlOrVideoId, CancellationToken cancellationToken) =>
        downloader.ChooseVideoAsync(track, urlOrVideoId, cancellationToken);

    public TrackResolution Skip(ExportedTrack track) => downloader.Skip(track);

    public bool Forget(ExportedTrack track) => downloader.Forget(track);

    public TrackResolution? GetCachedResolution(ExportedTrack track) => downloader.GetCachedResolution(track);

    public IReadOnlyList<TrackResolution?> FindInBackground(IReadOnlyList<ExportedTrack> tracks) => downloader.FindInBackground(tracks);

    public event EventHandler<TrackResolutionProgress>? LookupProgress
    {
        add => downloader.LookupProgress += value;
        remove => downloader.LookupProgress -= value;
    }

    public Task<TrackResolution> RetryAsync(ExportedTrack track, CancellationToken cancellationToken) => downloader.RetryAsync(track, cancellationToken);

    public Task<IReadOnlyList<TrackDownloadResult>> DownloadTracksAsync(IReadOnlyList<TrackDownloadRequest> requests, Action<TrackDownloadProgress>? progress, CancellationToken cancellationToken) =>
        downloader.DownloadTracksAsync(requests, progress, cancellationToken);
}