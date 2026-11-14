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
        PlacementTrack[] tracks = [Track("u", "Under Pressure", 11)];
        var plan = PlacementPlanner.Plan(tracks, [new PlayerFile("Queen - Staying Power.m4a", "Queen", "Hot Space", 1, "Staying Power", "Queen")]);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, t => t.Id == "u" ? under : null);

        report.ShouldSatisfyAllConditions(
            r => r.Failed.ShouldBeEmpty(),
            r => r.Canceled.ShouldBeFalse(),
            r => r.Moved.ShouldHaveSingleItem().ActualRelativePath.ShouldBe(@"Queen\Hot Space (1982)\01. Staying Power.m4a"),
            r => r.Added.ShouldHaveSingleItem().ActualRelativePath.ShouldBe(@"Queen\Hot Space (1982)\11. Under Pressure.m4a"),
            r => r.Added.ShouldHaveSingleItem().TrackId.ShouldBe("u"),
            _ => ReadPlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a").ShouldBe("existing"),
            _ => ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a").ShouldBe("under pressure"),
            _ => File.Exists(Path.Combine(root, "Queen - Staying Power.m4a")).ShouldBeFalse(),
            _ => File.Exists(under).ShouldBeTrue(customMessage: "The download is the caller's to clean up."),
            _ => Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty());
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

        report.ShouldSatisfyAllConditions(
            r => r.Moved.ShouldHaveSingleItem().ActualRelativePath.ShouldBe(@"Queen\Hot Space (1982)\01. Staying Power (2).m4a"),
            r => r.Added.ShouldHaveSingleItem().ActualRelativePath.ShouldBe(@"Queen\Hot Space (1982)\11. Under Pressure (2).m4a"),
            _ => ReadPlayerFile(@"Queen\Hot Space (1982)\01. Staying Power.m4a").ShouldBe("squatter 1"),
            _ => ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure.m4a").ShouldBe("squatter 2"),
            _ => ReadPlayerFile(@"Queen\Hot Space (1982)\11. Under Pressure (2).m4a").ShouldBe("new"));
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

        report.Failed.Select(c => c.FromRelativePath ?? c.TrackId).ShouldBe(["gone.m4a", "missing", "escape"]);
        report.Added.ShouldHaveSingleItem().TrackId.ShouldBe("ok");
        File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside.m4a")).ShouldBeFalse();
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ShouldBe(["C.m4a"]);
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

        report.Canceled.ShouldBeTrue();
        report.Moved.ShouldHaveSingleItem();
        report.Added.ShouldBeEmpty();
        Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }
}
