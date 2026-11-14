using Microsoft.Extensions.Time.Testing;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

/// <summary>
/// Deterministic: the pacer's decisions are taken synchronously on a fake clock, and the check after a cool-down
/// completes synchronously, so nothing depends on real time or thread scheduling.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "TUnit0018:Test methods should not assign instance data",
    Justification = "Tests set what the check returns for themselves; TUnit makes a new instance for each test, so it doesn't carry over.")]
public class YouTubeTrafficTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private FakeTimeProvider clock = null!;
    private string directory = null!;
    private string statePath = null!;
    private int checks;
    private Func<Task<bool>> check = null!;
    private readonly List<YouTubeTraffic> created = [];

    [Before(Test)]
    public void SetUp()
    {
        clock = new FakeTimeProvider(Start);
        directory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", Path.GetRandomFileName());
        statePath = Path.Join(directory, "YouTubeTraffic.json");
        checks = 0;
        check = () => Task.FromResult(true);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var traffic in created) traffic.Dispose();
        created.Clear();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private YouTubeTraffic Create(TimeProvider? time = null)
    {
        var traffic = new YouTubeTraffic(statePath, _ => { checks++; return check(); }, time ?? clock);
        created.Add(traffic);
        return traffic;
    }

    private static YtDlpException Throttled() => new("ERROR: [youtube] QAo_Ycocl1E: HTTP Error 429: Too Many Requests", 1);

    private static YtDlpException Forbidden() => new("ERROR: unable to download video data: HTTP Error 403: Forbidden", 1);

    private static void Take(YouTubeTraffic traffic, int requests = 1) =>
        traffic.TryTakeTurn(requests).ShouldBe(new YouTubeTraffic.Turn(true, false, TimeSpan.Zero));

    private static TimeSpan WaitBefore(YouTubeTraffic traffic, int requests = 1)
    {
        var turn = traffic.TryTakeTurn(requests);
        turn.ShouldSatisfyAllConditions(
            t => t.Granted.ShouldBeFalse(),
            t => t.NeedsCheck.ShouldBeFalse());
        return turn.Wait;
    }

    private static void AssertNeedsCheck(YouTubeTraffic traffic) =>
        traffic.TryTakeTurn(1).ShouldBe(new YouTubeTraffic.Turn(false, true, TimeSpan.Zero));

    private static void Trip(YouTubeTraffic traffic) => traffic.NotePushBack(Throttled()).ShouldBeTrue();

    private double MinutesLeft(YouTubeTraffic traffic, TimeProvider? time = null) =>
        traffic.BusyUntil is { } until ? (until - (time ?? clock).GetUtcNow()).TotalMinutes : 0;

    /// <summary>Lets the cool-down end and YouTube be checked, as the next request would.</summary>
    private async Task EndCooldownAsync(YouTubeTraffic traffic)
    {
        clock.Advance(traffic.BusyUntil.ShouldNotBeNull() - clock.GetUtcNow());
        AssertNeedsCheck(traffic);
        await traffic.CheckAsync();
    }

    [Test]
    public void Paces_to_40_requests_a_minute()
    {
        var traffic = Create();
        for (var i = 0; i < 40; i++) Take(traffic);

        WaitBefore(traffic).ShouldBe(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(59));
        WaitBefore(traffic).ShouldBe(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Take(traffic);
    }

    [Test]
    public void Stays_within_250_requests_in_10_minutes_and_1000_in_an_hour()
    {
        var traffic = Create();
        var sent = new List<DateTimeOffset>();
        for (var i = 0; i < 30; i++)
        {
            while (traffic.TryTakeTurn(40) is { Granted: false } turn) clock.Advance(turn.Wait);
            sent.Add(clock.GetUtcNow());
        }

        sent.ShouldSatisfyAllConditions(
            s => s.ShouldAllBe(at => RequestsWithin(s, at, TimeSpan.FromMinutes(1)) <= 40),
            s => s.ShouldAllBe(at => RequestsWithin(s, at, TimeSpan.FromMinutes(10)) <= 250),
            s => s.ShouldAllBe(at => RequestsWithin(s, at, TimeSpan.FromHours(1)) <= 1000));
        (sent[^1] - sent[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(1), customMessage: "1,200 requests take over an hour.");
    }

    /// <summary>How many requests were sent in the window up to and including <paramref name="at"/>, 40 at a time.</summary>
    private static int RequestsWithin(List<DateTimeOffset> sent, DateTimeOffset at, TimeSpan window) =>
        sent.Count(s => s > at - window && s <= at) * 40;

    [Test]
    public async Task Throttling_stops_all_requests_until_the_cool_down_and_a_clean_check()
    {
        var traffic = Create();
        var busyChanges = 0;
        traffic.BusyChanged += (_, _) => busyChanges++;

        Trip(traffic);
        traffic.BusyText.ShouldBe("YouTube is busy; continuing in 60 min");
        WaitBefore(traffic).ShouldBe(TimeSpan.FromMinutes(60));

        clock.Advance(TimeSpan.FromMinutes(59.5));
        traffic.BusyText.ShouldBe("YouTube is busy; continuing in 1 min");
        WaitBefore(traffic).ShouldBe(TimeSpan.FromMinutes(0.5));
        checks.ShouldBe(0);

        clock.Advance(TimeSpan.FromMinutes(0.5));
        busyChanges.ShouldBe(2, customMessage: "Raised when the cool-down starts and ends.");
        traffic.BusyText.ShouldBeNull();
        AssertNeedsCheck(traffic);

        await traffic.CheckAsync();
        checks.ShouldBe(1);
        Take(traffic);
    }

    [Test]
    public async Task Requests_stay_stopped_while_YouTube_is_still_throttling_after_the_cool_down()
    {
        check = () => throw Throttled();
        var traffic = Create();
        Trip(traffic);

        foreach (var minutes in (int[])[60, 120, 240, 240])
        {
            MinutesLeft(traffic).ShouldBe(minutes);
            await EndCooldownAsync(traffic);
        }

        MinutesLeft(traffic).ShouldBe(240);
        check = () => Task.FromResult(true);
        await EndCooldownAsync(traffic);
        Take(traffic);
        checks.ShouldBe(5);
    }

    [Test]
    public async Task Cool_downs_start_short_again_after_a_calm_day()
    {
        var traffic = Create();
        Trip(traffic);
        await EndCooldownAsync(traffic);
        Take(traffic);

        Trip(traffic);
        MinutesLeft(traffic).ShouldBe(120, customMessage: "Soon after the last one, it's longer.");
        await EndCooldownAsync(traffic);
        Take(traffic);

        clock.Advance(TimeSpan.FromHours(24));
        Trip(traffic);
        MinutesLeft(traffic).ShouldBe(60);
    }

    [Test]
    public async Task Requests_are_held_to_half_the_limits_for_an_hour_after_a_cool_down()
    {
        var traffic = Create();
        Trip(traffic);
        await EndCooldownAsync(traffic);

        // The check's 4 count towards the 20 a minute.
        for (var i = 0; i < 16; i++) Take(traffic);
        WaitBefore(traffic).ShouldBe(TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 40; i++) Take(traffic);
    }

    [Test]
    public async Task A_check_that_cant_tell_is_tried_again_a_minute_later()
    {
        check = () => Task.FromResult(false);
        var traffic = Create();
        Trip(traffic);
        await EndCooldownAsync(traffic);

        traffic.BusyText.ShouldBeNull();
        WaitBefore(traffic).ShouldBe(TimeSpan.FromMinutes(1));

        check = () => Task.FromResult(true);
        clock.Advance(TimeSpan.FromMinutes(1));
        AssertNeedsCheck(traffic);
        await traffic.CheckAsync();
        Take(traffic);
        checks.ShouldBe(2);
    }

    [Test]
    public async Task A_lone_403_is_passed_on_but_three_in_5_minutes_stop_requests()
    {
        var traffic = Create();
        await Should.ThrowAsync<YtDlpException>(() => traffic.SendAsync(1, _ => throw Forbidden(), CancellationToken.None));
        traffic.BusyUntil.ShouldBeNull();

        clock.Advance(TimeSpan.FromMinutes(4));
        traffic.NotePushBack(Forbidden()).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(2)); // The first has dropped out of the last 5 minutes.
        traffic.NotePushBack(Forbidden()).ShouldBeFalse();
        traffic.BusyUntil.ShouldBeNull();

        traffic.NotePushBack(Forbidden()).ShouldBeTrue();
        MinutesLeft(traffic).ShouldBe(60);
    }

    [Test]
    public void Other_failures_are_passed_on_without_stopping_requests()
    {
        var traffic = Create();
        traffic.NotePushBack(new YtDlpException("ERROR: [youtube] abc: Video unavailable", 1)).ShouldBeFalse();
        traffic.NotePushBack(new IOException()).ShouldBeFalse();
        Take(traffic);
    }

    [Test]
    public async Task A_cool_down_survives_a_restart_and_nothing_is_sent_until_it_ends()
    {
        var before = Create();
        Trip(before);
        before.Dispose();
        clock.Advance(TimeSpan.FromMinutes(10));

        var after = Create();
        after.BusyText.ShouldBe("YouTube is busy; continuing in 50 min");
        WaitBefore(after).ShouldBe(TimeSpan.FromMinutes(50));

        await EndCooldownAsync(after);
        Take(after);
        checks.ShouldBe(1, customMessage: "YouTube is checked before anything else is sent.");
    }

    [Test]
    public void Recent_requests_count_after_a_restart()
    {
        var before = Create();
        for (var i = 0; i < 40; i++) Take(before);
        before.Dispose();

        var after = Create();
        WaitBefore(after).ShouldBe(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        Take(after);
    }

    [Test]
    public async Task Longer_cool_downs_survive_a_restart()
    {
        var before = Create();
        Trip(before);
        await EndCooldownAsync(before);
        before.Dispose();

        var after = Create();
        Trip(after);
        MinutesLeft(after).ShouldBe(120);
    }

    [Test]
    public void A_clock_that_went_back_doesnt_end_a_cool_down()
    {
        var before = Create();
        Trip(before);
        before.Dispose();

        var earlier = new FakeTimeProvider(Start - TimeSpan.FromDays(2));
        MinutesLeft(Create(earlier), earlier).ShouldBe(60, customMessage: "Capped at the cool-down's length.");
    }

    [Test]
    public void Requests_from_before_a_clock_went_back_still_count()
    {
        var before = Create();
        for (var i = 0; i < 40; i++) Take(before);
        before.Dispose();

        var earlier = new FakeTimeProvider(Start - TimeSpan.FromDays(2));
        var after = Create(earlier);
        WaitBefore(after).ShouldBe(TimeSpan.FromMinutes(1));
        earlier.Advance(TimeSpan.FromMinutes(1));
        Take(after);
    }

    [Test]
    public void A_saved_cool_down_too_far_ahead_is_capped()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, $$"""{ "level": 0, "busyUntil": "{{Start.AddDays(10):O}}", "needsCheck": true }""");

        Create().BusyUntil.ShouldBe(Start + TimeSpan.FromMinutes(60));
    }

    [Test]
    public void A_damaged_state_file_starts_afresh()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, "{ not json");

        var traffic = Create();
        traffic.BusyUntil.ShouldBeNull();
        Take(traffic);
    }
}
