using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// Paces every request the app makes to YouTube (lookups, downloads and cover art) to well under the rate that gets
/// an IP address blocked, and stops them all for a while when YouTube pushes back, so that the user can keep using
/// YouTube. Share one instance between everything that talks to YouTube. What it knows is saved in the data folder,
/// so a block is still respected after the app restarts or the PC reboots.
/// </summary>
/// <remarks>
/// <para>
/// A probe got this PC blocked at about 3,600 requests an hour (download 403s, then 429s and bot checks on watch
/// pages that lasted 36 to 51 minutes; searches kept working). The limits leave plenty of room below that. Requests
/// are yt-dlp page and API requests, estimated per call (see <see cref="SearchRequests"/> and the other costs).
/// </para>
/// <para>
/// Times are wall-clock UTC, which survive a reboot, unlike tick counts. A clock that jumps back never ends a
/// cool-down early: what's left of one is capped at its full length instead.
/// </para>
/// </remarks>
public sealed class YouTubeTraffic : IDisposable
{
    /// <summary>The most requests in any minute.</summary>
    public const int MaxRequestsPerMinute = 40;

    /// <summary>The most requests in any 10 minutes.</summary>
    public const int MaxRequestsPerTenMinutes = 250;

    /// <summary>The most requests in any hour. This is the one that matters most.</summary>
    public const int MaxRequestsPerHour = 1000;

    /// <summary>Estimated requests for one flat YouTube Music search: the page, then one search API page.</summary>
    public const int SearchRequests = 2;

    /// <summary>Estimated requests for extracting one video: its watch page, player API responses and player script.</summary>
    public const int ExtractRequests = 4;

    /// <summary>Estimated requests for downloading one audio format from an extraction's info JSON.</summary>
    public const int DownloadRequests = 1;

    /// <summary>Requests for one cover art image.</summary>
    public const int CoverArtRequests = 1;

    /// <summary>
    /// How long all YouTube requests stop after YouTube pushes back: the first time, then each time it pushes back
    /// again within <see cref="CalmBeforeReset"/> of the last cool-down ending.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Cooldowns =
        [TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(120), TimeSpan.FromHours(4)];

    /// <summary>How long after the last cool-down ended before the next one starts from the shortest again.</summary>
    public static readonly TimeSpan CalmBeforeReset = TimeSpan.FromHours(24);

    /// <summary>How long after a cool-down the limits are halved.</summary>
    public static readonly TimeSpan ReducedPeriod = TimeSpan.FromHours(1);

    /// <summary>How many 403s that a retry didn't fix, within <see cref="ForbiddenPeriod"/>, count as YouTube pushing back.</summary>
    public const int ForbiddenToTrip = 3;

    public static readonly TimeSpan ForbiddenPeriod = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait before checking again when the check after a cool-down couldn't tell (e.g. offline).</summary>
    public static readonly TimeSpan RecheckDelay = TimeSpan.FromMinutes(1);

    private static readonly (TimeSpan Period, int Max)[] Limits =
    [
        (TimeSpan.FromMinutes(1), MaxRequestsPerMinute),
        (TimeSpan.FromMinutes(10), MaxRequestsPerTenMinutes),
        (TimeSpan.FromHours(1), MaxRequestsPerHour),
    ];

    private static readonly TimeSpan History = TimeSpan.FromHours(1);

    private readonly TimeProvider time;
    private readonly string? statePath;
    private readonly Func<CancellationToken, Task<bool>> checkAsync;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private readonly YouTubeTrafficState state;
    private DateTimeOffset? recheckAt;
    private Task? checking;
    private ITimer? busyTimer;

