namespace Mp3PlayerSync;

/// <summary>
/// Tells the navigations the app starts itself from the ones the page starts. A navigation is the app's only if it
/// goes where the app sent the page: one the page started just before, such as a redirect to Spotify's sign-in, can
/// start first, and mustn't take the app's place.
/// </summary>
public sealed class HostNavigations
{
    private Uri? pending;

    /// <summary>Notes that the app is navigating the page to <paramref name="url"/>.</summary>
    public void Starting(string url) => pending = new Uri(url);

    /// <summary>Whether the navigation to <paramref name="url"/> that's starting is the app's own.</summary>
    public bool IsOwn(string url)
    {
        if (pending is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri != pending) return false;
        pending = null;
        return true;
    }
}
