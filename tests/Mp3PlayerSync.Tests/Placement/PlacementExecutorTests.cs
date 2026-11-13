using Mp3PlayerSync.Core.Placement;

namespace Mp3PlayerSync.Tests.Placement;

public class PlacementExecutorTests
{
    private string root = null!;
    private string downloads = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "Mp3PlayerSync.Tests", Guid.NewGuid().ToString("N"));
        root = Path.Combine(baseDirectory, "player");
        downloads = Path.Combine(baseDirectory, "downloads");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(downloads);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);

    private static PlacementTrack Track(string id, string title, int number) =>
        new(id, title, "Queen", ["Queen"], "Hot Space", 1982, number, ".m4a");

    private void WritePlayerFile(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string WriteDownload(string name, string content)
    {
        var path = Path.Combine(downloads, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string ReadPlayerFile(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

    [Test]
    public async Task Applies_moves_and_adds_new_files()
    {
        WritePlayerFile("Queen - Staying Power.m4a", "existing");
        var under = WriteDownload("a.m4a", "under pressure");
        var tracks = new[] { Track("u", "Under Pressure", 11) };
        var plan = PlacementPlanner.Plan(tracks, [new PlayerFile("Queen - Staying Power.m4a", "Queen", "Hot Space", 1, "Staying Power", "Queen")]);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, t => t.Id == "u" ? under : null);

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.Canceled, Is.False);
            Assert.That(report.Moved.Single().ActualRelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\01. Staying Power.m4a"));
            Assert.That(report.Added.Single().ActualRelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\11. Under Pressure.m4a"));
            Assert.That(report.Added.Single().TrackId, Is.EqualTo("u"));
            Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a"), Is.EqualTo("existing"));
            Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a"), Is.EqualTo("under pressure"));
            Assert.That(File.Exists(Path.Combine(root, "Queen - Staying Power.m4a")), Is.False);
            Assert.That(File.Exists(under), Is.True, "The download is the caller's to clean up.");
            Assert.That(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories), Is.Empty);
        });
    }

    [Test]
    public async Task Destinations_taken_after_planning_are_not_overwritten()
    {
        WritePlayerFile("Queen - Staying Power.m4a", "existing");
        var plan = PlacementPlanner.Plan(
            [Track("u", "Under Pressure", 11)],
            [new PlayerFile("Queen - Staying Power.m4a", "Queen", "Hot Space", 1, "Staying Power", "Queen")]);
        WritePlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a", "squatter 1");
        WritePlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a", "squatter 2");

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => WriteDownload("a.m4a", "new"));

        Assert.Multiple(() =>
        {
            Assert.That(report.Moved.Single().ActualRelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\01. Staying Power (2).m4a"));
            Assert.That(report.Added.Single().ActualRelativePath, Is.EqualTo(@"Queen\Hot Space (1982)\11. Under Pressure (2).m4a"));
            Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a"), Is.EqualTo("squatter 1"));
            Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a"), Is.EqualTo("squatter 2"));
            Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure (2).m4a"), Is.EqualTo("new"));
        });
    }

    [Test]
    public async Task Failures_are_reported_and_the_rest_still_applies()
    {
        var plan = new PlacementPlan(
            [new PlannedMove(new PlayerFile("gone.m4a", "", "", null, "", ""), "gone.m4a", @"X\gone.m4a")],
            [
                new PlannedNewFile(Track("missing", "A", 1), "A.m4a"),
                new PlannedNewFile(Track("escape", "B", 2), @"..\outside.m4a"),
                new PlannedNewFile(Track("ok", "C", 3), "C.m4a"),
            ]);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, t => t.Id == "missing" ? null : WriteDownload(t.Id, t.Id));

        Assert.That(report.Failed.Select(c => c.FromRelativePath ?? c.TrackId), Is.EqualTo(new[] { "gone.m4a", "missing", "escape" }));
        Assert.That(report.Added.Single().TrackId, Is.EqualTo("ok"));
        Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside.m4a")), Is.False);
        Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName), Is.EqualTo(new[] { "C.m4a" }));
    }

    [Test]
    public async Task Cancellation_stops_and_reports_what_already_changed()
    {
        WritePlayerFile("Queen - Staying Power.m4a", "existing");
        var plan = PlacementPlanner.Plan(
            [Track("u", "Under Pressure", 11)],
            [new PlayerFile("Queen - Staying Power.m4a", "Queen", "Hot Space", 1, "Staying Power", "Queen")]);
        using var cancellation = new CancellationTokenSource();

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ =>
        {
            cancellation.Cancel();
            return WriteDownload("a.m4a", "new");
        }, cancellation.Token);

        Assert.That(report.Canceled, Is.True);
        Assert.That(report.Moved.Count(), Is.EqualTo(1));
        Assert.That(report.Added, Is.Empty);
        Assert.That(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories), Is.Empty);
    }
}
