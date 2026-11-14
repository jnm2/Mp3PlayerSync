using System.Text.Json;
using System.Text.Json.Serialization;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.E2EHost;

/// <summary>
/// Writes down the songs the app asks <see cref="FakeTrackFinder"/> to look for, as the app read them from
/// Exportify's export, so that a test can check what the app's own parsing got. Each batch is one line of JSON in the
/// file: an array of <see cref="ExportedTrack"/>, including every column of the CSV.
/// </summary>
internal sealed class TrackRecorder(string path)
{
    private readonly Lock gate = new();

    /// <summary>A recorder for the file named by <c>MP3PLAYERSYNC_TEST_RECORD_TRACKS</c>, if it's set.</summary>
    public static TrackRecorder? FromEnvironment() =>
        Environment.GetEnvironmentVariable("MP3PLAYERSYNC_TEST_RECORD_TRACKS") is { Length: > 0 } path ? new(path) : null;

    public void Record(IReadOnlyList<ExportedTrack> tracks)
    {
        var line = JsonSerializer.Serialize([.. tracks], TrackRecorderJsonContext.Default.ListExportedTrack);
        lock (gate) File.AppendAllLines(path, (string[])[line]);
    }
}

// Generated rather than reflection-based, because the host is published trimmed.
[JsonSerializable(typeof(List<ExportedTrack>))]
internal sealed partial class TrackRecorderJsonContext : JsonSerializerContext;
