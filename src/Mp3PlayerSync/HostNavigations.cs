namespace Mp3PlayerSync;

/// <summary>
/// Tells the navigations the app starts itself from the ones the page starts.
/// </summary>
public sealed class HostNavigations
{
    private bool pending;

    /// <summary>Notes that the app is navigating the page to <paramref name="url"/>.</summary>
    public void Starting(string url) => pending = true;

    /// <summary>Whether the navigation to <paramref name="url"/> that's starting is the app's own.</summary>
    public bool IsOwn(string url)
    {
        var own = pending;
        pending = false;
        return own;
    }
}
