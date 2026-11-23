// This checks the single-file release exe after it's published. Its bundle has to carry exactly the kinds of files on
// the allow-list below, with nothing else and nothing missing (REL-2), and its version fields have to be the ones that
// build\Version.targets gives this build (REL-1). Its product name, company and copyright are checked too (TEXT-3 and
// REL-3). It reads only the bundle's list of files and the exe's version resource, not the files in the bundle, so it
// takes a fraction of a second. CI's publish job runs it like this:
//
//   dotnet run --file build\CheckReleaseExe.cs -- MP3-Player-Sync-x64.exe

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

if (args is not [var exePath])
{
    Console.Error.WriteLine(@"Usage: dotnet run --file build\CheckReleaseExe.cs -- <the published Mp3PlayerSync.exe>");
    return 2;
}

// Every file in the bundle is extracted onto the user's disk at first launch, so only files the app needs at run time
// belong here. Each entry is a path in the bundle, where * stands for any part of one folder or file name, and each one
// has to match at least one file, so that the app's own files can't go missing either.
string[] allowList =
[
    // The app's own assemblies, and what the .NET host needs to start them.
    "Mp3PlayerSync.dll",
    "Mp3PlayerSync.Core.dll",
    "Mp3PlayerSync.deps.json",
    "Mp3PlayerSync.runtimeconfig.json",
    // The app's compiled XAML and its resources. Unpackaged WinUI looks for them as resources.pri beside the exe.
    "resources.pri",
    // The window's icon, which MainWindow loads from this path.
    "Assets/AppIcon.ico",
    // The parts of .NET that trimming keeps, and the managed assemblies of the packages the app uses.
    "System.dll",
    "System.*.dll",
    "Microsoft.*.dll",
    "WinRT.Runtime.dll",
    "TagLibSharp.dll",
    // The Windows App SDK: its native DLLs, metadata and resources, and the XAML strings in each language.
    "Microsoft.*.winmd",
    "Microsoft.*.pri",
    "Microsoft.UI.Xaml/Assets/*",
    "*/Microsoft.ui.xaml.dll.mui",
    "*/Microsoft.UI.Xaml.Phone.dll.mui",
    "CoreMessagingXP.dll",
    "dcompi.dll",
    "dwmcorei.dll",
    "DwmSceneI.dll",
    "marshal.dll",
    "MRM.dll",
    "PushNotificationsLongRunningTask.ProxyStub.dll",
    "RestartAgent.exe",
    "WebView2Loader.dll",
    "WinUIEdit.dll",
    "wuceffectsi.dll",
];

var problems = new List<string>();

