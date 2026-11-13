using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mp3PlayerSync.Core;

/// <summary>
/// User choices remembered between runs.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The MP3 player chosen last time.</summary>
    public PlayerChoice? Player { get; set; }

    /// <summary>The folder last chosen with "Other folder", where the folder dialog opens next time.</summary>
    public string? LastPickedFolder { get; set; }

    public string? LastSelectedKey { get; set; }

    public LibraryFilter Filter { get; set; } = LibraryFilter.PlaylistsByYou;

    /// <summary>What the last export of each playlist showed.</summary>
    public Dictionary<string, ExportSummary> Exports { get; init; } = [];

    public static AppSettings Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        File.Move(temporary, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
