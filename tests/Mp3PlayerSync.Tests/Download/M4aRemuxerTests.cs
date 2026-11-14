using System.Buffers.Binary;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class M4aRemuxerTests
{
    private string directory = null!;

    [Before(Test)]
    public void SetUp()
    {
        directory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
    }

    [After(Test)]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task Rewrites_fragmented_file_as_progressive_with_sample_data_in_order()
    {
        int[][] fragments = [[371, 422, 396], [399, 395], [416, 416, 416, 10]];
        var input = Path.Join(directory, "dash.m4a");
        var output = Path.Join(directory, "progressive.m4a");
        File.WriteAllBytes(input, Mp4TestFiles.CreateFragmented(fragments));

        var info = M4aRemuxer.RemuxToProgressive(input, output);

        var file = File.ReadAllBytes(output);
        var topLevel = Mp4Box.ParseChildren(file);
        await Assert.That(topLevel.Select(b => b.Type)).IsEquivalentTo(new[] { "ftyp", "moov", "mdat" }, CollectionOrdering.Matching);

        var moov = topLevel[1];
        await Assert.That(moov.Child("mvex")).IsNull();

        var stbl = moov.RequiredChild("trak").RequiredChild("mdia").RequiredChild("minf").RequiredChild("stbl");
        await Assert.That(stbl.Children!.Select(b => b.Type)).IsEquivalentTo(new[] { "stsd", "stts", "stsc", "stsz", "stco" }, CollectionOrdering.Matching);

        var sampleSizes = ReadTable(stbl.RequiredChild("stsz").Payload!, start: 12, count: 9, entrySize: 4);
        await Assert.That(sampleSizes).IsEquivalentTo(fragments.SelectMany(f => f).Select(s => (long)s), CollectionOrdering.Matching);

        // One chunk per fragment, each pointing at that fragment's samples.
        var chunkOffsets = ReadTable(stbl.RequiredChild("stco").Payload!, start: 8, count: 3, entrySize: 4);
        for (var fragment = 0; fragment < fragments.Length; fragment++)
        {
            var expected = fragments[fragment].SelectMany((size, sample) => Mp4TestFiles.SampleData(fragment, sample, size)).ToArray();
            await Assert.That(file.AsSpan((int)chunkOffsets[fragment], expected.Length).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"Fragment {fragment}");
        }

        var stts = stbl.RequiredChild("stts").Payload!;
        await Assert.That(ReadTable(stts, start: 4, count: 3, entrySize: 4)).IsEquivalentTo(new long[] { 1, 9, Mp4TestFiles.SampleDuration }, CollectionOrdering.Matching);

        await Assert.That(info.IsAac).IsTrue();
        await Assert.That(info.Codec).IsEqualTo("mp4a.40.2");
        await Assert.That(info.Channels).IsEqualTo(2);
        await Assert.That(info.SampleRate).IsEqualTo(44100);

        // The edit list's zero segment duration is filled in, excluding the 1600 priming samples.
        var expectedDurationMs = (9 * 1024 - 1600) * 1000L / 44100;
        await Assert.That(info.Duration.TotalMilliseconds).IsEqualTo(expectedDurationMs).Within(1);
        await Assert.That(M4aRemuxer.ReadAudioInfo(output)).IsEqualTo(info);
    }

    [Test]
    public async Task Progressive_file_is_copied_as_is()
    {
        var dash = Path.Join(directory, "dash.m4a");
        var progressive = Path.Join(directory, "progressive.m4a");
        var copy = Path.Join(directory, "copy.m4a");
        File.WriteAllBytes(dash, Mp4TestFiles.CreateFragmented([[100, 200]]));
        M4aRemuxer.RemuxToProgressive(dash, progressive);

        M4aRemuxer.RemuxToProgressive(progressive, copy);

        await Assert.That(File.ReadAllBytes(copy)).IsEquivalentTo(File.ReadAllBytes(progressive), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Opus_audio_is_not_reported_as_aac()
    {
        var path = Path.Join(directory, "opus.mp4");
        File.WriteAllBytes(path, Mp4TestFiles.CreateFragmented([[100]], sampleEntryType: "Opus"));

        var info = M4aRemuxer.ReadAudioInfo(path);

        await Assert.That(info.IsAac).IsFalse();
        await Assert.That(info.Codec).IsEqualTo("Opus");
    }

    [Test]
    public async Task Non_mp4_file_is_rejected()
    {
        var path = Path.Join(directory, "not.m4a");
        File.WriteAllText(path, "<html>Not an MP4 file</html>");

        await Assert.That(() => M4aRemuxer.ReadAudioInfo(path)).ThrowsExactly<InvalidDataException>();
    }

    private static long[] ReadTable(byte[] payload, int start, int count, int entrySize)
    {
        return Enumerable.Range(0, count)
            .Select(i => (long)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(start + (i * entrySize))))
            .ToArray();
    }
}
