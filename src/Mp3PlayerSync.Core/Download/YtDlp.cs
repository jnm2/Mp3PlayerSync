using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mp3PlayerSync.Core.Download;

public enum YouTubeMusicSearchSection
{
    /// <summary>Official audio tracks ("Songs" in YouTube Music search results).</summary>
    Songs,

    /// <summary>Music videos and other uploads ("Videos" in YouTube Music search results).</summary>
    Videos,
}

public sealed class YtDlpException(string message, int exitCode) : Exception(message)
{
    // Checked first: YouTube's rate limit reads "Video unavailable. This content isn't available, try again later."
    private static readonly string[] TransientSigns =
    [
        "try again later", "rate-limit", "rate limit", "429", "Too Many Requests", "not a bot", "timed out", "urlopen error",
        "getaddrinfo", "Connection", "Network", "HTTP Error 5", "Temporary failure", "SSL", "Read timed out",
    ];

    /// <summary>
    /// YouTube refusing this connection for making too many requests. Seen in testing as 429 warnings on watch pages
    /// that escalate to "Sign in to confirm you're not a bot" on every extraction for 35+ minutes.
    /// </summary>
    private static readonly string[] ThrottleSigns =
    [
        "HTTP Error 429", "Too Many Requests", "not a bot", "try again later", "rate-limit", "rate limit",
    ];

    private static readonly string[] GoneSigns =
    [
        "Video unavailable", "Private video", "This video has been removed", "This video is no longer available",
        "account associated with this video has been terminated", "in your country", "This video is not available",
        "confirm your age",
    ];

    public int ExitCode { get; } = exitCode;

    /// <summary>
    /// Whether yt-dlp reported that the video itself can't be had (removed, private, region-locked, or
    /// age-restricted), as opposed to a failure that may go away (offline, rate limit, bot check).
    /// </summary>
    public bool IsVideoGone => IsVideoGoneMessage(Message);

    /// <summary>
    /// Whether YouTube is refusing requests from this connection (429, bot check, "try again later"). All YouTube
    /// traffic should stop for a while: retrying makes the block last longer.
    /// </summary>
    public bool IsThrottled => IsThrottledMessage(Message);

    public static bool IsVideoGoneMessage(string message) =>
        !TransientSigns.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase))
        && GoneSigns.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static bool IsThrottledMessage(string message) =>
        ThrottleSigns.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Runs yt-dlp. ffmpeg is never used: downloads are single AAC formats, fixups are disabled, and the DASH m4a
/// container is rewritten by <see cref="M4aRemuxer"/> afterwards.
/// </summary>
public sealed class YtDlp
{
    private const string ProgressPrefix = "[mp3playersync-progress] ";

    private readonly string executablePath;
    private readonly string? denoPath;
    private readonly IReadOnlyList<string> extraArguments;

    /// <param name="denoPath">
    /// The Deno executable yt-dlp uses to solve YouTube's JavaScript challenges. Without a JavaScript runtime, yt-dlp
    /// warns that YouTube support is deprecated and fewer formats may be available. If null, yt-dlp looks for Deno on
    /// the PATH.
    /// </param>
    /// <param name="extraArguments">
    /// Additional yt-dlp arguments for every invocation, for example <c>--cookies-from-browser firefox</c> so that a
    /// YouTube Premium account can get format 141 (256 kbps AAC).
    /// </param>
    public YtDlp(string executablePath, IEnumerable<string>? extraArguments = null, string? denoPath = null)
    {
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("The executable path must be fully qualified.", nameof(executablePath));

        if (denoPath is not null && !Path.IsPathFullyQualified(denoPath))
            throw new ArgumentException("The Deno path must be fully qualified.", nameof(denoPath));

        this.executablePath = executablePath;
        this.denoPath = denoPath;
        this.extraArguments = extraArguments?.ToList() ?? [];
    }

