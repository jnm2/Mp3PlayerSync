using System.Diagnostics;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Tests.Scanning;

public class PlayerLibraryTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "Mp3PlayerSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        // Recursive delete removes junctions without following them.
        Directory.Delete(root, recursive: true);
    }

    [TestCase("song.mp3", true)]
    [TestCase("SONG.FLAC", true)]
    [TestCase("song.m4a", true)]
    [TestCase("song.opus", true)]
    [TestCase("song.wma", true)]
    [TestCase("._song.mp3", false)]
    [TestCase("cover.jpg", false)]
    [TestCase("playlist.m3u", false)]
    [TestCase("song", false)]
    public void IsAudioFile_checks_the_extension(string name, bool expected)
    {
        Assert.That(AudioFileScanner.IsAudioFile(name), Is.EqualTo(expected));
    }

    [TestCase("System Volume Information", FileAttributes.Directory, true)]
    [TestCase("$RECYCLE.BIN", FileAttributes.Directory, true)]
    [TestCase(".Trashes", FileAttributes.Directory, true)]
    [TestCase(".Trash-1000", FileAttributes.Directory, true)]
    [TestCase("LOST.DIR", FileAttributes.Directory, true)]
    [TestCase("FOUND.000", FileAttributes.Directory, true)]
    [TestCase("FOUND.Music", FileAttributes.Directory, false)]
    [TestCase("Hidden system", FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System, true)]
    [TestCase("Just hidden", FileAttributes.Directory | FileAttributes.Hidden, false)]
    [TestCase("Link", FileAttributes.Directory | FileAttributes.ReparsePoint, true)]
    [TestCase("Music", FileAttributes.Directory, false)]
    public void ShouldSkipDirectory_skips_system_folders_and_links(string name, FileAttributes attributes, bool expected)
    {
        Assert.That(AudioFileScanner.ShouldSkipDirectory(name, attributes), Is.EqualTo(expected));
    }

    [Test]
    public void Reparse_points_that_are_not_links_are_scanned()
    {
        Assert.That(AudioFileScanner.ShouldSkipDirectory("OneDrive folder", FileAttributes.Directory | FileAttributes.ReparsePoint, () => null), Is.False);
    }

    [Test]
    public void EnumerateAudioFiles_finds_files_at_any_depth_and_skips_system_folders_and_junctions()
    {
        Touch("a.mp3");
        Touch(@"Music\Artist\Album\Disc 1\Deep\b.flac");
        Touch(@"Music\cover.jpg");
        Touch(@"Music\._b.mp3");
        Touch(@"System Volume Information\c.mp3");
        Touch(@"$RECYCLE.BIN\S-1-5\d.mp3");
        CreateJunction(Path.Combine(root, @"Music\Loop"), root);

        var found = AudioFileScanner.EnumerateAudioFiles(root).Select(f => Path.GetRelativePath(root, f.FullName)).ToArray();

        Assert.That(found, Is.EqualTo(new[] { "a.mp3", @"Music\Artist\Album\Disc 1\Deep\b.flac" }));
    }

    [Test]
    public void EnumerateAudioFiles_throws_when_the_root_is_missing()
    {
        Assert.That(() => AudioFileScanner.EnumerateAudioFiles(Path.Combine(root, "missing")).ToList(), Throws.TypeOf<DirectoryNotFoundException>());
    }

    [Test]
    public async Task Scans_tags_of_files_at_any_depth()
    {
        var path = WriteMp3(@"Queen\Hot Space\11 Under Pressure.mp3", seconds: 10, tag =>
        {
            tag.Title = "Under Pressure";
            tag.Performers = ["Queen", "David Bowie"];
            tag.Album = "Hot Space";
            tag.AlbumArtists = ["Queen"];
            tag.Track = 11;
            tag.Disc = 1;
            tag.Year = 1982;
            tag.ISRC = "GBUM71029605";
        });
        Touch(@"Unknown\01 - Untagged Song.mp3");

        using var library = new PlayerLibrary(root, new PlayerLibraryOptions { WatchForChanges = false });
        var progress = new List<PlayerScanProgress>();
        library.ProgressChanged += (_, p) => { lock (progress) progress.Add(p); };
        library.Start();
        await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = library.GetSnapshot();
        Assert.That(snapshot.IsScanComplete);
        Assert.That(snapshot.Files, Has.Count.EqualTo(2));

        var tagged = snapshot.Files.Single(f => f.Path == path);
        Assert.Multiple(() =>
        {
            Assert.That(tagged.HasTags);
            Assert.That(tagged.Tags.Title, Is.EqualTo("Under Pressure"));
            Assert.That(tagged.Tags.Artists, Is.EqualTo(new[] { "Queen", "David Bowie" }));
            Assert.That(tagged.Tags.Album, Is.EqualTo("Hot Space"));
            Assert.That(tagged.Tags.AlbumArtists, Is.EqualTo(new[] { "Queen" }));
            Assert.That(tagged.Tags.TrackNumber, Is.EqualTo(11));
            Assert.That(tagged.Tags.DiscNumber, Is.EqualTo(1));
            Assert.That(tagged.Tags.Year, Is.EqualTo(1982));
            Assert.That(tagged.Tags.Isrc, Is.EqualTo("GBUM71029605"));
            Assert.That(tagged.Tags.Duration!.Value.TotalSeconds, Is.EqualTo(10).Within(0.5));
        });

        var untagged = snapshot.Files.Single(f => f.Path != path);
        Assert.That(untagged.HasTags, Is.False);
        Assert.That(untagged.Tags.Title, Is.EqualTo("Untagged Song"));

        var match = snapshot.Match(new ExportedTrack(1, "", "Under Pressure", ["Queen", "David Bowie"], "Hot Space", TimeSpan.FromSeconds(11), "", null, "", null, "", ""));
        Assert.That(match.Kind, Is.EqualTo(TrackMatchKind.ArtistAndTitle));
        Assert.That(snapshot.FindAlbum("Queen", "Hot Space").Single().Path, Is.EqualTo(path));

        lock (progress)
        {
            Assert.That(progress.Last().Phase, Is.EqualTo(PlayerScanPhase.Complete));
            Assert.That(progress.Last().FilesRead, Is.EqualTo(2));
        }
    }

    [Test]
    public void Fails_when_the_folder_is_missing()
    {
        using var library = new PlayerLibrary(Path.Combine(root, "missing"));
        library.Start();

        Assert.That(async () => await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30)), Throws.TypeOf<DirectoryNotFoundException>());
        Assert.That(library.Progress.Phase, Is.EqualTo(PlayerScanPhase.Failed));
    }

    [Test]
    public void Disposing_cancels_the_scan()
    {
        var library = new PlayerLibrary(root);
        library.Dispose();

        Assert.That(library.ScanCompletion.IsCanceled);
        Assert.That(library.Progress.Phase, Is.EqualTo(PlayerScanPhase.Canceled));
        Assert.That(() => library.Start(), Throws.TypeOf<ObjectDisposedException>());
    }

    [TestCase("folder", PlayerScanPhase.Complete)]
    [TestCase("missing", PlayerScanPhase.Failed)]
    [TestCase("disposed", PlayerScanPhase.Canceled)]
    public async Task The_final_phase_is_set_before_ScanCompletion_completes(string scenario, PlayerScanPhase expected)
    {
        using var library = new PlayerLibrary(scenario == "missing" ? Path.Combine(root, "missing") : root, new PlayerLibraryOptions { WatchForChanges = false });
        bool? completedAtFinalEvent = null;
        library.ProgressChanged += (_, p) => { if (p.IsFinished) completedAtFinalEvent ??= library.ScanCompletion.IsCompleted; };

        if (scenario == "disposed") library.Dispose();
        else library.Start();
        try { await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception e) when (e is DirectoryNotFoundException or OperationCanceledException) { }

        Assert.That(library.Progress.Phase, Is.EqualTo(expected));
        Assert.That(completedAtFinalEvent, Is.False, "ScanCompletion was already complete when the final phase was set");
    }

    [Test]
    public async Task Picks_up_files_added_renamed_and_deleted_after_the_scan()
    {
        using var library = new PlayerLibrary(root, new PlayerLibraryOptions { ChangeDebounce = TimeSpan.FromMilliseconds(100) });
        library.Start();
        await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(library.GetSnapshot().Files, Is.Empty);

        var path = WriteMp3(@"New\Song.mp3", seconds: 5, tag => { tag.Title = "Song"; tag.Performers = ["Artist"]; });
        await WaitUntil(() => library.GetSnapshot().Files.Any(f => f.Path == path && f.Tags.Title == "Song"));

        var renamedFolder = Path.Combine(root, "Renamed");
        Directory.Move(Path.Combine(root, "New"), renamedFolder);
        var renamed = Path.Combine(renamedFolder, "Song.mp3");
        await WaitUntil(() => library.GetSnapshot().Files.Select(f => f.Path).SequenceEqual([renamed]));

        System.IO.File.Delete(renamed);
        await WaitUntil(() => library.GetSnapshot().Files.Count == 0);
    }

    [Test]
    public async Task The_scan_raises_all_its_events_before_ScanCompletion_completes()
    {
        using var library = new PlayerLibrary(root, new PlayerLibraryOptions { WatchForChanges = false });
        // Widens the window in which a late event could reach a handler subscribed after awaiting the scan.
        library.ProgressChanged += (_, p) => { if (p.Phase == PlayerScanPhase.Complete) Thread.Sleep(300); };
        library.Start();
        await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30));

        var lateEvents = 0;
        library.IndexChanged += (_, _) => Interlocked.Increment(ref lateEvents);
        await Task.Delay(600);

        Assert.That(library.IsScanComplete);
        Assert.That(library.Progress.Phase, Is.EqualTo(PlayerScanPhase.Complete));
        Assert.That(lateEvents, Is.Zero);
    }

    [Test]
    public async Task Notify_methods_update_the_index_immediately()
    {
        using var library = new PlayerLibrary(root, new PlayerLibraryOptions { WatchForChanges = false });
        library.Start();
        await library.ScanCompletion.WaitAsync(TimeSpan.FromSeconds(30));

        var changes = 0;
        library.IndexChanged += (_, _) => Interlocked.Increment(ref changes);

        var path = WriteMp3(@"Artist\Song.mp3", seconds: 5, tag => tag.Title = "Song");
        library.NotifyFileWritten(path);
        Assert.That(library.GetSnapshot().Files.Single().Tags.Title, Is.EqualTo("Song"));

        var moved = Path.Combine(root, "Other", "Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        System.IO.File.Move(path, moved);
        library.NotifyMoved(path, moved);
        Assert.That(library.GetSnapshot().Files.Single().Path, Is.EqualTo(moved));

        System.IO.File.Delete(moved);
        library.NotifyDeleted(moved);
        Assert.That(library.GetSnapshot().Files, Is.Empty);
        Assert.That(changes, Is.EqualTo(3));
    }

    [Test]
    public void Reads_the_mp4_advisory_written_for_explicit_tracks()
    {
        var path = Path.Combine(root, "explicit.m4a");
        System.IO.File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Tagging", "Assets", "silence.m4a"), path);
        Mp3PlayerSync.Core.Tagging.TrackTagWriter.Write(path, new Mp3PlayerSync.Core.Tagging.TrackMetadata { Title = "Song", Explicit = true }, art: null);

        Assert.That(AudioTagReader.ReadTags(path).Explicit, Is.True);
    }

    [TestCase("1", true)]
    [TestCase("2", false)]
    [TestCase("0", null)]
    public void Reads_the_id3_advisory(string advisory, bool? expected)
    {
        var path = WriteMp3("Song.mp3", seconds: 1, tag =>
            TagLib.Id3v2.UserTextInformationFrame.Get((TagLib.Id3v2.Tag)tag, "ITUNESADVISORY", create: true).Text = [advisory]);

        Assert.That(AudioTagReader.ReadTags(path).Explicit, Is.EqualTo(expected));
    }

    [Test]
    public void Files_without_an_advisory_have_no_version()
    {
        Assert.That(AudioTagReader.ReadTags(WriteMp3("Song.mp3", seconds: 1, tag => tag.Title = "Song")).Explicit, Is.Null);
    }

    [Test]
    public void Reading_tags_leaves_the_file_untouched()
    {
        var path = WriteMp3("Song.mp3", seconds: 5, tag => tag.Title = "Song");
        var oldTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        System.IO.File.SetLastAccessTimeUtc(path, oldTime);
        System.IO.File.SetLastWriteTimeUtc(path, oldTime);
        var attributes = System.IO.File.GetAttributes(path);

        Assert.That(AudioTagReader.Read(new FileInfo(path)).Tags.Title, Is.EqualTo("Song"));

        Assert.Multiple(() =>
        {
            Assert.That(System.IO.File.GetLastAccessTimeUtc(path), Is.EqualTo(oldTime));
            Assert.That(System.IO.File.GetLastWriteTimeUtc(path), Is.EqualTo(oldTime));
            Assert.That(System.IO.File.GetAttributes(path), Is.EqualTo(attributes));
        });
    }

    [Test]
    public void Reading_tags_reads_only_a_few_kilobytes_even_with_large_cover_art()
    {
        var path = WriteMp3("Song.mp3", seconds: 120, tag =>
        {
            tag.Title = "Song";
            tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(new byte[1024 * 1024])) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
        });

        CountingStream? counter = null;
        var tags = AudioTagReader.ReadTags(path, stream => counter = new CountingStream(stream));

        Assert.That(tags.Title, Is.EqualTo("Song"));
        Assert.That(tags.Duration!.Value.TotalSeconds, Is.EqualTo(120).Within(1));
        Assert.That(new FileInfo(path).Length, Is.GreaterThan(2 * 1024 * 1024));
        TestContext.Out.WriteLine($"Read {counter!.BytesRead} of {new FileInfo(path).Length} bytes.");
        Assert.That(counter.BytesRead, Is.LessThanOrEqualTo(32 * 1024));
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); }

        private int Count(int read)
        {
            BytesRead += read;
            return read;
        }
    }

    private string Touch(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>
    /// Writes silent MPEG-1 Layer III frames (128 kbps, 44.1 kHz) and tags them with TagLib#.
    /// </summary>
    private string WriteMp3(string relativePath, int seconds, Action<TagLib.Tag> setTags)
    {
        const int frameLength = 417; // 144 * 128000 / 44100
        var frameCount = (int)Math.Ceiling(seconds * 44100 / 1152.0);
        var frame = new byte[frameLength];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;

        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = System.IO.File.Create(temp))
        {
            for (var i = 0; i < frameCount; i++) stream.Write(frame);
        }

        using (var file = TagLib.File.Create(temp, "taglib/mp3", TagLib.ReadStyle.Average))
        {
            setTags(file.GetTag(TagLib.TagTypes.Id3v2, create: true));
            file.Save();
        }

        System.IO.File.Move(temp, path);
        return path;
    }

    private static void CreateJunction(string link, string target)
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        if (process.ExitCode != 0) Assert.Ignore("Couldn't create a junction: " + process.StandardError.ReadToEnd());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(15)) Assert.Fail("Timed out waiting for the index to change.");
            await Task.Delay(50);
        }
    }
}