    /// <param name="statePath">The file to keep what's known in, e.g. <c>YouTubeTraffic.json</c> in the data folder; null for none.</param>
    /// <param name="checkAsync">
    /// Checks whether YouTube is taking requests again after a cool-down, by extracting one well-known video (not a
    /// search: searches kept working during a block). Returns whether it went cleanly; throws when YouTube is still
    /// pushing back. Its requests aren't paced.
    /// </param>
    public YouTubeTraffic(string? statePath, Func<CancellationToken, Task<bool>> checkAsync, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(checkAsync);

        this.time = time ?? TimeProvider.System;
        this.statePath = statePath;
        this.checkAsync = checkAsync;

        state = Load(statePath, this.time.GetUtcNow());
        if (state.BusyUntil is { } until && until > this.time.GetUtcNow())
            busyTimer = this.time.CreateTimer(_ => RaiseBusyChanged(), null, until - this.time.GetUtcNow(), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Raised, on a background thread, when YouTube pushes back and requests stop, and when the cool-down ends.
    /// </summary>
    public event EventHandler? BusyChanged;

    /// <summary>When the cool-down ends, while YouTube requests are stopped.</summary>
    public DateTimeOffset? BusyUntil
    {
        get { lock (gate) return BusyUntilLocked(time.GetUtcNow()); }
    }

    /// <summary>"YouTube is busy; continuing in N min" during a cool-down, otherwise null.</summary>
    public string? BusyText
    {
        get
        {
            lock (gate)
            {
                var now = time.GetUtcNow();
                return BusyUntilLocked(now) is { } until ? DescribeBusy(until - now) : null;
            }
        }
    }

    public static string DescribeBusy(TimeSpan left) =>
        $"YouTube is busy; continuing in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    /// <summary>
    /// Sends something to YouTube that makes about <paramref name="requests"/> requests, once the limits allow and no
    /// cool-down is on. If YouTube pushes back, all requests stop for a cool-down and this one is sent again after it,
    /// so callers don't see the push-back. A lone 403 is passed on (it's usually just this video), but
    /// <see cref="ForbiddenToTrip"/> of them close together start a cool-down too.
    /// </summary>
    public async Task<T> SendAsync<T>(int requests, Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        while (true)
        {
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
                await WaitForTurnAsync(requests, waiting.Token).ConfigureAwait(false);
            try
            {
                return await send(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && NotePushBack(ex))
            {
            }
        }
    }

    /// <inheritdoc cref="SendAsync{T}"/>
    public Task SendAsync(int requests, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        return SendAsync(requests, async ct => { await send(ct).ConfigureAwait(false); return true; }, cancellationToken);
    }

    internal enum PushBack { None, Throttled, Forbidden }

    /// <summary>
    /// How YouTube refused a request, if it did: throttled (429, bot check, "try again later"), or forbidden (403).
    /// </summary>
    internal static PushBack Classify(Exception exception) => exception switch
    {
        YtDlpException { IsThrottled: true } => PushBack.Throttled,
        YtDlpException ytDlp when ytDlp.Message.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) => PushBack.Forbidden,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => PushBack.Throttled,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => PushBack.Forbidden,
        _ => PushBack.None,
    };

    /// <summary>Notes a push-back; returns whether it started a cool-down (or one was already on).</summary>
    internal bool NotePushBack(Exception exception)
    {
        var kind = Classify(exception);
        if (kind == PushBack.None) return false;

        bool tripped;
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (kind == PushBack.Forbidden)
            {
                Prune(now);
                state.Forbidden.Add(now);
                if (state.Forbidden.Count < ForbiddenToTrip && BusyUntilLocked(now) is null)
                {
                    Save();
                    return false;
                }
            }

            tripped = TripLocked(now);
        }

        if (tripped) RaiseBusyChanged();
        return true;
    }

    /// <returns>Whether a new cool-down started (rather than one already being on).</returns>
    private bool TripLocked(DateTimeOffset now)
    {
        if (BusyUntilLocked(now) is not null) return false; // Requests that were already on their way.

        state.Level = state.Level < 0 || state.LastBusyEnded is not { } ended || now - ended >= CalmBeforeReset
            ? 0
            : Math.Min(state.Level + 1, Cooldowns.Count - 1);
        var until = now + Cooldowns[state.Level];
        state.TrippedAt = now;
        state.BusyUntil = until;
        state.LastBusyEnded = until;
        state.NeedsCheck = true;
        state.ReducedUntil = null;
        state.Forbidden.Clear();
        recheckAt = null;
        Save();

        busyTimer?.Dispose();
        busyTimer = time.CreateTimer(_ => RaiseBusyChanged(), null, Cooldowns[state.Level], Timeout.InfiniteTimeSpan);
        return true;
    }

    /// <summary>When the cool-down ends, capped at its full length from now in case the clock went back.</summary>
    private DateTimeOffset? BusyUntilLocked(DateTimeOffset now)
    {
        if (state.BusyUntil is not { } until || until <= now) return null;

        var longest = now + Cooldowns[Math.Max(0, state.Level)];
        if (until > longest)
        {
            state.BusyUntil = until = longest;
            Save();
        }
        return until;
    }

    /// <summary>Waits until no cool-down is on, YouTube has been checked after one, and the limits allow the requests; then counts them.</summary>
    private async Task WaitForTurnAsync(int requests, CancellationToken cancellationToken)
    {
        while (true)
        {
            var turn = TryTakeTurn(requests);
            if (turn.Granted) return;

            if (turn.NeedsCheck)
            {
                Task check;
                lock (gate) check = checking ??= Task.Run(CheckAsync);
                await check.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(turn.Wait, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether <see cref="TryTakeTurn"/> counted the requests, and if not, what to do first.</summary>
    /// <param name="NeedsCheck">Whether YouTube needs checking (<see cref="CheckAsync"/>) before anything is sent.</param>
    /// <param name="Wait">Otherwise, how long to wait before trying again.</param>
    internal readonly record struct Turn(bool Granted, bool NeedsCheck, TimeSpan Wait);

    /// <summary>
    /// Counts <paramref name="requests"/> as sent now if no cool-down is on, YouTube has been checked after one, and
    /// the limits allow them.
    /// </summary>
    internal Turn TryTakeTurn(int requests)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (BusyUntilLocked(now) is { } until)
                return new(false, false, until - now);

            if (state.NeedsCheck)
            {
                return recheckAt is { } at && at > now
                    ? new(false, false, at - now > RecheckDelay ? RecheckDelay : at - now)
                    : new(false, true, TimeSpan.Zero);
            }

            Prune(now);
            var ready = ReadyAt(now, requests);
            if (ready > now)
                return new(false, false, ready - now);

            state.Requests.Add(new(now, requests));
            Save();
            return new(true, false, TimeSpan.Zero);
        }
    }

    /// <summary>Checks whether YouTube takes requests again after a cool-down, and resumes them if so.</summary>
    internal async Task CheckAsync()
    {
        lock (gate)
        {
            state.Requests.Add(new(time.GetUtcNow(), ExtractRequests));
            Save();
        }

        bool? clean;
        try
        {
            clean = await checkAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Classify(ex) == PushBack.Throttled)
        {
            clean = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
        {
            clean = false;
        }

        var tripped = false;
        lock (gate)
        {
            var now = time.GetUtcNow();
            checking = null;
            if (clean is null)
            {
                tripped = TripLocked(now);
            }
            else if (clean.Value)
            {
                state.NeedsCheck = false;
                state.ReducedUntil = now + ReducedPeriod;
                Save();
            }
            else
            {
                recheckAt = now + RecheckDelay;
            }
        }

        if (tripped) RaiseBusyChanged();
    }

    /// <summary>When the requests sent recently will have dropped far enough for <paramref name="requests"/> more.</summary>
    private DateTimeOffset ReadyAt(DateTimeOffset now, int requests)
    {
        var reduced = state.ReducedUntil is { } reducedUntil && reducedUntil > now;
        if (reduced && state.ReducedUntil > now + ReducedPeriod) state.ReducedUntil = now + ReducedPeriod;

        var ready = now;
        foreach (var (period, max) in Limits)
        {
            var limit = reduced ? max / 2 : max;
            var allowed = limit - Math.Clamp(requests, 1, limit);
            var recent = state.Requests.Where(r => r.At > now - period).OrderBy(r => r.At).ToList();
            var total = recent.Sum(r => r.Count);
            foreach (var (at, count) in recent)
            {
                if (total <= allowed) break;
                total -= count;
                if (total <= allowed && at + period > ready) ready = at + period;
            }
        }

        return ready;
    }

    private void Prune(DateTimeOffset now)
    {
        ClampToNow(state, now);
        state.Requests.RemoveAll(r => r.At <= now - History);
        state.Forbidden.RemoveAll(at => at <= now - ForbiddenPeriod);
    }

    /// <summary>
    /// Requests recorded in the future (the clock went back) count as sent now, so they still count, and then expire
    /// as usual.
    /// </summary>
    private static void ClampToNow(YouTubeTrafficState state, DateTimeOffset now)
    {
        for (var i = 0; i < state.Requests.Count; i++)
            if (state.Requests[i].At > now) state.Requests[i] = state.Requests[i] with { At = now };
        for (var i = 0; i < state.Forbidden.Count; i++)
            if (state.Forbidden[i] > now) state.Forbidden[i] = now;
    }

    private static YouTubeTrafficState Load(string? path, DateTimeOffset now)
    {
        YouTubeTrafficState? loaded = null;
        if (path is not null)
        {
            try
            {
                using var stream = File.OpenRead(path);
                loaded = JsonSerializer.Deserialize(stream, YouTubeTrafficJsonContext.Default.YouTubeTrafficState);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        var state = loaded ?? new YouTubeTrafficState();
        ClampToNow(state, now);
        state.Level = state.BusyUntil is null && state.Level < 0 ? -1 : Math.Clamp(state.Level, 0, Cooldowns.Count - 1);
        if (state.BusyUntil > now + Cooldowns[Math.Max(0, state.Level)]) state.BusyUntil = now + Cooldowns[Math.Max(0, state.Level)];
        if (state.ReducedUntil > now + ReducedPeriod) state.ReducedUntil = now + ReducedPeriod;
        return state;
    }

    private void Save()
    {
        if (statePath is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            var temporary = statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, YouTubeTrafficJsonContext.Default.YouTubeTrafficState));
            File.Move(temporary, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RaiseBusyChanged()
    {
        try
        {
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing handler mustn't stop requests.
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (gate) busyTimer?.Dispose();
    }
}

internal sealed class YouTubeTrafficState
{
    /// <summary>Which of <see cref="YouTubeTraffic.Cooldowns"/> the last cool-down was; -1 if there hasn't been one.</summary>
    public int Level { get; set; } = -1;

    public DateTimeOffset? TrippedAt { get; set; }

    public DateTimeOffset? BusyUntil { get; set; }

    public DateTimeOffset? LastBusyEnded { get; set; }

    /// <summary>Whether YouTube needs checking before requests resume, after a cool-down.</summary>
    public bool NeedsCheck { get; set; }

    /// <summary>Until when the limits are halved, after a cool-down.</summary>
    public DateTimeOffset? ReducedUntil { get; set; }

    /// <summary>Requests in the last hour.</summary>
    public List<YouTubeRequestRecord> Requests { get; set; } = [];

    /// <summary>When the 403s of the last few minutes came.</summary>
    public List<DateTimeOffset> Forbidden { get; set; } = [];
}

internal sealed record YouTubeRequestRecord(DateTimeOffset At, int Count);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(YouTubeTrafficState))]
internal sealed partial class YouTubeTrafficJsonContext : JsonSerializerContext;
