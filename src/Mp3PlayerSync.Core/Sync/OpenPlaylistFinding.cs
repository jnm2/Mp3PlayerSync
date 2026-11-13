using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Core.Sync;

/// <summary>
/// Looks for the open playlist's songs in the background, and only those (SYNC-11): YouTube allows a connection only
/// so many lookups an hour, so they go to the songs the user is looking at. Opening another playlist replaces what
/// was queued; leaving a playlist drops what was queued; nothing is looked for in the background during a sync, which
/// looks for its own songs first. Lookups already under way finish, and everything found stays remembered.
/// </summary>
/// <param name="getFinder">Gets the finder, which may be created on first use.</param>
public sealed class OpenPlaylistFinding(Func<ITrackFinder> getFinder)
{
    private string? queuedSet;

    /// <summary>Whether a sync is running, which pauses background finding.</summary>
    public bool IsPausedForSync { get; private set; }

    /// <summary>
    /// Looks for the open playlist's songs that aren't known yet, in the given order, replacing anything queued
    /// before. Returns what's already known for each song (null where it's queued), or null if nothing changed:
    /// a sync is running, or the same songs are already being looked for.
    /// </summary>
    /// <param name="songs">The open playlist's songs to look for: not on the player, not skipped, not yet found.</param>
    public IReadOnlyList<TrackResolution?>? Find(IReadOnlyList<ExportedTrack> songs, Func<ExportedTrack, string> key)
    {
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(key);
        if (IsPausedForSync) return null;

        var set = string.Join("\n", songs.Select(key));
        if (set == queuedSet) return null;

        if (songs.Count == 0 && queuedSet is null)
        {
            queuedSet = set;
            return [];
        }

        queuedSet = set;
        return getFinder().FindInBackground(songs);
    }

    /// <summary>Drops the queued lookups, e.g. when the user leaves the playlist.</summary>
    public void Stop()
    {
        if (queuedSet is { Length: > 0 }) getFinder().FindInBackground([]);
        queuedSet = null;
    }

    /// <summary>Stops background finding until <see cref="ResumeAfterSync"/>.</summary>
    public void PauseForSync()
    {
        Stop();
        IsPausedForSync = true;
    }

    /// <summary>Lets <see cref="Find"/> look for songs again; call it, then <see cref="Find"/> for the open playlist.</summary>
    public void ResumeAfterSync() => IsPausedForSync = false;
}
