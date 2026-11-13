using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Scanning;
using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Sync;

public enum StatusTone { None, Success, Caution, Critical, Neutral }

/// <summary>
/// A row on the contents page: a Spotify track, which can be selected for syncing, or a file in the playlist's
/// folder that's no longer on Spotify, which can be selected for removal.
/// </summary>
public sealed class TrackRow : INotifyPropertyChanged
{
    private bool isSelected;
    private bool userChoseSelection;
    private bool isMarkedForRemoval;
    private bool isOnPlayer;
    private bool canChange = true;
    private bool isSkipped;
    private bool isFinding;
    private TrackResolutionState? found;
    private TrackSyncState? syncState;
    private double? percent;
    private string? syncMessage;

    public TrackRow(ExportedTrack track) => Track = track;

    /// <param name="inAlbum">Whether the page is an album's, rather than a playlist's.</param>
    public TrackRow(IndexedFile playerOnlyFile, string relativePath, bool inAlbum = false)
    {
        PlayerOnlyFile = playerOnlyFile;
        PlayerOnlyRelativePath = relativePath;
        isOnPlayer = true;
        this.inAlbum = inAlbum;
    }

    private readonly bool inAlbum;

    public ExportedTrack? Track { get; }

    /// <summary>A file in the playlist's folder that isn't in the playlist on Spotify.</summary>
    public IndexedFile? PlayerOnlyFile { get; }

    /// <summary>Where the player-only file is, relative to the player root.</summary>
    public string? PlayerOnlyRelativePath { get; }

    public bool IsSpotifyTrack => Track is not null;

    public bool IsPlayerOnly => PlayerOnlyFile is not null;

    public string Number => Track?.Number.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";

    public string Title => Track?.Title ?? PlayerOnlyFile!.Tags.Title;

    public string ArtistsText => Track?.ArtistsText ?? string.Join(", ", PlayerOnlyFile!.Tags.Artists);

    public string Album => Track?.Album ?? PlayerOnlyFile!.Tags.Album;

    public string DurationText => Track?.DurationText ?? ExportedTrack.FormatDuration(PlayerOnlyFile!.Tags.Duration);

