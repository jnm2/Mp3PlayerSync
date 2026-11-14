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

    private static async Task Take(YouTubeTraffic traffic, int requests = 1) =>
        await Assert.That(traffic.TryTakeTurn(requests)).IsEqualTo(new YouTubeTraffic.Turn(true, false, TimeSpan.Zero));

    private static async Task<TimeSpan> WaitBefore(YouTubeTraffic traffic, int requests = 1)
    {
        var turn = traffic.TryTakeTurn(requests);
        await Assert.That((turn.Granted, turn.NeedsCheck)).IsEqualTo((false, false));
        return turn.Wait;
    }

    private static async Task AssertNeedsCheck(YouTubeTraffic traffic) =>
        await Assert.That(traffic.TryTakeTurn(1)).IsEqualTo(new YouTubeTraffic.Turn(false, true, TimeSpan.Zero));

    private static async Task Trip(YouTubeTraffic traffic) => await Assert.That(traffic.NotePushBack(Throttled())).IsTrue();

    private double MinutesLeft(YouTubeTraffic traffic, TimeProvider? time = null) =>
        traffic.BusyUntil is { } until ? (until - (time ?? clock).GetUtcNow()).TotalMinutes : 0;

    /// <summary>Lets the cool-down end and YouTube be checked, as the next request would.</summary>
    private async Task EndCooldownAsync(YouTubeTraffic traffic)
    {
        clock.Advance(traffic.BusyUntil!.Value - clock.GetUtcNow());
        await AssertNeedsCheck(traffic);
        await traffic.CheckAsync();
    }

    [Test]
    public async Task Paces_to_40_requests_a_minute()
    {
        var traffic = Create();
        for (var i = 0; i < 40; i++) await Take(traffic);

        await Assert.That(await WaitBefore(traffic)).IsEqualTo(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(59));
        await Assert.That(await WaitBefore(traffic)).IsEqualTo(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Take(traffic);
    }

    [Test]
    public async Task Stays_within_250_requests_in_10_minutes_and_1000_in_an_hour()
    {
        var traffic = Create();
        var sent = new List<DateTimeOffset>();
        for (var i = 0; i < 30; i++)
        {
            while (traffic.TryTakeTurn(40) is { Granted: false } turn) clock.Advance(turn.Wait);
            sent.Add(clock.GetUtcNow());
        }

        foreach (var at in sent)
        {
            await Assert.That(sent.Count(s => s > at - TimeSpan.FromMinutes(1) && s <= at) * 40).IsLessThanOrEqualTo(40);
            await Assert.That(sent.Count(s => s > at - TimeSpan.FromMinutes(10) && s <= at) * 40).IsLessThanOrEqualTo(250);
            await Assert.That(sent.Count(s => s > at - TimeSpan.FromHours(1) && s <= at) * 40).IsLessThanOrEqualTo(1000);
        }
        await Assert.That(sent[^1] - sent[0]).IsGreaterThanOrEqualTo(TimeSpan.FromHours(1)).Because("1,200 requests take over an hour.");
    }

    [Test]
    public async Task Throttling_stops_all_requests_until_the_cool_down_and_a_clean_check()
    {
        var traffic = Create();
        var busyChanges = 0;
        traffic.BusyChanged += (_, _) => busyChanges++;

        await Trip(traffic);
        await Assert.That(traffic.BusyText).IsEqualTo("YouTube is busy; continuing in 60 min");
        await Assert.That(await WaitBefore(traffic)).IsEqualTo(TimeSpan.FromMinutes(60));

        clock.Advance(TimeSpan.FromMinutes(59.5));
        await Assert.That(traffic.BusyText).IsEqualTo("YouTube is busy; continuing in 1 min");
        await Assert.That(await WaitBefore(traffic)).IsEqualTo(TimeSpan.FromMinutes(0.5));
        await Assert.That(checks).IsZero();

        clock.Advance(TimeSpan.FromMinutes(0.5));
        await Assert.That(busyChanges).IsEqualTo(2).Because("Raised when the cool-down starts and ends.");
        await Assert.That(traffic.BusyText).IsNull();
        await AssertNeedsCheck(traffic);

        await traffic.CheckAsync();
        await Assert.That(checks).IsEqualTo(1);
        await Take(traffic);
    }

    [Test]
    public async Task Requests_stay_stopped_while_YouTube_is_still_throttling_after_the_cool_down()
    {
        check = () => throw Throttled();
        var traffic = Create();
        await Trip(traffic);

        foreach (var minutes in (int[])[60, 120, 240, 240])
        {
            await Assert.That(MinutesLeft(traffic)).IsEqualTo(minutes);
            await EndCooldownAsync(traffic);
        }

        await Assert.That(MinutesLeft(traffic)).IsEqualTo(240);
        check = () => Task.FromResult(true);
        await EndCooldownAsync(traffic);
        await Take(traffic);
        await Assert.That(checks).IsEqualTo(5);
    }

    [Test]
    public async Task Cool_downs_start_short_again_after_a_calm_day()
    {
        var traffic = Create();
        await Trip(traffic);
        await EndCooldownAsync(traffic);
        await Take(traffic);

        await Trip(traffic);
        await Assert.That(MinutesLeft(traffic)).IsEqualTo(120).Because("Soon after the last one, it's longer.");
        await EndCooldownAsync(traffic);
        await Take(traffic);

        clock.Advance(TimeSpan.FromHours(24));
        await Trip(traffic);
        await Assert.That(MinutesLeft(traffic)).IsEqualTo(60);
    }

    [Test]
    public async Task Requests_are_held_to_half_the_limits_for_an_hour_after_a_cool_down()
    {
        var traffic = Create();
        await Trip(traffic);
        await EndCooldownAsync(traffic);

        // The check's 4 count towards the 20 a minute.
        for (var i = 0; i < 16; i++) await Take(traffic);
        await Assert.That(await WaitBefore(traffic)).IsEqualTo(TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < 40; i++) await Take(traffic);
    }

    [Test]
    public async Task A_check_that_cant_tell_is_tried_again_a_minute_later()
    {
        check = () => Task.FromResult(false);
        var traffic = Create();
        await Trip(traffic);
        await EndCooldownAsync(traffic);

        await Assert.That(traffic.BusyText).IsNull();
        await Assert.That(await WaitBefore(traffic)).IsEqualTo(TimeSpan.FromMinutes(1));

        check = () => Task.FromResult(true);
        clock.Advance(TimeSpan.FromMinutes(1));
        await AssertNeedsCheck(traffic);
        await traffic.CheckAsync();
        await Take(traffic);
        await Assert.That(checks).IsEqualTo(2);
    }

    [Test]
    public async Task A_lone_403_is_passed_on_but_three_in_5_minutes_stop_requests()
    {
        var traffic = Create();
        await Assert.ThrowsAsync<YtDlpException>(() => traffic.SendAsync(1, _ => throw Forbidden(), CancellationToken.None));
        await Assert.That(traffic.BusyUntil).IsNull();

        clock.Advance(TimeSpan.FromMinutes(4));
        await Assert.That(traffic.NotePushBack(Forbidden())).IsFalse();
        clock.Advance(TimeSpan.FromMinutes(2)); // The first has dropped out of the last 5 minutes.
        await Assert.That(traffic.NotePushBack(Forbidden())).IsFalse();
        await Assert.That(traffic.BusyUntil).IsNull();

        await Assert.That(traffic.NotePushBack(Forbidden())).IsTrue();
        await Assert.That(MinutesLeft(traffic)).IsEqualTo(60);
    }

    [Test]
    public async Task Other_failures_are_passed_on_without_stopping_requests()
    {
        var traffic = Create();
        await Assert.That(traffic.NotePushBack(new YtDlpException("ERROR: [youtube] abc: Video unavailable", 1))).IsFalse();
        await Assert.That(traffic.NotePushBack(new IOException())).IsFalse();
        await Take(traffic);
    }

    [Test]
    public async Task A_cool_down_survives_a_restart_and_nothing_is_sent_until_it_ends()
    {
        var before = Create();
        await Trip(before);
        before.Dispose();
        clock.Advance(TimeSpan.FromMinutes(10));

        var after = Create();
        await Assert.That(after.BusyText).IsEqualTo("YouTube is busy; continuing in 50 min");
        await Assert.That(await WaitBefore(after)).IsEqualTo(TimeSpan.FromMinutes(50));

        await EndCooldownAsync(after);
        await Take(after);
        await Assert.That(checks).IsEqualTo(1).Because("YouTube is checked before anything else is sent.");
    }

    [Test]
    public async Task Recent_requests_count_after_a_restart()
    {
        var before = Create();
        for (var i = 0; i < 40; i++) await Take(before);
        before.Dispose();

        var after = Create();
        await Assert.That(await WaitBefore(after)).IsEqualTo(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Take(after);
    }

    [Test]
    public async Task Longer_cool_downs_survive_a_restart()
    {
        var before = Create();
        await Trip(before);
        await EndCooldownAsync(before);
        before.Dispose();

        var after = Create();
        await Trip(after);
        await Assert.That(MinutesLeft(after)).IsEqualTo(120);
    }

    [Test]
    public async Task A_clock_that_went_back_doesnt_end_a_cool_down()
    {
        var before = Create();
        await Trip(before);
        before.Dispose();

        var earlier = new FakeTimeProvider(Start - TimeSpan.FromDays(2));
        await Assert.That(MinutesLeft(Create(earlier), earlier)).IsEqualTo(60).Because("Capped at the cool-down's length.");
    }

    [Test]
    public async Task Requests_from_before_a_clock_went_back_still_count()
    {
        var before = Create();
        for (var i = 0; i < 40; i++) await Take(before);
        before.Dispose();

        var earlier = new FakeTimeProvider(Start - TimeSpan.FromDays(2));
        var after = Create(earlier);
        await Assert.That(await WaitBefore(after)).IsEqualTo(TimeSpan.FromMinutes(1));
        earlier.Advance(TimeSpan.FromMinutes(1));
        await Take(after);
    }

    [Test]
    public async Task A_saved_cool_down_too_far_ahead_is_capped()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, $$"""{ "level": 0, "busyUntil": "{{Start.AddDays(10):O}}", "needsCheck": true }""");

        await Assert.That(Create().BusyUntil).IsEqualTo(Start + TimeSpan.FromMinutes(60));
    }

    [Test]
    public async Task A_damaged_state_file_starts_afresh()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, "{ not json");

        var traffic = Create();
        await Assert.That(traffic.BusyUntil).IsNull();
        await Take(traffic);
    }
}
