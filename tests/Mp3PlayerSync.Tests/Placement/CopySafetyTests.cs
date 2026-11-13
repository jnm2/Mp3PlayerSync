using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Tests.Placement;

public class CopySafetyTests
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

    private static PlacementTrack Track(string id) => new(id, "Song " + id, "Queen", ["Queen"], "", null, null, ".m4a");

    private string Download(string name, string content = "audio")
    {
        var path = Path.Combine(downloads, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string[] PlayerFiles() =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal).ToArray();

    private static PlacementPlan Plan(params string[] ids) =>
        new([], ids.Select(id => new PlannedNewFile(Track(id), $"Queen - Song {id}.m4a")).ToList());

    [Test]
    public async Task Failed_copy_leaves_nothing_on_the_player_and_keeps_the_download()
    {
        var source = Download("a.m4a");
        var executor = new PlacementExecutor(root)
        {
            DeleteDownloadsAfterCopy = true,
            OpenSource = _ => new ScriptedStream(chunks: 1, afterChunks: _ => throw new IOException("The device is not ready.")),
        };

        var report = await executor.ExecuteAsync(Plan("1"), _ => source);

        Assert.That(report.Failed.Single().Error, Is.EqualTo("The device is not ready."));
        Assert.That(PlayerFiles(), Is.Empty, "The partial copy is deleted.");
        Assert.That(File.Exists(source), Is.True);
    }

    [Test]
    public async Task Failed_rename_deletes_the_partial_copy()
    {
        var plan = new PlacementPlan([], [new PlannedNewFile(Track("1"), new string('n', 300) + ".m4a")]);

        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => Download("a.m4a"));

        Assert.That(report.Failed.Count(), Is.EqualTo(1));
        Assert.That(PlayerFiles(), Is.Empty);
    }

    [Test]
    public async Task Cancel_mid_copy_rolls_it_back_and_reports_exactly_what_was_done()
    {
        File.WriteAllText(Path.Combine(root, "old.m4a"), "old");
        using var cancellation = new CancellationTokenSource();
        var sources = new Dictionary<string, string> { ["1"] = Download("1.m4a", "one"), ["2"] = Download("2.m4a", "two"), ["3"] = Download("3.m4a", "three") };
        var executor = new PlacementExecutor(root)
        {
            DeleteDownloadsAfterCopy = true,
            OpenSource = path => path == sources["2"]
                ? new ScriptedStream(chunks: 2, afterChunks: token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); })
                : File.OpenRead(path),
        };
        var plan = Plan("1", "2", "3") with { Moves = [new PlannedMove(new PlayerFile("old.m4a", "", "", null, "", ""), "old.m4a", @"Old\old.m4a")] };

        var report = await executor.ExecuteAsync(plan, t => sources[t.Id], cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(report.Canceled, Is.True);
            Assert.That(report.Changes.Select(c => (c.Kind, c.Succeeded, c.ActualRelativePath)), Is.EqualTo(new[]
            {
                (PlacementChangeKind.Moved, true, @"Old\old.m4a"),
                (PlacementChangeKind.Added, true, "Queen - Song 1.m4a"),
            }));
            Assert.That(PlayerFiles(), Is.EqualTo(new[] { @"Old\old.m4a", "Queen - Song 1.m4a" }), "No partial or later file is left.");
            Assert.That(File.Exists(sources["1"]), Is.False, "A copied download is deleted.");
            Assert.That(File.Exists(sources["2"]), Is.True, "An unfinished one is kept.");
        });
    }

    [Test]
    public async Task Copying_from_elsewhere_on_the_player_never_deletes_the_original()
    {
        File.WriteAllText(Path.Combine(root, "Queen - A.m4a"), "A");
        var files = new[] { PlaylistPlannerTests.FileAt(root, "Queen - A.m4a", "A") };
        var plan = PlaylistPlanner.Plan("Mix", [PlaylistPlannerTests.Song("A")], root, files);

        var report = await new PlacementExecutor(root) { DeleteDownloadsAfterCopy = true }.ExecuteAsync(plan, _ => null);

        Assert.That(report.Added.Single().ActualRelativePath, Is.EqualTo(@"Mix\01. Queen - A.m4a"));
        Assert.That(File.Exists(Path.Combine(root, "Queen - A.m4a")), Is.True);
    }

    [Test]
    public async Task Leftover_partial_copies_anywhere_on_the_player_are_removed_first()
    {
        Directory.CreateDirectory(Path.Combine(root, @"Artist\Album"));
        File.WriteAllText(Path.Combine(root, @"Artist\Album\~mps-0f0f0f.partial"), "half");
        File.WriteAllText(Path.Combine(root, "~mps-abcdef.partial"), "half");
        File.WriteAllText(Path.Combine(root, @"Artist\Album\~mps-0f0f0f 01. Kept.m4a"), "parked");

        await new PlacementExecutor(root).ExecuteAsync(Plan(), _ => null);

        Assert.That(PlayerFiles(), Is.EqualTo(new[] { @"Artist\Album\~mps-0f0f0f 01. Kept.m4a" }));
    }

    [Test]
    public async Task Syncing_files_that_are_already_right_writes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "Mix"));
        File.WriteAllText(Path.Combine(root, @"Mix\01. Queen - A.m4a"), "A");
        File.WriteAllText(Path.Combine(root, @"Mix\02. Queen - B.m4a"), "B");
        File.WriteAllText(Path.Combine(root, @"Mix\03. Old - Gone.m4a"), "Gone");
        var files = new[]
        {
            PlaylistPlannerTests.FileAt(root, @"Mix\01. Queen - A.m4a", "A"),
            PlaylistPlannerTests.FileAt(root, @"Mix\02. Queen - B.m4a", "B"),
            PlaylistPlannerTests.FileAt(root, @"Mix\03. Old - Gone.m4a", "Gone", "Old"),
        };
        var folder = PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me");
        PlaylistFolders.Claim(root, folder);
        var before = Snapshot();

        var plan = PlaylistPlanner.Plan(PlaylistFolders.Assign(root, "https://open.spotify.com/playlist/a", "Mix", "me"), [PlaylistPlannerTests.Song("A"), PlaylistPlannerTests.Song("B")], root, files);
        PlaylistFolders.Claim(root, folder);
        var report = await new PlacementExecutor(root).ExecuteAsync(plan, _ => null);
        PlaylistFolders.FinishRename(root, folder);
        await new PlacementExecutor(root).ExecuteAsync(new PlacementPlan([], []), _ => null);

        Assert.That(report.Changes, Is.Empty);
        Assert.That(Snapshot(), Is.EqualTo(before));
    }

    // Every entry with its timestamps and attributes; a folder's write time changes whenever an entry in it is
    // created, renamed or deleted.
    private string[] Snapshot()
    {
        var root = new DirectoryInfo(this.root);
        return root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(root)
            .Select(e => $"{e.FullName}|{e.LastWriteTimeUtc.Ticks}|{e.CreationTimeUtc.Ticks}|{e.Attributes}|{(e as FileInfo)?.Length}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Test]
    public void Staging_removes_folders_left_by_a_crashed_sync_but_not_one_in_use()
    {
        var work = Path.Combine(downloads, "work");
        Directory.CreateDirectory(Path.Combine(work, "crashed"));
        File.WriteAllText(Path.Combine(work, "crashed", "0.m4a"), "half");
        using var running = StagingDirectory.Create(work);
        File.WriteAllText(Path.Combine(running.Path, "1.m4a"), "busy");

        using var next = StagingDirectory.Create(work);

        Assert.That(Directory.Exists(Path.Combine(work, "crashed")), Is.False);
        Assert.That(File.Exists(Path.Combine(running.Path, "1.m4a")), Is.True);

        next.Dispose();
        Assert.That(Directory.Exists(next.Path), Is.False);
    }

    /// <summary>Returns <paramref name="chunks"/> chunks of data, then runs <paramref name="afterChunks"/> on the next read.</summary>
    private sealed class ScriptedStream(int chunks, Action<CancellationToken> afterChunks) : Stream
    {
        private int reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (reads++ < chunks)
            {
                buffer.Span[..16].Fill((byte)'x');
                return ValueTask.FromResult(16);
            }

            afterChunks(cancellationToken);
            return ValueTask.FromResult(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