    public string AddedAtText => Track?.AddedAtText ?? "";

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (Set(ref isSelected, value)) userChoseSelection = true;
        }
    }

    /// <summary>Whether the user ticked or unticked the track themselves.</summary>
    public bool UserChoseSelection => userChoseSelection;

    /// <summary>Carries over the user's choice from an earlier copy of this row, e.g. after a refresh.</summary>
    public void KeepChoiceFrom(TrackRow earlier)
    {
        if (!earlier.userChoseSelection || IsSkipped) return;
        Set(ref isSelected, earlier.isSelected, nameof(IsSelected));
        userChoseSelection = true;
    }

    /// <summary>Selects the track by default, unless the user has already ticked or unticked it.</summary>
    public void SetDefaultSelection(bool selected)
    {
        if (!userChoseSelection) Set(ref isSelected, selected && !IsSkipped, nameof(IsSelected));
    }

    /// <summary>For a player-only file: delete it from the player when syncing.</summary>
    public bool IsMarkedForRemoval
    {
        get => isMarkedForRemoval;
        set => Set(ref isMarkedForRemoval, value);
    }

    /// <summary>After syncing, the track is on the player and no longer selected.</summary>
    public void MarkOnPlayer()
    {
        IsOnPlayer = true;
        Set(ref isSelected, false, nameof(IsSelected));
        // From now on the default follows the player again, e.g. if the file is deleted later.
        userChoseSelection = false;
    }

    /// <summary>For a song that would be copied from elsewhere on the player, the size of that file.</summary>
    public long? CopySize { get; set; }

    /// <summary>Whether the song is part of a sync that's still going.</summary>
    public bool IsSyncing => SyncState is TrackSyncState.Queued or TrackSyncState.Searching or TrackSyncState.Downloading or TrackSyncState.Saving or TrackSyncState.NeedsReview;

    public bool IsOnPlayer
    {
        get => isOnPlayer;
        set
        {
            if (!Set(ref isOnPlayer, value)) return;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(CanUndoSkip));
        }
    }

    public bool CanChange
    {
        get => canChange;
        set
        {
            if (Set(ref canChange, value))
            {
                OnPropertyChanged(nameof(CanCheck));
                OnPropertyChanged(nameof(CanUndoSkip));
            }
        }
    }

    /// <summary>The user chose not to download this track. It stays unselected until the skip is undone.</summary>
    public bool IsSkipped
    {
        get => isSkipped;
        set
        {
            if (!Set(ref isSkipped, value)) return;
            if (value) Set(ref isSelected, false, nameof(IsSelected));
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CanUndoSkip));
            OnPropertyChanged(nameof(Status));
        }
    }

    public bool CanCheck => CanChange && !IsSkipped;

    /// <summary>The song is being looked for on YouTube Music, ahead of syncing.</summary>
    public bool IsFinding
    {
        get => isFinding;
        set { if (Set(ref isFinding, value)) OnPropertyChanged(nameof(Status)); }
    }

    /// <summary>What looking for the song found, if it has been looked for.</summary>
    public TrackResolutionState? Found
    {
        get => found;
        set { if (Set(ref found, value)) OnPropertyChanged(nameof(Status)); }
    }

    /// <summary>Whether the user has to choose the song's match before it can be synced.</summary>
    public bool NeedsReview => Found is TrackResolutionState.NeedsReview or TrackResolutionState.Unavailable or TrackResolutionState.LookupFailed;

    public bool CanUndoSkip => CanChange && IsSkipped && !IsOnPlayer;

    public TrackSyncState? SyncState
    {
        get => syncState;
        set { if (Set(ref syncState, value)) OnPropertyChanged(nameof(Status)); }
    }

    public double? Percent
    {
        get => percent;
        set { if (Set(ref percent, value)) OnPropertyChanged(nameof(Status)); }
    }

    public string? SyncMessage
    {
        get => syncMessage;
        set { if (Set(ref syncMessage, value)) OnPropertyChanged(nameof(Status)); }
    }

    public string Status => SyncState switch
    {
        TrackSyncState.Queued => "Waiting",
        TrackSyncState.Searching => "Finding",
        TrackSyncState.Downloading => Percent is { } p ? $"Downloading {p:0}%" : "Downloading",
        TrackSyncState.Saving => "Saving",
        TrackSyncState.Done => "On player",
        TrackSyncState.Failed => SyncMessage ?? "Failed",
        TrackSyncState.NeedsReview => "Needs review",
        _ when IsPlayerOnly => inAlbum ? "Not in album" : "Not in playlist",
        _ when IsOnPlayer => "On player",
        _ when IsSkipped => "Skipped",
        _ when IsFinding => "Finding source…",
        _ => Found switch
        {
            TrackResolutionState.Resolved => "Source found",
            TrackResolutionState.NeedsReview => "Needs review",
            TrackResolutionState.Unavailable => "Unavailable",
            TrackResolutionState.LookupFailed => "Couldn't search",
            _ => "",
        },
    };

    /// <summary>Every status that doesn't depend on the row, so the Status column can be wide enough for any of them.</summary>
    public static IReadOnlyList<string> FixedStatuses { get; } =
        (string[])["Waiting", "Finding", "Downloading 100%", "Saving", "On player", "Failed", "Needs review", "Skipped", "Finding source…", "Source found", "Unavailable", "Couldn't search"];

    /// <summary>What the status means for the user, shown as an icon and color beside it.</summary>
    public StatusTone Tone => SyncState switch
    {
        TrackSyncState.Done => StatusTone.Success,
        TrackSyncState.Failed => StatusTone.Critical,
        TrackSyncState.NeedsReview => StatusTone.Caution,
        not null => StatusTone.None,
        // In the same order as Status.
        _ when IsPlayerOnly => StatusTone.None,
        _ when IsOnPlayer => StatusTone.Success,
        _ when IsSkipped => StatusTone.Neutral,
        _ when IsFinding => StatusTone.None,
        _ => Found switch
        {
            TrackResolutionState.NeedsReview => StatusTone.Caution,
            TrackResolutionState.Unavailable or TrackResolutionState.LookupFailed => StatusTone.Critical,
            _ => StatusTone.None,
        },
    };

    public bool IsSuccessStatus => Tone == StatusTone.Success;

    public bool IsCautionStatus => Tone == StatusTone.Caution;

    public bool IsCriticalStatus => Tone == StatusTone.Critical;

    public bool IsNeutralStatus => Tone == StatusTone.Neutral;

    // Used by XAML item containers as the accessible name.
    public override string ToString() => Track?.ToString() ?? $"{Title} — {ArtistsText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? name)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name != nameof(Status)) return;
        // The tone follows the status, which every change to the row's state raises.
        PropertyChanged?.Invoke(this, new(nameof(Tone)));
        PropertyChanged?.Invoke(this, new(nameof(IsSuccessStatus)));
        PropertyChanged?.Invoke(this, new(nameof(IsCautionStatus)));
        PropertyChanged?.Invoke(this, new(nameof(IsCriticalStatus)));
        PropertyChanged?.Invoke(this, new(nameof(IsNeutralStatus)));
    }

    private bool Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(name);
        return true;
    }
}
