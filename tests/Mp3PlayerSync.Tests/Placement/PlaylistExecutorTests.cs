using Mp3PlayerSync.Core.Placement;

namespace Mp3PlayerSync.Tests.Placement;

public class PlaylistExecutorTests
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

    private Mp3PlayerSync.Core.Scanning.IndexedFile Put(string relativePath, string title, string artist = "Queen")
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, title);
        return PlaylistPlannerTests.FileAt(root, relativePath, title, artist);
    }

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));

    private string[] FolderContents(string folder) =>
        Directory.GetFiles(Path.Combine(root, folder)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Test]
    public async Task Applies_deletions_swaps_copies_and_downloads()
    {
        var files = new[]
        {
            Put(@"My Mix\01. Queen - A.m4a", "A"),
            Put(@"My Mix\02. Queen - B.m4a", "B"),
            Put(@"My Mix\03. Old - Gone.m4a", "Gone", "Old"),
            Put(@"Queen - C.m4a", "C"),
        };
        var download = Path.Combine(downloads, "d.m4a");
        File.WriteAllText(download, "D");
        var plan = PlaylistPlanner.Plan("My Mix", [PlaylistPlannerTests.Song("B"), PlaylistPlannerTests.Song("A"), PlaylistPlannerTests.Song("C"), PlaylistPlannerTests.Song("D")],
            root, files, cleanupSelection: [@"My Mix\03. Old - Gone.m4a"]);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, t => t.Track.Title == "D" ? download : null);

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.Deleted.Single().FromRelativePath, Is.EqualTo(@"My Mix\03. Old - Gone.m4a"));
            Assert.That(report.Renamed.Count(), Is.EqualTo(2));
            Assert.That(report.Added.Select(c => (c.TrackId, c.FromRelativePath)), Is.EqualTo(new[] { ("3", "Queen - C.m4a"), ("4", (string?)null) }));
            Assert.That(FolderContents("My Mix"), Is.EqualTo(new[] { "01. Queen - B.m4a", "02. Queen - A.m4a", "03. Queen - C.m4a", "04. Queen - D.m4a" }));
            Assert.That(Read(@"My Mix\01. Queen - B.m4a"), Is.EqualTo("B"));
            Assert.That(Read(@"My Mix\02. Queen - A.m4a"), Is.EqualTo("A"));
            Assert.That(Read(@"My Mix\03. Queen - C.m4a"), Is.EqualTo("C"));
            Assert.That(Read("Queen - C.m4a"), Is.EqualTo("C"), "Copying leaves the original where it was.");
        });
    }

    [Test]
    public async Task Rerunning_a_finished_plan_changes_nothing()
    {
        var files = new[] { Put(@"My Mix\01. Queen - A.m4a", "A"), Put(@"My Mix\02. Queen - B.m4a", "B") };
        var tracks = new[] { PlaylistPlannerTests.Song("B"), PlaylistPlannerTests.Song("A") };
        await new PlacementExecutor(root).ExecuteAsync(PlaylistPlanner.Plan("My Mix", tracks, root, files), _ => null);

        var rescanned = new[] { PlaylistPlannerTests.FileAt(root, @"My Mix\01. Queen - B.m4a", "B"), PlaylistPlannerTests.FileAt(root, @"My Mix\02. Queen - A.m4a", "A") };
        var plan = PlaylistPlanner.Plan("My Mix", tracks, root, rescanned);

        Assert.That(plan.Tracks.Select(t => t.State), Is.All.EqualTo(PlaylistTrackState.InPlace));
        Assert.That(plan.Renames, Is.Empty);
    }

    [Test]
    public async Task Interrupted_renumbering_converges_on_the_next_run()
    {
        // As if the previous run stopped after parking A on its way from 02 to 01.
        var files = new[] { Put(@"My Mix\~mps-0a1b2c 02. Queen - A.m4a", "A"), Put(@"My Mix\01. Queen - B.m4a", "B") };
        var plan = PlaylistPlanner.Plan("My Mix", [PlaylistPlannerTests.Song("A"), PlaylistPlannerTests.Song("B")], root, files);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => null);

        Assert.That(report.Failed, Is.Empty);
        Assert.That(FolderContents("My Mix"), Is.EqualTo(new[] { "01. Queen - A.m4a", "02. Queen - B.m4a" }));
        Assert.That(Read(@"My Mix\01. Queen - A.m4a"), Is.EqualTo("A"));
    }

    [Test, Platform("Win")]
    public async Task Renumbering_renames_the_files_without_copying_them()
    {
        // WRITE-4 in docs/design-decisions.md
        var files = new[] { Put(@"My Mix\01. Queen - A.m4a", "A"), Put(@"My Mix\02. Queen - B.m4a", "B") };
        var idOfA = FileId(@"My Mix\01. Queen - A.m4a");
        var idOfB = FileId(@"My Mix\02. Queen - B.m4a");
        var plan = PlaylistPlanner.Plan("My Mix", [PlaylistPlannerTests.Song("B"), PlaylistPlannerTests.Song("A")], root, files);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => null);

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.Added, Is.Empty);
            Assert.That(FileId(@"My Mix\01. Queen - B.m4a"), Is.EqualTo(idOfB), "B is the same file, renamed");
            Assert.That(FileId(@"My Mix\02. Queen - A.m4a"), Is.EqualTo(idOfA), "A is the same file, renamed");
        });
    }

    /// <summary>The file system's ID for the file, which a rename keeps and a copy doesn't.</summary>
    private ulong FileId(string relativePath)
    {
        using var handle = File.OpenHandle(Path.Combine(root, relativePath));
        if (!GetFileInformationByHandle(handle, out var info)) throw new System.ComponentModel.Win32Exception();
        return (ulong)info.FileIndexHigh << 32 | info.FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [Test]
    public async Task Leftover_partial_copies_are_removed_but_other_files_are_kept()
    {
        Put(@"My Mix\~mps-123abc.partial", "half");
        Put(@"My Mix\~mps-notours.partial", "user");

        await new PlacementExecutor(root).ExecuteAsync(PlaylistPlanner.Plan("My Mix", [], root, []), _ => null);

        Assert.That(FolderContents("My Mix"), Is.EqualTo(new[] { "~mps-notours.partial" }));
    }

    [Test]
    public async Task Rename_never_overwrites_a_file_that_appeared_after_planning()
    {
        var files = new[] { Put(@"My Mix\02. Queen - A.m4a", "A") };
        var plan = PlaylistPlanner.Plan("My Mix", [PlaylistPlannerTests.Song("A")], root, files);
        Put(@"My Mix\01. Queen - A.m4a", "squatter");

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => null);

        Assert.That(report.Renamed.Single().ActualRelativePath, Is.EqualTo(@"My Mix\01. Queen - A (2).m4a"));
        Assert.That(Read(@"My Mix\01. Queen - A.m4a"), Is.EqualTo("squatter"));
    }

    [Test]
    public async Task Missing_download_and_vanished_copy_source_are_reported()
    {
        var files = new[] { Put(@"Queen - C.m4a", "C") };
        var plan = PlaylistPlanner.Plan("My Mix", [PlaylistPlannerTests.Song("C"), PlaylistPlannerTests.Song("D")], root, files);
        File.Delete(Path.Combine(root, "Queen - C.m4a"));

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => null);

        Assert.That(report.Failed.Select(c => c.TrackId), Is.EqualTo(new[] { "1", "2" }));
    }
}
