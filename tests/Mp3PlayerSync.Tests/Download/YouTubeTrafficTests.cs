using Microsoft.Extensions.Time.Testing;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

/// <summary>
/// Deterministic: the pacer's decisions are taken synchronously on a fake clock, and the check after a cool-down
/// completes synchronously, so nothing depends on real time or thread scheduling.
/// </summary>
public class YouTubeTrafficTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private FakeTimeProvider clock = null!;
    private string directory = null!;
    private string statePath = null!;
    private int checks;
    private Func<Task<bool>> check = null!;
    private readonly List<YouTubeTraffic> created = [];

    [SetUp]
    public void SetUp()
    {
        clock = new FakeTimeProvider(Start);
        directory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", Path.GetRandomFileName());
        statePath = Path.Join(directory, "YouTubeTraffic.json");
        checks = 0;
        check = () => Task.FromResult(true);
    }

    [TearDown]
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
        Assert.That(traffic.TryTakeTurn(requests), Is.EqualTo(new YouTubeTraffic.Turn(true, false, TimeSpan.Zero)));

    private static TimeSpan WaitBefore(YouTubeTraffic traffic, int requests = 1)
    {
        var turn = traffic.TryTakeTurn(requests);
        Assert.That((turn.Granted, turn.NeedsCheck), Is.EqualTo((false, false)));
        return turn.Wait;
    }

    private static void AssertNeedsCheck(YouTubeTraffic traffic) =>
        Assert.That(traffic.TryTakeTurn(1), Is.EqualTo(new YouTubeTraffic.Turn(false, true, TimeSpan.Zero)));

    private static void Trip(YouTubeTraffic traffic) => Assert.That(traffic.NotePushBack(Throttled()), Is.True);

    private double MinutesLeft(YouTubeTraffic traffic, TimeProvider? time = null) =>
        traffic.BusyUntil is { } until ? (until - (time ?? clock).GetUtcNow()).TotalMinutes : 0;

    /// <summary>Lets the cool-down end and YouTube be checked, as the next request would.</summary>
    private async Task EndCooldownAsync(YouTubeTraffic traffic)
    {
        clock.Advance(traffic.BusyUntil!.Value - clock.GetUtcNow());
        AssertNeedsCheck(traffic);
        await traffic.CheckAsync();
    }

    [Test]
    public void Paces_to_40_requests_a_minute()
    {
        var traffic = Create();
        for (var i = 0; i < 40; i++) Take(traffic);

        Assert.That(WaitBefore(traffic), Is.EqualTo(TimeSpan.FromMinutes(1)));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.That(WaitBefore(traffic), Is.EqualTo(TimeSpan.FromSeconds(1)));
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

        foreach (var at in sent)
        {
            Assert.That(sent.Count(s => s > at - TimeSpan.FromMinutes(1) && s <= at) * 40, Is.LessThanOrEqualTo(40));
            Assert.That(sent.Count(s => s > at - TimeSpan.FromMinutes(10) && s <= at) * 40, Is.LessThanOrEqualTo(250));
            Assert.That(sent.Count(s => s > at - TimeSpan.FromHours(1) && s <= at) * 40, Is.LessThanOrEqualTo(1000));
        }
        Assert.That(sent[^1] - sent[0], Is.GreaterThanOrEqualTo(TimeSpan.FromHours(1)), "1,200 requests take over an hour.");
    }

    [Test]
    public async Task Throttling_stops_all_requests_until_the_cool_down_and_a_clean_check()
    {
        var traffic = Create();
        var busyChanges = 0;
        traffic.BusyChanged += (_, _) => busyChanges++;

        Trip(traffic);
        Assert.That(traffic.BusyText, Is.EqualTo("YouTube is busy; continuing in 60 min"));
        Assert.That(WaitBefore(traffic), Is.EqualTo(TimeSpan.FromMinutes(60)));

        clock.Advance(TimeSpan.FromMinutes(59.5));
        Assert.That(traffic.BusyText, Is.EqualTo("YouTube is busy; continuing in 1 min"));
        Assert.That(WaitBefore(traffic), Is.EqualTo(TimeSpan.FromMinutes(0.5)));
        Assert.That(checks, Is.Zero);

        clock.Advance(TimeSpan.FromMinutes(0.5));
        Assert.That(busyChanges, Is.EqualTo(2), "Raised when the cool-down starts and ends.");
        Assert.That(traffic.BusyText, Is.Null);
        AssertNeedsCheck(traffic);

        await traffic.CheckAsync();
        Assert.That(checks, Is.EqualTo(1));
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
            Assert.That(MinutesLeft(traffic), Is.EqualTo(minutes));
            await EndCooldownAsync(traffic);
        }

        Assert.That(MinutesLeft(traffic), Is.EqualTo(240));
        check = () => Task.FromResult(true);
        await EndCooldownAsync(traffic);
        Take(traffic);
        Assert.That(checks, Is.EqualTo(5));
    }

    [Test]
    public async Task Cool_downs_start_short_again_after_a_calm_day()
    {
        var traffic = Create();
        Trip(traffic);
        await EndCooldownAsync(traffic);
        Take(traffic);

        Trip(traffic);
        Assert.That(MinutesLeft(traffic), Is.EqualTo(120), "Soon after the last one, it's longer.");
        await EndCooldownAsync(traffic);
        Take(traffic);

        clock.Advance(TimeSpan.FromHours(24));
        Trip(traffic);
        Assert.That(MinutesLeft(traffic), Is.EqualTo(60));
    }

    [Test]
    public async Task Requests_are_held_to_half_the_limits_for_an_hour_after_a_cool_down()
    {
        var traffic = Create();
        Trip(traffic);
        await EndCooldownAsync(traffic);

        // The check's 4 count towards the 20 a minute.
        for (var i = 0; i < 16; i++) Take(traffic);
        Assert.That(WaitBefore(traffic), Is.EqualTo(TimeSpan.FromMinutes(1)));

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

        Assert.That(traffic.BusyText, Is.Null);
        Assert.That(WaitBefore(traffic), Is.EqualTo(TimeSpan.FromMinutes(1)));

        check = () => Task.FromResult(true);
        clock.Advance(TimeSpan.FromMinutes(1));
        AssertNeedsCheck(traffic);
        await traffic.CheckAsync();
        Take(traffic);
        Assert.That(checks, Is.EqualTo(2));
    }

    [Test]
    public void A_lone_403_is_passed_on_but_three_in_5_minutes_stop_requests()
    {
        var traffic = Create();
        Assert.ThrowsAsync<YtDlpException>(() => traffic.SendAsync(1, _ => throw Forbidden(), CancellationToken.None));
        Assert.That(traffic.BusyUntil, Is.Null);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.That(traffic.NotePushBack(Forbidden()), Is.False);
        clock.Advance(TimeSpan.FromMinutes(2)); // The first has dropped out of the last 5 minutes.
        Assert.That(traffic.NotePushBack(Forbidden()), Is.False);
        Assert.That(traffic.BusyUntil, Is.Null);

        Assert.That(traffic.NotePushBack(Forbidden()), Is.True);
        Assert.That(MinutesLeft(traffic), Is.EqualTo(60));
    }

    [Test]
    public void Other_failures_are_passed_on_without_stopping_requests()
    {
        var traffic = Create();
        Assert.That(traffic.NotePushBack(new YtDlpException("ERROR: [youtube] abc: Video unavailable", 1)), Is.False);
        Assert.That(traffic.NotePushBack(new IOException()), Is.False);
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
        Assert.That(after.BusyText, Is.EqualTo("YouTube is busy; continuing in 50 min"));
        Assert.That(WaitBefore(after), Is.EqualTo(TimeSpan.FromMinutes(50)));

        await EndCooldownAsync(after);
        Take(after);
        Assert.That(checks, Is.EqualTo(1), "YouTube is checked before anything else is sent.");
    }

    [Test]
    public void Recent_requests_count_after_a_restart()
    {
        var before = Create();
        for (var i = 0; i < 40; i++) Take(before);
        before.Dispose();

        var after = Create();
        Assert.That(WaitBefore(after), Is.EqualTo(TimeSpan.FromMinutes(1)));
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
        Assert.That(MinutesLeft(after), Is.EqualTo(120));
    }

    [Test]
    public void A_clock_that_went_back_doesnt_end_a_cool_down()
    {
        var before = Create();
        Trip(before);
        before.Dispose();

        var earlier = new FakeTimeProvider(Start - TimeSpan.FromDays(2));
        Assert.That(MinutesLeft(Create(earlier), earlier), Is.EqualTo(60), "Capped at the cool-down's length.");
    }

    [Test]
    public void Requests_from_before_a_clock_went_back_still_count()
    {
        var before = Create();
        for (var i = 0; i < 40; i++) Take(before);
        before.Dispose();

        var earlier = new FakeTimeProvider(Start - TimeSpan.FromDays(2));
        var after = Create(earlier);
        Assert.That(WaitBefore(after), Is.EqualTo(TimeSpan.FromMinutes(1)));
        earlier.Advance(TimeSpan.FromMinutes(1));
        Take(after);
    }

    [Test]
    public void A_saved_cool_down_too_far_ahead_is_capped()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, $$"""{ "level": 0, "busyUntil": "{{Start.AddDays(10):O}}", "needsCheck": true }""");

        Assert.That(Create().BusyUntil, Is.EqualTo(Start + TimeSpan.FromMinutes(60)));
    }

    [Test]
    public void A_damaged_state_file_starts_afresh()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(statePath, "{ not json");

        var traffic = Create();
        Assert.That(traffic.BusyUntil, Is.Null);
        Take(traffic);
    }
}
