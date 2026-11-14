using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mp3PlayerSync.Tests;

/// <summary>
/// Checks of decisions in docs/design-decisions.md that only reading the source can catch, such as a call that must
/// never be added. Each test names its decision's ID.
/// </summary>
public partial class DesignDecisionSourceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Mp3PlayerSync.slnx"))) return folder.FullName;
        }
        throw new InvalidOperationException("Mp3PlayerSync.slnx not found above the test directory.");
    }

    private static IEnumerable<string> SourceFiles(params string[] patterns) =>
        patterns.SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), pattern, SearchOption.AllDirectories))
            .Where(path => !Regex.IsMatch(path, @"[\\/](bin|obj|Assets)[\\/]"));

    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot, path);

    /// <summary>The source's string literals, leaving out comments.</summary>
    private static IEnumerable<string> StringLiterals(string path) =>
        File.ReadLines(path)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(line => StringLiteral().Matches(line).Select(m => m.Value));

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [Test]
    public async Task DATA_1_The_app_never_calls_Spotify_itself()
    {
        var calls = SourceFiles("*.cs", "*.js")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(l => Regex.IsMatch(l.line, @"api\.spotify\.com|accounts\.spotify\.com/api|\bBearer\b", RegexOptions.IgnoreCase))
            .Select(l => $"{Relative(l.path)}:{l.index + 1}: {l.line.Trim()}");

        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task AUDIO_1_Downloads_never_convert_or_use_ffmpeg()
    {
        var conversions = SourceFiles("*.cs")
            .SelectMany(path => StringLiterals(path).Select(literal => (path, literal)))
            .Where(s => Regex.IsMatch(s.literal, @"ffmpeg|avconv|--extract-audio|^""-x""$|--audio-format|--recode-video|--remux-video|--postprocessor-args|--merge-output-format", RegexOptions.IgnoreCase))
            .Select(s => $"{Relative(s.path)}: {s.literal}");

        await Assert.That(conversions).IsEmpty();
    }

    [Test]
    public async Task TEXT_1_and_TEXT_3_The_UI_never_mentions_the_browser_or_Exportify_or_misnames_the_app()
    {
        string[] shown = ["Text", "Content", "Title", "Header", "PlaceholderText", "ToolTipService.ToolTip", "AutomationProperties.Name"];
        var mentions = SourceFiles("*.xaml")
            .SelectMany(path => XDocument.Load(path).Descendants()
                .SelectMany(element => element.Attributes())
                .Where(attribute => shown.Contains(attribute.Name.LocalName))
                .Select(attribute => (path, text: attribute.Value)))
            .Where(s => Regex.IsMatch(s.text, @"browser|exportify|Mp3PlayerSync", RegexOptions.IgnoreCase))
            .Select(s => $"{Relative(s.path)}: {s.text}");

        await Assert.That(mentions).IsEmpty();
    }

    [Test]
    public async Task TEXT_1_Messages_the_app_shows_never_mention_the_browser_or_Exportify()
    {
        // Text that can reach the UI: exception messages (shown by ShowMessage), messages and errors set on
        // objects, and the message bar itself. The page's own error text is reworded by FailureText.
        var mentions = SourceFiles("*.cs")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(l => Regex.IsMatch(l.line, @"Exception\(|ShowMessage\(|\b(Message|Error|LinkError|SyncMessage)\s*=") && !l.line.Contains("Debug.WriteLine"))
            .SelectMany(l => StringLiteral().Matches(l.line).Select(m => (l.path, l.index, literal: m.Value)))
            .Where(s => Regex.IsMatch(s.literal, @"browser|exportify", RegexOptions.IgnoreCase))
            .Select(s => $"{Relative(s.path)}:{s.index + 1}: {s.literal}");

        await Assert.That(mentions).IsEmpty();
    }

    [Test]
    public async Task TEXT_3_The_window_is_called_MP3_Player_Sync()
    {
        var window = XDocument.Load(Path.Combine(RepositoryRoot, @"src\Mp3PlayerSync\MainWindow.xaml")).Root!;

        await Assert.That((string?)window.Attribute("Title")).IsEqualTo("MP3 Player Sync");
    }

    [Test]
    public async Task WIN_1_The_window_has_a_Mica_backdrop()
    {
        var window = XDocument.Load(Path.Combine(RepositoryRoot, @"src\Mp3PlayerSync\MainWindow.xaml")).Root!;
        var backdrop = window.Elements().SingleOrDefault(e => e.Name.LocalName == "Window.SystemBackdrop")?.Elements().SingleOrDefault();

        await Assert.That(backdrop?.Name.LocalName).IsEqualTo("MicaBackdrop");
    }

    [Test]
    public async Task DEV_1_DevTools_are_only_ever_enabled_in_Debug_builds()
    {
        var enabled = new List<string>();
        foreach (var path in SourceFiles("*.cs"))
        {
            var debugOnly = new Stack<bool>();
            foreach (var (line, index) in File.ReadLines(path).Select((line, index) => (line.Trim(), index)))
            {
                if (line.StartsWith("#if", StringComparison.Ordinal)) debugOnly.Push(Regex.IsMatch(line, @"^#if\s+DEBUG\s*$"));
                else if ((line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal)) && debugOnly.Count > 0)
                {
                    debugOnly.Pop();
                    debugOnly.Push(false);
                }
                else if (line.StartsWith("#endif", StringComparison.Ordinal) && debugOnly.Count > 0) debugOnly.Pop();
                else if (Regex.IsMatch(line, @"AreDevToolsEnabled\s*=(?!=)") && !Regex.IsMatch(line, @"AreDevToolsEnabled\s*=\s*false\s*;") && !debugOnly.Contains(true))
                    enabled.Add($"{Relative(path)}:{index + 1}: {line}");
            }
        }

        await Assert.That(enabled).IsEmpty();
    }

    [Test]
    public async Task TEST_2_The_app_has_nothing_that_is_only_for_tests()
    {
        var testOnly = SourceFiles("*.cs", "*.xaml", "*.js")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line: line.Trim(), index)))
            .Where(l => !l.line.StartsWith("//", StringComparison.Ordinal) && !l.line.StartsWith("<!--", StringComparison.Ordinal))
            .Where(l => TestOnlyCode().IsMatch(l.line))
            .Select(l => $"{Relative(l.path)}:{l.index + 1}: {l.line}");

        await Assert.That(testOnly).IsEmpty();
    }

    // Fakes and test hooks, and the environment variables that the app used to read to turn them on
    [GeneratedRegex(@"\w*(Fake|Mock|Stub)\w*|\bTest[A-Z_]\w*|\w*(?<!Hit)Test(s|ing)?\b|MP3PLAYERSYNC_|\bE2E", RegexOptions.None)]
    private static partial Regex TestOnlyCode();
}
