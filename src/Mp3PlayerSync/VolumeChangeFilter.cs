namespace Mp3PlayerSync;

/// <summary>
/// Picks out the volume watcher's events that are changes. On starting, the watcher reports every volume that's
/// already there as added, until it says enumeration is complete; those aren't changes.
/// </summary>
internal sealed class VolumeChangeFilter(Action changed)
{
    private volatile bool enumerated;

    public void OnAdded()
    {
        if (enumerated) changed();
    }

    /// <summary>E.g. a volume being enabled or disabled, or perhaps a card going into a reader that has a letter already.</summary>
    public void OnUpdated()
    {
        if (enumerated) changed();
    }

    /// <summary>Never part of the initial enumeration, so always a change.</summary>
    public void OnRemoved() => changed();

    public void OnEnumerationCompleted() => enumerated = true;
}
