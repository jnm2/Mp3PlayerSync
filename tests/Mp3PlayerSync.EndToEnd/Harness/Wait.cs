using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.Exceptions;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>
/// Waits for what the app should show, polling a condition rather than sleeping a fixed time: a wait ends as soon as
/// the condition holds, and a timeout says what it waited for and what the window showed instead.
/// </summary>
public static class Wait
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly Lock ScaleGate = new();
    private static double scale = Environment.GetEnvironmentVariable("CI") == "true" ? 2 : 1;
    private static long scaleCheckedAt;

    /// <summary>
    /// How much longer than usual every wait may take: CI runners, with two cores on Arm64, are slower and busier than a
    /// developer's PC, and other runs going at the same time slow this one down too.
    /// </summary>
    public static double Scale
    {
        get
        {
            lock (ScaleGate)
            {
                if (Stopwatch.GetElapsedTime(scaleCheckedAt) >= TimeSpan.FromSeconds(5) && Environment.GetEnvironmentVariable(Launcher.Launcher.QueueVariable) is { Length: > 0 } queue)
                {
                    scaleCheckedAt = Stopwatch.GetTimestamp();
                    var running = Directory.EnumerateFiles(queue, "*.json").Count(IsRunning);
                    scale = Math.Max(Environment.GetEnvironmentVariable("CI") == "true" ? 2.0 : 1.0, Math.Min(2.0, 1 + 0.5 * (running - 1)));
                }
                return scale;
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="probe"/> finds something, and returns it. UI Automation's errors from elements that
    /// come and go while the app redraws count as not found yet.
    /// </summary>
    /// <param name="what">What's waited for, as in "Timed out after 30 s waiting for the sign-in screen".</param>
    /// <param name="showing">The app whose window's text a timeout reports.</param>
    public static async Task<T> UntilAsync<T>(Func<T?> probe, string what, TimeSpan? timeout = null, AppUnderTest? showing = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var limit = (timeout ?? TimeSpan.FromSeconds(30)) * Scale;
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;
        while (true)
        {
            try
            {
                if (probe() is { } found) return found;
                lastError = null;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or TimeoutException)
            {
                lastError = exception;
            }

            if (stopwatch.Elapsed >= limit)
            {
                var shown = showing?.TryReadAllText() is { } text ? $". The window showed: {text}" : "";
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.#} s waiting for {what}{shown}", lastError);
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds.</summary>
    public static Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null, AppUnderTest? showing = null, CancellationToken cancellationToken = default) =>
        UntilAsync(() => condition() ? (object)true : null, what, timeout, showing, cancellationToken);

    private static bool IsRunning(string queueEntry)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(queueEntry));
            return json.RootElement.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }
}
