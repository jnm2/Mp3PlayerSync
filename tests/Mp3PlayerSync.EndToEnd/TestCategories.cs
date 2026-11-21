namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// The categories the launcher's <c>--only</c> takes, one per run of the tests. Every test class has one, so a run can
/// be chosen by name.
/// </summary>
public static class TestCategories
{
    /// <summary>Starts the test host, finds its window and Spotify's sign-in page in it, and closes it.</summary>
    public const string Smoke = nameof(Smoke);

    /// <summary>Spotify's session: tokens that run out or are refused, signing out, and the sign-in page's links.</summary>
    public const string Session = nameof(Session);

    /// <summary>Signing in and looking around: the library, its tabs, a playlist's page at every width, and saved albums.</summary>
    public const string Browsing = nameof(Browsing);

    /// <summary>Starting the app again: a second copy, what it reopens, and changing the player, by folder dialog too.</summary>
    public const string Restart = nameof(Restart);

    /// <summary>Syncing to player drives, which decide where the songs go from what's on them (PLAYER-7).</summary>
    public const string Drives = nameof(Drives);

    /// <summary>
    /// The weekly live check on the real exportify.net and Spotify (TEST-4). It's explicit, so it runs only when asked
    /// for with <c>--only Live</c>, never as part of the other runs.
    /// </summary>
    public const string Live = nameof(Live);

    public static IReadOnlyList<string> All { get; } =
        typeof(TestCategories).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();
}
