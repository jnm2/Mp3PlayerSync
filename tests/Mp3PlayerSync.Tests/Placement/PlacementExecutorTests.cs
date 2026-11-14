using Mp3PlayerSync.Core.Placement;

namespace Mp3PlayerSync.Tests.Placement;

public class PlacementExecutorTests
{
    private string root = null!;
    private string downloads = null!;

    [Before(Test)]
    public void SetUp()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "Mp3PlayerSync.Tests", Guid.NewGuid().ToString("N"));
        root = Path.Combine(baseDirectory, "player");
        downloads = Path.Combine(baseDirectory, "downloads");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(downloads);
    }

    [After(Test)]
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

        using (Assert.Multiple())
        {
            await Assert.That(report.Failed).IsEmpty();
            await Assert.That(report.Canceled).IsFalse();
            await Assert.That(report.Moved.Single().ActualRelativePath).IsEqualTo(@"Queen\Hot Space (1982)\01. Staying Power.m4a");
            await Assert.That(report.Added.Single().ActualRelativePath).IsEqualTo(@"Queen\Hot Space (1982)\11. Under Pressure.m4a");
            await Assert.That(report.Added.Single().TrackId).IsEqualTo("u");
            await Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a")).IsEqualTo("existing");
            await Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a")).IsEqualTo("under pressure");
            await Assert.That(File.Exists(Path.Combine(root, "Queen - Staying Power.m4a"))).IsFalse();
            await Assert.That(File.Exists(under)).IsTrue().Because("The download is the caller's to clean up.");
            await Assert.That(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            await Assert.That(report.Moved.Single().ActualRelativePath).IsEqualTo(@"Queen\Hot Space (1982)\01. Staying Power (2).m4a");
            await Assert.That(report.Added.Single().ActualRelativePath).IsEqualTo(@"Queen\Hot Space (1982)\11. Under Pressure (2).m4a");
            await Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a")).IsEqualTo("squatter 1");
            await Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a")).IsEqualTo("squatter 2");
            await Assert.That(ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure (2).m4a")).IsEqualTo("new");
        }
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

        await Assert.That(report.Failed.Select(c => c.FromRelativePath ?? c.TrackId)).IsEquivalentTo(new string?[] { "gone.m4a", "missing", "escape" }, CollectionOrdering.Matching);
        await Assert.That(report.Added.Single().TrackId).IsEqualTo("ok");
        await Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside.m4a"))).IsFalse();
        await Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName)).IsEquivalentTo(new string?[] { "C.m4a" }, CollectionOrdering.Matching);
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

        await Assert.That(report.Canceled).IsTrue();
        await Assert.That(report.Moved.Count()).IsEqualTo(1);
        await Assert.That(report.Added).IsEmpty();
        await Assert.That(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
    }
}
