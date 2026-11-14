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
    public void Rewrites_fragmented_file_as_progressive_with_sample_data_in_order()
    {
        int[][] fragments = [[371, 422, 396], [399, 395], [416, 416, 416, 10]];
        var input = Path.Join(directory, "dash.m4a");
        var output = Path.Join(directory, "progressive.m4a");
        File.WriteAllBytes(input, Mp4TestFiles.CreateFragmented(fragments));

        var info = M4aRemuxer.RemuxToProgressive(input, output);

        var file = File.ReadAllBytes(output);
        var topLevel = Mp4Box.ParseChildren(file);
        topLevel.Select(b => b.Type).ShouldBe(new[] { "ftyp", "moov", "mdat" });

        var moov = topLevel[1];
        moov.Child("mvex").ShouldBeNull();

        var stbl = moov.RequiredChild("trak").RequiredChild("mdia").RequiredChild("minf").RequiredChild("stbl");
        stbl.Children.ShouldNotBeNull().Select(b => b.Type).ShouldBe(new[] { "stsd", "stts", "stsc", "stsz", "stco" });

        var sampleSizes = ReadTable(stbl.RequiredChild("stsz").Payload!, start: 12, count: 9, entrySize: 4);
        sampleSizes.ShouldBe(fragments.SelectMany(f => f).Select(s => (long)s));

        // One chunk per fragment, each pointing at that fragment's samples.
        var chunkOffsets = ReadTable(stbl.RequiredChild("stco").Payload!, start: 8, count: 3, entrySize: 4);
        var expected = fragments.Select((sizes, fragment) => sizes.SelectMany((size, sample) => Mp4TestFiles.SampleData(fragment, sample, size)).ToArray()).ToList();
        chunkOffsets.Select((offset, fragment) => file.AsSpan((int)offset, expected[fragment].Length).ToArray()).ShouldBe(expected);

        var stts = stbl.RequiredChild("stts").Payload!;
        ReadTable(stts, start: 4, count: 3, entrySize: 4).ShouldBe(new long[] { 1, 9, Mp4TestFiles.SampleDuration });

        info.IsAac.ShouldBeTrue();
        info.Codec.ShouldBe("mp4a.40.2");
        info.Channels.ShouldBe(2);
        info.SampleRate.ShouldBe(44100);

        // The edit list's zero segment duration is filled in, excluding the 1600 priming samples.
        var expectedDurationMs = (9 * 1024 - 1600) * 1000L / 44100;
        info.Duration.TotalMilliseconds.ShouldBe(expectedDurationMs, 1);
        M4aRemuxer.ReadAudioInfo(output).ShouldBe(info);
    }

    [Test]
    public void Progressive_file_is_copied_as_is()
    {
        var dash = Path.Join(directory, "dash.m4a");
        var progressive = Path.Join(directory, "progressive.m4a");
        var copy = Path.Join(directory, "copy.m4a");
        File.WriteAllBytes(dash, Mp4TestFiles.CreateFragmented([[100, 200]]));
        M4aRemuxer.RemuxToProgressive(dash, progressive);

        M4aRemuxer.RemuxToProgressive(progressive, copy);

        File.ReadAllBytes(copy).ShouldBe(File.ReadAllBytes(progressive));
    }

    [Test]
    public void Opus_audio_is_not_reported_as_aac()
    {
        var path = Path.Join(directory, "opus.mp4");
        File.WriteAllBytes(path, Mp4TestFiles.CreateFragmented([[100]], sampleEntryType: "Opus"));

        var info = M4aRemuxer.ReadAudioInfo(path);

        info.IsAac.ShouldBeFalse();
        info.Codec.ShouldBe("Opus");
    }

    [Test]
    public void Non_mp4_file_is_rejected()
    {
        var path = Path.Join(directory, "not.m4a");
        File.WriteAllText(path, "<html>Not an MP4 file</html>");

        Should.Throw<InvalidDataException>(() => M4aRemuxer.ReadAudioInfo(path));
    }

    private static long[] ReadTable(byte[] payload, int start, int count, int entrySize)
    {
        return Enumerable.Range(0, count)
            .Select(i => (long)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(start + (i * entrySize))))
            .ToArray();
    }
}