List<string> files;
try
{
    files = ReadBundledFiles(exePath);
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
var patterns = allowList.ToDictionary(entry => entry, entry => new Regex(
    "^" + Regex.Escape(entry).Replace(@"\*", "[^/]*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
foreach (var file in files.Where(file => !patterns.Values.Any(pattern => pattern.IsMatch(file))))
{
    problems.Add($"The exe carries {file}, which isn't on the allow-list in build\\CheckReleaseExe.cs. If the app needs it at run time, add it there. Otherwise, keep it out of the publish (REL-2).");
}
foreach (var (entry, pattern) in patterns.Where(p => !files.Any(p.Value.IsMatch)))
{
    problems.Add($"Nothing in the exe matches '{entry}' on the allow-list in build\\CheckReleaseExe.cs. If the app no longer needs it, remove it from there.");
}

var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
Console.WriteLine($"Version: {versionInfo.ProductVersion}");
Console.WriteLine($"File version: {versionInfo.FileVersion}");
if (versionInfo.ProductName != "MP3 Player Sync")
    problems.Add($"The exe's product name is '{versionInfo.ProductName}', but it should be 'MP3 Player Sync' (TEXT-3).");
if (versionInfo.FileDescription != "MP3 Player Sync")
    problems.Add($"The exe's file description is '{versionInfo.FileDescription}', but it should be 'MP3 Player Sync' (TEXT-3).");
if (versionInfo.CompanyName != "Joseph N. Musser II")
    problems.Add($"The exe's company is '{versionInfo.CompanyName}', but it should be 'Joseph N. Musser II' (REL-3).");
if (versionInfo.LegalCopyright is not { } copyright || !Regex.IsMatch(copyright, @"^Copyright © 2026(–\d{4})? Joseph N\. Musser II$"))
    problems.Add($"The exe's copyright is '{versionInfo.LegalCopyright}', but it should be 'Copyright © 2026 Joseph N. Musser II', or a range of years from 2026 (REL-3).");

var (versionPattern, expectedVersion, runNumber) = ExpectedVersion();
var version = versionInfo.ProductVersion ?? "";
if (!Regex.IsMatch(version, versionPattern))
{
    problems.Add($"The exe's version is {version}, but this build's should be {expectedVersion} (REL-1).");
}
else
{
    // The file version is numeric: the version prefix, then the run number in CI and 0 elsewhere.
    var expectedFileVersion = $"{Regex.Match(version, @"^\d+\.\d+\.\d+").Value}.{runNumber ?? "0"}";
    if (versionInfo.FileVersion != expectedFileVersion)
        problems.Add($"The exe's file version is {versionInfo.FileVersion}, but it should be {expectedFileVersion} (REL-1).");
}

if (problems.Count > 0)
{
    foreach (var problem in problems) Console.Error.WriteLine($"error: {problem}");
    return 1;
}
Console.WriteLine($"The exe's {files.Count} bundled files and its version fields are as expected.");
return 0;

// The single-file bundle's list of files, in the format of the .NET SDK's Microsoft.NET.HostModel.Bundle. The SDK
// writes the offset of the bundle's header into the exe just before a fixed signature, which is how the .NET host finds
// it too. The header and the list of files are at the end of the exe, after the files themselves.
static List<string> ReadBundledFiles(string exePath)
{
    ReadOnlySpan<byte> signature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];
    var bytes = File.ReadAllBytes(exePath);
    var signatureAt = bytes.AsSpan().IndexOf(signature);
    var headerAt = signatureAt >= 8 ? BitConverter.ToInt64(bytes, signatureAt - 8) : 0;
    if (headerAt <= 0 || headerAt >= bytes.Length)
        throw new InvalidDataException($"{exePath} isn't a single-file bundle.");

    using var reader = new BinaryReader(new MemoryStream(bytes, (int)headerAt, bytes.Length - (int)headerAt), Encoding.UTF8);
    var majorVersion = reader.ReadUInt32();
    reader.ReadUInt32(); // The minor version
    // Version 6 has been the format since .NET 6. Another one may lay the list out differently, so it needs a look.
    if (majorVersion != 6)
        throw new InvalidDataException($"{exePath} has a version {majorVersion} bundle, which this check doesn't know yet.");
    var fileCount = reader.ReadInt32();
    reader.ReadString(); // The bundle's ID
    reader.ReadBytes(4 * sizeof(long) + sizeof(ulong)); // Where the .deps.json and .runtimeconfig.json are, and the flags

    var files = new List<string>(fileCount);
    for (var i = 0; i < fileCount; i++)
    {
        reader.ReadBytes(3 * sizeof(long) + sizeof(byte)); // The file's offset, size, compressed size and type
        files.Add(reader.ReadString());
    }
    return files;
}

// What build\Version.targets makes this build's version, as a pattern, as words for the error message, and the run
// number that the file version ends with in CI. It tells the kinds of build apart by the same variables.
static (string Pattern, string Description, string? RunNumber) ExpectedVersion()
{
    var gitHubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
    var run = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER");
    var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ?? "";
    var commit = Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: >= 8 } sha ? sha[..8] : "";
    var pullRequest = Regex.Match(Environment.GetEnvironmentVariable("GITHUB_REF") ?? "", @"(?<=^refs/pull/)\d+").Value;
    const string prefix = @"\d+\.\d+\.\d+";

    if (!gitHubActions)
        return ($"^{prefix}-dev$", "<version>-dev", null);
    if (Environment.GetEnvironmentVariable("GITHUB_REF_TYPE") == "tag")
        return ($"^{Regex.Escape(refName[1..])}\\+build\\.{run}\\.commit\\.{commit}$", $"{refName[1..]}+build.{run}.commit.{commit}, from the tag", run);
    if (pullRequest.Length > 0)
        return ($"^{prefix}-{run}\\.pr\\.{pullRequest}$", $"<version>-{run}.pr.{pullRequest}", run);
    if (refName == "main")
        return ($"^{prefix}-ci\\.{run}\\+commit\\.{commit}$", $"<version>-ci.{run}+commit.{commit}", run);
    var branch = Regex.Replace(refName, "[^a-zA-Z0-9]+", "-");
    return ($"^{prefix}-{run}\\.{Regex.Escape(branch)}$", $"<version>-{run}.{branch}", run);
}
