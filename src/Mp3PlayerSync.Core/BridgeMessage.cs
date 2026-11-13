using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mp3PlayerSync.Core;

public sealed record PlaylistSummary(string Key, string Name, string Owner, int? TrackCount, string? Url, string? ImageUrl = null)
{
    public string Details => TrackCount is { } count
        ? $"{count} {(count == 1 ? "track" : "tracks")} · {Owner}"
        : Owner;

    // Used by XAML item containers as the accessible name.
    public override string ToString() => Name;
}

/// <summary>
/// A message posted by ExportifyBridge.js through <c>chrome.webview.postMessage</c>.
/// </summary>
public sealed class BridgeMessage
{
    public const string StateType = "state";
    public const string PlaylistsType = "playlists";
    public const string ExportCompletedType = "exportCompleted";
    public const string ExportFailedType = "exportFailed";
    public const string LogType = "log";

    public string Type { get; init; } = "";

    // state
    public bool SignedIn { get; init; }
    public bool TokenExpired { get; init; }

    /// <summary>The token is still valid but runs out within minutes, so it's time to renew it.</summary>
    public bool RenewSoon { get; init; }
    public bool SigningIn { get; init; }
    public bool PlaylistsReady { get; init; }
    public string? Url { get; init; }

    // playlists
    public List<PlaylistSummary>? Playlists { get; init; }

    // exportCompleted / exportFailed
    public string? Key { get; init; }
    public string? FileName { get; init; }
    public string? Csv { get; init; }
    public string? Warnings { get; init; }

    // state (text of Exportify's #error element) / exportFailed
    public string? Error { get; init; }

    // exportFailed: what went wrong, one of FailureText.Codes
    public string? Code { get; init; }

    // log
    public string? Message { get; init; }

    public static BridgeMessage Parse(string json)
    {
        return JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeMessage)
            ?? throw new FormatException("Bridge message was null.");
    }
}

/// <summary>
/// A command sent to ExportifyBridge.js through <c>CoreWebView2.PostWebMessageAsJson</c>.
/// </summary>
public sealed record BridgeCommand(string Cmd, string? Key = null, string? Url = null, string? Name = null, bool? Force = null)
{
    public const string GetState = "getState";
    public const string Export = "export";
    public const string SignIn = "signIn";
    public const string CaptureUrl = "captureUrl";

    public string ToJson() => JsonSerializer.Serialize(this, BridgeJsonContext.Default.BridgeCommand);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(BridgeCommand))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;