    /// <summary>
    /// Extracts one video and returns yt-dlp's warnings and errors along with it, for checking that the tools work
    /// (e.g. that yt-dlp found its JavaScript runtime).
    /// </summary>
    public async Task<(YouTubeVideoInfo? Video, IReadOnlyList<string> Messages)> ExtractWithMessagesAsync(string videoUrl, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var result = await RunAsync(["--dump-json", "--no-playlist", "--", videoUrl], line => { lock (lines) lines.Add(line); }, cancellationToken).ConfigureAwait(false);
        var messages = result.ErrorLines.Where(l => l.StartsWith("WARNING:", StringComparison.Ordinal) || l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
        return (YouTubeVideoInfo.ParseLines(lines).FirstOrDefault(), messages);
    }

    public static string GetSearchUrl(string searchText, YouTubeMusicSearchSection section)
    {
        return "https://music.youtube.com/search?q=" + Uri.EscapeDataString(searchText)
            + section switch
            {
                YouTubeMusicSearchSection.Songs => "#songs",
                YouTubeMusicSearchSection.Videos => "#videos",
                _ => throw new ArgumentOutOfRangeException(nameof(section)),
            };
    }

    /// <summary>
    /// Searches YouTube Music. By default, returns flat results, which only have an ID and title (about 1.7 s). With
    /// <paramref name="fullyExtract"/>, also extracts full metadata for each result (about 1.3 s more per result).
    /// </summary>
    public async Task<IReadOnlyList<YouTubeVideoInfo>> SearchYouTubeMusicAsync(
        string searchText,
        YouTubeMusicSearchSection section,
        int maxResults,
        CancellationToken cancellationToken,
        bool fullyExtract = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);

        if (fullyExtract)
            return await DumpJsonAsync(["--dump-json", "--ignore-errors", "--playlist-items", $"1:{maxResults}", "--", GetSearchUrl(searchText, section)], cancellationToken).ConfigureAwait(false);

        // yt-dlp doesn't report YouTube Music's explicit badge, but the search responses it saves with --write-pages
        // have it. This costs no extra requests.
        var pagesDirectory = Path.Join(Path.GetTempPath(), "mps-" + Path.GetRandomFileName());
        Directory.CreateDirectory(pagesDirectory);
        try
        {
            var videos = await DumpJsonAsync(
                ["--dump-json", "--flat-playlist", "--write-pages", "--playlist-items", $"1:{maxResults}", "--", GetSearchUrl(searchText, section)],
                cancellationToken,
                workingDirectory: pagesDirectory).ConfigureAwait(false);

            var badges = new Dictionary<string, bool>();
            foreach (var page in Directory.EnumerateFiles(pagesDirectory, "*.dump"))
            {
                foreach (var (videoId, isExplicit) in YouTubeMusicSearchPage.ReadExplicitBadges(await File.ReadAllTextAsync(page, cancellationToken).ConfigureAwait(false)))
                    badges.TryAdd(videoId, isExplicit);
            }

            return videos.Select(v => badges.TryGetValue(v.Id, out var isExplicit) ? v with { IsExplicit = isExplicit } : v).ToList();
        }
        finally
        {
            try { Directory.Delete(pagesDirectory, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Extracts full metadata (artists, album, duration, formats) for videos. Videos that are unavailable are left
    /// out of the results.
    /// </summary>
    public async Task<IReadOnlyList<YouTubeVideoInfo>> ExtractVideosAsync(IEnumerable<string> videoUrls, CancellationToken cancellationToken)
    {
        var urls = videoUrls.ToList();
        if (urls.Count == 0) return [];

        return await DumpJsonAsync(["--dump-json", "--ignore-errors", "--no-playlist", "--", .. urls], cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<YouTubeVideoInfo>> DumpJsonAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null)
    {
        var lines = new List<string>();
        var result = await RunAsync(
            arguments,
            onOutputLine: line => { lock (lines) lines.Add(line); },
            cancellationToken,
            workingDirectory).ConfigureAwait(false);

        var videos = YouTubeVideoInfo.ParseLines(lines);

        // YouTube's first sign of throttling is often a warning while yt-dlp still exits 0 with degraded metadata.
        // Stop on it rather than act on (and cache) that data.
        if (result.ThrottleLine is { } throttle)
            throw new YtDlpException(throttle, result.ExitCode);

        // With --ignore-errors, an unavailable video doesn't prevent returning the others.
        if (videos.Count == 0 && result.ExitCode != 0)
            throw new YtDlpException(result.GetErrorMessage(), result.ExitCode);

        return videos;
    }

    /// <summary>
    /// Downloads one format of a video to <paramref name="outputPath"/> as is. When <paramref name="video"/> was
    /// fully extracted, its info JSON is reused instead of extracting the video again; if that fails (e.g. because
    /// the stream URLs have expired), the video is extracted again.
    /// </summary>
    public async Task DownloadFormatAsync(
        YouTubeVideoInfo video,
        string formatSelector,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (video.IsFullyExtracted && video.InfoJson is { } infoJson)
        {
            var infoJsonPath = outputPath + ".info.json";
            try
            {
                await File.WriteAllTextAsync(infoJsonPath, infoJson, cancellationToken).ConfigureAwait(false);
                await DownloadFormatAsync(["--load-info-json", infoJsonPath], formatSelector, outputPath, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (YtDlpException ex) when (!ex.IsThrottled)
            {
                // Usually expired stream URLs; extract again. When throttled, retrying would only prolong the block.
            }
            finally
            {
                try { File.Delete(infoJsonPath); }
                catch (IOException) { }
            }
        }

        await DownloadFormatAsync(["--", video.Url], formatSelector, outputPath, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one format of a video to <paramref name="outputPath"/> as is.
    /// </summary>
    public Task DownloadFormatAsync(
        string videoUrl,
        string formatSelector,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return DownloadFormatAsync(["--", videoUrl], formatSelector, outputPath, progress, cancellationToken);
    }

    private async Task DownloadFormatAsync(
        string[] sourceArguments,
        string formatSelector,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException("The output path must be fully qualified.", nameof(outputPath));

        var result = await RunAsync(
            [
                "--format", formatSelector,
                // '%' starts an output template field.
                "--output", outputPath.Replace("%", "%%", StringComparison.Ordinal),
                "--no-playlist",
                "--no-part",
                "--force-overwrites",
                "--no-mtime",
                "--fixup", "never",
                "--newline",
                "--progress-template", $"download:{ProgressPrefix}%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
                .. sourceArguments,
            ],
            onOutputLine: line =>
            {
                if (progress is not null && TryParseProgressLine(line, out var fraction))
                    progress.Report(fraction);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ThrottleLine is { } throttle)
            throw new YtDlpException(throttle, result.ExitCode);

        if (result.ExitCode != 0)
            throw new YtDlpException(result.GetErrorMessage(), result.ExitCode);

        if (!File.Exists(outputPath))
            throw new YtDlpException("yt-dlp finished without writing the file.", result.ExitCode);
    }

    /// <summary>
    /// Parses a line printed by the progress template used by <see cref="DownloadFormatAsync"/>.
    /// </summary>
    public static bool TryParseProgressLine(string line, out double fraction)
    {
        fraction = 0;
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) return false;

        var parts = line[ProgressPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !TryParse(parts[0], out var downloaded)) return false;

        var total = TryParse(parts[1], out var exact) ? exact : TryParse(parts[2], out var estimate) ? estimate : 0;
        if (total <= 0) return false;

        fraction = Math.Clamp(downloaded / total, 0, 1);
        return true;

        static bool TryParse(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private sealed record RunResult(int ExitCode, IReadOnlyList<string> ErrorLines)
    {
        /// <summary>The first warning or error that says YouTube is throttling this connection, if any.</summary>
        public string? ThrottleLine => ErrorLines.FirstOrDefault(l =>
            (l.StartsWith("ERROR:", StringComparison.Ordinal) || l.StartsWith("WARNING:", StringComparison.Ordinal))
            && YtDlpException.IsThrottledMessage(l));

        public string GetErrorMessage()
        {
            var errors = ErrorLines.Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
            var lines = errors.Count > 0 ? errors : ErrorLines.TakeLast(5).ToList();
            return lines.Count > 0
                ? string.Join(Environment.NewLine, lines)
                : $"yt-dlp exited with code {ExitCode}.";
        }
    }

    private async Task<RunResult> RunAsync(IEnumerable<string> arguments, Action<string> onOutputLine, CancellationToken cancellationToken, string? workingDirectory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo =
            {
                FileName = executablePath,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? "",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                Environment =
                {
                    ["PYTHONUTF8"] = "1",
                    ["PYTHONIOENCODING"] = "utf-8",
                },
            },
        };

        var startArguments = process.StartInfo.ArgumentList;
        string[] baseArguments =
        [
            "--ignore-config", "--color", "never", "--encoding", "utf-8",
            // HLS formats are never downloaded (they'd need ffmpeg), and fetching their manifest takes about 0.25 s.
            // Given before the extra arguments so that callers can override it.
            "--extractor-args", "youtube:skip=hls",
        ];
        foreach (var argument in baseArguments) startArguments.Add(argument);
        if (denoPath is not null)
        {
            startArguments.Add("--js-runtimes");
            startArguments.Add("deno:" + denoPath);
        }
        foreach (var argument in extraArguments) startArguments.Add(argument);
        foreach (var argument in arguments) startArguments.Add(argument);

        var errorLines = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errorLines) errorLines.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (cancellationToken.Register(() =>
        {
            // yt-dlp.exe starts a child yt-dlp.exe process which does the actual work.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }))
        {
            // Waits for the redirected streams to be drained, too.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (errorLines) return new RunResult(process.ExitCode, errorLines.ToList());
    }
}
