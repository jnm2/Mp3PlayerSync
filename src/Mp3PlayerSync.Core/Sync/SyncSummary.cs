namespace Mp3PlayerSync.Core.Sync;

public static class SyncSummary
{
    /// <summary>"Adding 12 songs, removing 3 songs", leaving out what's zero; empty when nothing would change.</summary>
    public static string Describe(int adding, int removing)
    {
        var parts = new List<string>(2);
        if (adding > 0) parts.Add($"Adding {Songs(adding)}");
        if (removing > 0) parts.Add($"{(parts.Count > 0 ? "removing" : "Removing")} {Songs(removing)}");
        return string.Join(", ", parts);

        static string Songs(int count) => count == 1 ? "1 song" : $"{count} songs";
    }
}
