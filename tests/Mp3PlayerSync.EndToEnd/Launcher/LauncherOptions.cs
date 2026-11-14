namespace Mp3PlayerSync.EndToEnd.Launcher;

internal sealed record LauncherOptions(
    IReadOnlyList<string> Only,
    string? AppPath,
    bool HiddenDesktop,
    TimeSpan Timeout,
    string ResultsDirectory,
    IReadOnlyList<string> TestArguments)
{
    public const string Usage = """
        Runs the end-to-end tests kept apart from whatever you're doing: on a hidden desktop of their own, so the app
        never appears over or takes focus from your windows.

        Usage: dotnet run --project tests\Mp3PlayerSync.EndToEnd -- [options] [-- <test platform options>]

          --only <names>           Just these runs, comma-separated: {0}
          --app <path>             The test host to run, such as a trimmed one; otherwise the one built beside this
          --desktop Hidden|Current Hidden (the default): a desktop of its own. Current: this session's desktop, where
                                   the app can cover and take focus from other windows: for CI, or for someone who
                                   asks for it on purpose. Agents never use it on the user's PC.
          --timeout-minutes <n>    How long the tests may take once they start (default 15)
          --results-directory <d>  Where the TRX report goes (default: TestResults beside this)

        Exits with 0 when every test passed, 1 when any failed or didn't run, and 2 when none failed but some were
        inconclusive (they couldn't see what they check).
        """;

    public static LauncherOptions Parse(IReadOnlyList<string> args)
    {
        var only = new List<string>();
        string? app = null;
        var hidden = true;
        var timeout = TimeSpan.FromMinutes(15);
        var results = Path.Combine(AppContext.BaseDirectory, "TestResults");
        var testArguments = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--only":
                    foreach (var name in Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        only.Add(TestCategories.All.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"There's no run called '{name}'. The runs are {string.Join(", ", TestCategories.All)}."));
                    }
                    break;
                case "--app":
                    app = Path.GetFullPath(Value());
                    if (!File.Exists(app)) throw new ArgumentException($"The test host '{app}' doesn't exist.");
                    break;
                case "--desktop":
                    hidden = Value().ToLowerInvariant() switch
                    {
                        "hidden" => true,
                        "current" => false,
                        var other => throw new ArgumentException($"--desktop is Hidden or Current, not '{other}'."),
                    };
                    break;
                case "--timeout-minutes":
                    timeout = double.TryParse(Value(), out var minutes) && minutes > 0 ? TimeSpan.FromMinutes(minutes) : throw new ArgumentException("--timeout-minutes needs a number of minutes.");
                    break;
                case "--results-directory":
                    results = Path.GetFullPath(Value());
                    break;
                case "--":
                    testArguments.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        return new(only, app, hidden, timeout, results, testArguments);
    }

    /// <summary>The test platform's filter for <see cref="Only"/>, if any.</summary>
    public string? TreeNodeFilter => Only.Count switch
    {
        0 => null,
        1 => $"/**[Category={Only[0]}]",
        _ => $"/**[{string.Join('|', Only.Select(c => $"(Category={c})"))}]",
    };
}
