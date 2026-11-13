using System.Text.Json;
using System.Text.Json.Serialization;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.E2EHost;

/// <summary>
/// Drives to list instead of the real ones, each backed by a folder, from <c>MP3PLAYERSYNC_TEST_DRIVES</c>: a JSON array
/// such as <c>[{"root":"E:\\","label":"MP3 PLAYER","bus":"Usb","totalBytes":16000000000,"freeBytes":9000000000,"folder":"C:\\Temp\\Player"}]</c>.
/// </summary>
internal sealed class TestDrives(TestDrive[] drives)
{
    public const string EnvironmentVariable = "MP3PLAYERSYNC_TEST_DRIVES";

    public static TestDrives? FromEnvironment() =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } json
            ? new(JsonSerializer.Deserialize(json, TestDriveJsonContext.Default.TestDriveArray) ?? [])
            : null;

    public IReadOnlyList<DriveCandidate> List() =>
        drives.Select(d => new DriveCandidate(d.Root, d.Label, DriveType.Removable, d.Bus, false, $@"\\?\Volume{{test-{d.Root.TrimEnd('\\', ':')}}}\", null, d.TotalBytes, d.FreeBytes)).ToList();

    public string FolderOf(DriveCandidate drive) =>
        drives.FirstOrDefault(d => string.Equals(d.Root, drive.RootPath, StringComparison.OrdinalIgnoreCase))?.Folder ?? drive.RootPath;
}

internal sealed record TestDrive(string Root, string Label, StorageBus Bus, long TotalBytes, long FreeBytes, string Folder);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TestDrive[]))]
internal sealed partial class TestDriveJsonContext : JsonSerializerContext;
