using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Sync;

public enum ReviewState { Open, Working, Chosen, Skipped }

/// <summary>
/// A track whose match the user has to settle before syncing: pick a candidate, paste a link, or skip it.
/// </summary>
public sealed class ReviewItem : INotifyPropertyChanged
{
    private TrackResolution resolution;
    private ReviewState state = ReviewState.Open;
    private string pastedLink = "";
    private string? linkError;

    public ReviewItem(TrackResolution resolution)
    {
        this.resolution = Original = resolution;
        Candidates = new(resolution.Candidates.Select(c => new ReviewCandidate(this, c)));
    }

    public ExportedTrack Track => resolution.Track;

    /// <summary>What resolving found, with its candidates, to go back to if the user changes their mind.</summary>
    public TrackResolution Original { get; }

    public TrackResolution Resolution
    {
        get => resolution;
        set
        {
            resolution = value;
            Candidates.Clear();
            foreach (var candidate in value.Candidates) Candidates.Add(new ReviewCandidate(this, candidate));
            State = value.State switch
            {
                TrackResolutionState.Resolved => ReviewState.Chosen,
                TrackResolutionState.Skipped => ReviewState.Skipped,
                _ => ReviewState.Open,
            };
            Changed(nameof(Reason));
            Changed(nameof(CanRetry));
            Changed(nameof(HasCandidates));
            Changed(nameof(SettledText));
            Changed(nameof(ChangeText));
            Changed(nameof(ChangeLabel));
        }
    }

    public ObservableCollection<ReviewCandidate> Candidates { get; }

    public string Title => Track.Title;

    public string Details => Join(Track.ArtistsText, Track.Album, Track.DurationText);

    public string Reason => resolution.State == TrackResolutionState.LookupFailed
        ? "Couldn't search YouTube Music. " + resolution.Reason
        : resolution.Reason ?? "";

    public bool CanRetry => resolution.State == TrackResolutionState.LookupFailed && State == ReviewState.Open;

    public bool HasCandidates => Candidates.Count > 0 && State == ReviewState.Open;

    public string SettledText => resolution.Video is { } video
        ? "Using " + Join(video.Title, string.Join(", ", video.Artists), video.Album, video.Duration is { } d ? ExportedTrack.FormatDuration(d) : null)
        : "Skipped";

    public string ChangeText => resolution.State == TrackResolutionState.Skipped ? "Undo" : "Change";

    public string ChangeLabel => resolution.State == TrackResolutionState.Skipped ? $"Undo skipping {Title}" : $"Change the match for {Title}";

    public string LinkLabel => $"Link to {Title}";

    public string UseLinkLabel => $"Use the link for {Title}";

    public string RetryLabel => $"Search again for {Title}";

    public string SkipLabel => $"Skip {Title}";

    public ReviewState State
    {
        get => state;
        set
        {
            if (state == value) return;
            state = value;
            Changed();
            Changed(nameof(IsOpen));
            Changed(nameof(IsWorking));
            Changed(nameof(IsChosen));
            Changed(nameof(IsSkipped));
            Changed(nameof(CanRetry));
            Changed(nameof(HasCandidates));
            Changed(nameof(IsSettled));
        }
    }

    public bool IsOpen => State == ReviewState.Open;

    public bool IsWorking => State == ReviewState.Working;

    public bool IsChosen => State == ReviewState.Chosen;

    public bool IsSkipped => State == ReviewState.Skipped;

    /// <summary>Whether the user has dealt with this track: chosen a video or skipped it.</summary>
    public bool IsSettled => State is ReviewState.Chosen or ReviewState.Skipped;

    public string PastedLink
    {
        get => pastedLink;
        set => Set(ref pastedLink, value);
    }

    public string? LinkError
    {
        get => linkError;
        set
        {
            if (Set(ref linkError, value)) Changed(nameof(HasLinkError));
        }
    }

    public bool HasLinkError => !string.IsNullOrEmpty(LinkError);

    // Used by XAML item containers as the accessible name.
    public override string ToString() => $"{Title} — {Track.ArtistsText}";

    internal static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    private bool Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        Changed(name);
        return true;
    }
}

/// <summary>A YouTube Music result offered for a track under review.</summary>
public sealed class ReviewCandidate(ReviewItem item, ResolutionCandidate candidate)
{
    public ReviewItem Item { get; } = item;

    public ResolutionCandidate Candidate { get; } = candidate;

    public string Title => Candidate.Video.Track ?? Candidate.Video.Title;

    public string Details => ReviewItem.Join(
        Candidate.Video.Artists.Count > 0 ? string.Join(", ", Candidate.Video.Artists) : Candidate.Video.Channel,
        Candidate.Video.Album,
        Candidate.Video.Duration is { } d ? ExportedTrack.FormatDuration(d) : null);

    public string Problem => Candidate.HasAcceptableAudio ? "" : Candidate.FormatProblem ?? "No usable audio";

    public bool CanUse => Candidate.HasAcceptableAudio;

    public Uri? Link => Uri.TryCreate(Candidate.Video.Url, UriKind.Absolute, out var uri) ? uri : null;

    public string UseLabel => string.Create(CultureInfo.CurrentCulture, $"Use {Title} for {Item.Title}");

    public string OpenLabel => string.Create(CultureInfo.CurrentCulture, $"Open {Title} on YouTube Music");

    // Used by XAML item containers as the accessible name.
    public override string ToString() => $"{Title} — {Details}";
}
