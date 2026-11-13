using System.Buffers.Binary;
using System.Text;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// Describes the audio stream of an MP4/M4A file.
/// </summary>
/// <param name="SampleEntryType">The sample entry box type, such as <c>mp4a</c> or <c>Opus</c>.</param>
/// <param name="ObjectTypeIndication">The MPEG-4 object type from the <c>esds</c> box, if any (0x40 for MPEG-4 audio).</param>
/// <param name="AudioObjectType">The MPEG-4 audio object type, if any (2 for AAC-LC, 5 for HE-AAC).</param>
public sealed record M4aAudioInfo(
    string SampleEntryType,
    int? ObjectTypeIndication,
    int? AudioObjectType,
    int Channels,
    int SampleRate,
    TimeSpan Duration,
    int? AverageBitrate)
{
    /// <summary>Whether the audio is AAC (MPEG-4 audio or MPEG-2 AAC).</summary>
    public bool IsAac => SampleEntryType == "mp4a" && ObjectTypeIndication is 0x40 or 0x66 or 0x67 or 0x68;

    /// <summary>An RFC 6381 codec string, such as <c>mp4a.40.2</c>.</summary>
    public string Codec => SampleEntryType == "mp4a" && ObjectTypeIndication is { } oti
        ? AudioObjectType is { } aot ? $"mp4a.{oti:x2}.{aot}" : $"mp4a.{oti:x2}"
        : SampleEntryType;
}

/// <summary>
/// Rewrites a fragmented (DASH) MP4 audio file, as YouTube serves it, into a regular progressive M4A file with a
/// complete sample table and a single <c>mdat</c> box, without re-encoding. This is what yt-dlp would otherwise use
/// ffmpeg for ("writing DASH m4a. Only some players support this container").
/// </summary>
public static class M4aRemuxer
{
    private const uint TfhdBaseDataOffsetPresent = 0x1;
    private const uint TfhdSampleDescriptionIndexPresent = 0x2;
    private const uint TfhdDefaultSampleDurationPresent = 0x8;
    private const uint TfhdDefaultSampleSizePresent = 0x10;
    private const uint TfhdDefaultSampleFlagsPresent = 0x20;
    private const uint TfhdDefaultBaseIsMoof = 0x20000;

    private const uint TrunDataOffsetPresent = 0x1;
    private const uint TrunFirstSampleFlagsPresent = 0x4;
    private const uint TrunSampleDurationPresent = 0x100;
    private const uint TrunSampleSizePresent = 0x200;
    private const uint TrunSampleFlagsPresent = 0x400;
    private const uint TrunSampleCompositionTimeOffsetsPresent = 0x800;

    /// <summary>
    /// Writes a progressive copy of <paramref name="inputPath"/> to <paramref name="outputPath"/>. A file that is
    /// already progressive is copied as is.
    /// </summary>
    public static M4aAudioInfo RemuxToProgressive(string inputPath, string outputPath)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Mp4Box? moov = null;
        var runs = new List<SampleRun>();
        var topLevelOffset = 0L;
        Span<byte> header = stackalloc byte[16];

        while (topLevelOffset + 8 <= input.Length)
        {
            input.Position = topLevelOffset;
            input.ReadExactly(header[..8]);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 1) input.ReadExactly(header[8..16]);
            var (type, headerSize, size) = Mp4Box.ReadHeader(header, input.Length - topLevelOffset);

            if (type is "moov" or "moof")
            {
                var payload = new byte[size - headerSize];
                input.Position = topLevelOffset + headerSize;
                input.ReadExactly(payload);
                var box = Mp4Box.Parse(type, payload);

                if (type == "moov")
                {
                    if (moov is not null) throw new InvalidDataException("The MP4 file has more than one 'moov' box.");
                    moov = box;
                }
                else
                {
                    if (moov is null) throw new InvalidDataException("The MP4 file has a 'moof' box before the 'moov' box.");
                    ReadFragmentRuns(box, topLevelOffset, GetTrex(moov), input.Length, runs);
                }
            }

            topLevelOffset += size;
        }

        if (moov is null) throw new InvalidDataException("The file is not an MP4 file or has no 'moov' box.");

        var trak = GetSingleTrack(moov);
        var mdia = trak.RequiredChild("mdia");
        var stbl = mdia.RequiredChild("minf").RequiredChild("stbl");

        if (moov.Child("mvex") is null)
        {
            if (runs.Count > 0) throw new InvalidDataException("The MP4 file has movie fragments but no 'mvex' box.");

            input.Dispose();
            File.Copy(inputPath, outputPath, overwrite: true);
            return GetAudioInfo(moov, stbl, mediaDuration: null);
        }

        runs.RemoveAll(r => r.SampleSizes.Count == 0);
        if (runs.Count == 0) throw new InvalidDataException("The fragmented MP4 file contains no samples.");

        var mediaDuration = runs.Sum(r => r.SampleDurations.Sum(d => (long)d));
        var mediaTimescale = ReadTimescale(mdia.RequiredChild("mdhd"));
        var mvhd = moov.RequiredChild("mvhd");
        var movieTimescale = ReadTimescale(mvhd);

        var mediaDurationInMovieTimescale = mediaDuration * movieTimescale / mediaTimescale;
        var trackDuration = UpdateEditList(trak, mediaDurationInMovieTimescale, movieTimescale, mediaTimescale) ?? mediaDurationInMovieTimescale;

        WriteDuration(mdia.RequiredChild("mdhd"), mediaDuration, durationOffsetV0: 16, durationOffsetV1: 24);
        WriteDuration(trak.RequiredChild("tkhd"), trackDuration, durationOffsetV0: 20, durationOffsetV1: 28);
        WriteDuration(mvhd, trackDuration, durationOffsetV0: 16, durationOffsetV1: 24);

        moov.Children!.RemoveAll(c => c.Type == "mvex");

        var totalDataSize = runs.Sum(r => r.TotalSize);
        var useLargeMdat = totalDataSize > uint.MaxValue - 8;
        var mdatHeaderSize = useLargeMdat ? 16 : 8;

        var ftyp = CreateFtyp();

        // The chunk offsets depend on the size of moov, which depends on whether 32-bit offsets suffice.
        var useCo64 = false;
        while (true)
        {
            RebuildSampleTable(stbl, runs, firstChunkOffset: 0, useCo64);
            var dataStart = ftyp.Size + moov.Size + mdatHeaderSize;
            if (!useCo64 && dataStart + totalDataSize > uint.MaxValue)
            {
                useCo64 = true;
                continue;
            }

            RebuildSampleTable(stbl, runs, dataStart, useCo64);
            break;
        }

        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            ftyp.WriteTo(output);
            moov.WriteTo(output);

            Span<byte> mdatHeader = stackalloc byte[16];
            if (useLargeMdat)
            {
                BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, 1);
                Encoding.Latin1.GetBytes("mdat", mdatHeader[4..]);
                BinaryPrimitives.WriteUInt64BigEndian(mdatHeader[8..], (ulong)(16 + totalDataSize));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, (uint)(8 + totalDataSize));
                Encoding.Latin1.GetBytes("mdat", mdatHeader[4..]);
            }

            output.Write(mdatHeader[..mdatHeaderSize]);

            var buffer = new byte[81920];
            foreach (var run in runs)
            {
                input.Position = run.DataOffset;
                var remaining = run.TotalSize;
                while (remaining > 0)
                {
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new InvalidDataException("The MP4 file ends before the end of its sample data.");
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }

        return GetAudioInfo(moov, stbl, mediaDuration);
    }

    /// <summary>
    /// Reads the audio stream description of an MP4/M4A file, whether fragmented or not.
    /// </summary>
    public static M4aAudioInfo ReadAudioInfo(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var offset = 0L;
        Span<byte> header = stackalloc byte[16];

        while (offset + 8 <= input.Length)
        {
            input.Position = offset;
            input.ReadExactly(header[..8]);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 1) input.ReadExactly(header[8..16]);
            var (type, headerSize, size) = Mp4Box.ReadHeader(header, input.Length - offset);

            if (type == "moov")
            {
                var payload = new byte[size - headerSize];
                input.ReadExactly(payload);
                var moov = Mp4Box.Parse(type, payload);
                var stbl = GetSingleTrack(moov).RequiredChild("mdia").RequiredChild("minf").RequiredChild("stbl");
                return GetAudioInfo(moov, stbl, mediaDuration: null);
            }

            offset += size;
        }

        throw new InvalidDataException("The file is not an MP4 file or has no 'moov' box.");
    }

    private sealed record SampleRun(long DataOffset, List<uint> SampleSizes, List<uint> SampleDurations)
    {
        public long TotalSize { get; } = SampleSizes.Sum(s => (long)s);
    }

    private readonly record struct TrackDefaults(uint SampleDuration, uint SampleSize);

    private static TrackDefaults GetTrex(Mp4Box moov)
    {
        var trex = moov.RequiredChild("mvex").RequiredChild("trex").Payload!;
        // version/flags(4) track_ID(4) default_sample_description_index(4) default_sample_duration(4) default_sample_size(4)
        return new TrackDefaults(
            SampleDuration: BinaryPrimitives.ReadUInt32BigEndian(trex.AsSpan(12)),
            SampleSize: BinaryPrimitives.ReadUInt32BigEndian(trex.AsSpan(16)));
    }

    private static Mp4Box GetSingleTrack(Mp4Box moov)
    {
        var traks = moov.Children!.Where(c => c.Type == "trak").ToList();
        return traks.Count == 1
            ? traks[0]
            : throw new InvalidDataException($"Expected one track in the MP4 file, but found {traks.Count}.");
    }

    private static void ReadFragmentRuns(Mp4Box moof, long moofOffset, TrackDefaults trex, long fileLength, List<SampleRun> runs)
    {
        var previousDataEnd = moofOffset;
        var isFirstTraf = true;

        foreach (var traf in moof.Children!.Where(c => c.Type == "traf"))
        {
            var tfhd = traf.RequiredChild("tfhd").Payload!;
            var tfhdFlags = BinaryPrimitives.ReadUInt32BigEndian(tfhd) & 0xFFFFFF;
            var position = 8; // version/flags, track_ID

            long baseDataOffset;
            if ((tfhdFlags & TfhdBaseDataOffsetPresent) != 0)
            {
                baseDataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(tfhd.AsSpan(position));
                position += 8;
            }
            else
            {
                baseDataOffset = (tfhdFlags & TfhdDefaultBaseIsMoof) != 0 || isFirstTraf ? moofOffset : previousDataEnd;
            }

            if ((tfhdFlags & TfhdSampleDescriptionIndexPresent) != 0) position += 4;

            var defaultDuration = trex.SampleDuration;
            if ((tfhdFlags & TfhdDefaultSampleDurationPresent) != 0)
            {
                defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(tfhd.AsSpan(position));
                position += 4;
            }

            var defaultSize = trex.SampleSize;
            if ((tfhdFlags & TfhdDefaultSampleSizePresent) != 0)
            {
                defaultSize = BinaryPrimitives.ReadUInt32BigEndian(tfhd.AsSpan(position));
                position += 4;
            }

            if ((tfhdFlags & TfhdDefaultSampleFlagsPresent) != 0) position += 4;

            var nextDataStart = baseDataOffset;
            foreach (var trunBox in traf.Children!.Where(c => c.Type == "trun"))
            {
                var trun = trunBox.Payload!.AsSpan();
                var flags = BinaryPrimitives.ReadUInt32BigEndian(trun) & 0xFFFFFF;
                var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(trun[4..]);
                var p = 8;

                var dataStart = nextDataStart;
                if ((flags & TrunDataOffsetPresent) != 0)
                {
                    dataStart = baseDataOffset + BinaryPrimitives.ReadInt32BigEndian(trun[p..]);
                    p += 4;
                }

                if ((flags & TrunFirstSampleFlagsPresent) != 0) p += 4;

                var entrySize =
                    ((flags & TrunSampleDurationPresent) != 0 ? 4 : 0)
                    + ((flags & TrunSampleSizePresent) != 0 ? 4 : 0)
                    + ((flags & TrunSampleFlagsPresent) != 0 ? 4 : 0)
                    + ((flags & TrunSampleCompositionTimeOffsetsPresent) != 0 ? 4 : 0);

                if ((long)sampleCount * entrySize > trun.Length - p)
                    throw new InvalidDataException("An MP4 'trun' box is truncated.");

                var sizes = new List<uint>((int)sampleCount);
                var durations = new List<uint>((int)sampleCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    var duration = defaultDuration;
                    if ((flags & TrunSampleDurationPresent) != 0) { duration = BinaryPrimitives.ReadUInt32BigEndian(trun[p..]); p += 4; }

                    var size = defaultSize;
                    if ((flags & TrunSampleSizePresent) != 0) { size = BinaryPrimitives.ReadUInt32BigEndian(trun[p..]); p += 4; }

                    if ((flags & TrunSampleFlagsPresent) != 0) p += 4;
                    if ((flags & TrunSampleCompositionTimeOffsetsPresent) != 0) p += 4;

                    sizes.Add(size);
                    durations.Add(duration);
                }

                var run = new SampleRun(dataStart, sizes, durations);
                if (run.DataOffset < 0 || run.DataOffset + run.TotalSize > fileLength)
                    throw new InvalidDataException("An MP4 'trun' box refers to data outside the file.");

                runs.Add(run);
                nextDataStart = run.DataOffset + run.TotalSize;
            }

            previousDataEnd = nextDataStart;
            isFirstTraf = false;
        }
    }

    private static uint ReadTimescale(Mp4Box mvhdOrMdhd)
    {
        var payload = mvhdOrMdhd.Payload!;
        return BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(payload[0] == 1 ? 20 : 12));
    }

    private static void WriteDuration(Mp4Box box, long duration, int durationOffsetV0, int durationOffsetV1)
    {
        var payload = box.Payload!;
        if (payload[0] == 1)
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(durationOffsetV1), (ulong)duration);
        else
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(durationOffsetV0), checked((uint)duration));
    }

    /// <summary>
    /// Fills in a zero segment duration in a single-entry edit list (common in fragmented files, where the duration
    /// isn't known up front). Returns the track duration implied by the edit list, if there is one.
    /// </summary>
    private static long? UpdateEditList(Mp4Box trak, long mediaDurationInMovieTimescale, uint movieTimescale, uint mediaTimescale)
    {
        if (trak.Child("edts")?.Child("elst")?.Payload is not { } elst) return null;

        var isVersion1 = elst[0] == 1;
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(4));
        var entrySize = isVersion1 ? 20 : 12;
        var total = 0L;

        for (var i = 0; i < entryCount; i++)
        {
            var entry = elst.AsSpan(8 + (i * entrySize));
            var segmentDuration = isVersion1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry);
            var mediaTime = isVersion1 ? BinaryPrimitives.ReadInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadInt32BigEndian(entry[4..]);

            if (segmentDuration == 0 && entryCount == 1 && mediaTime >= 0)
            {
                segmentDuration = Math.Max(0, mediaDurationInMovieTimescale - (mediaTime * movieTimescale / mediaTimescale));
                if (isVersion1) BinaryPrimitives.WriteUInt64BigEndian(entry, (ulong)segmentDuration);
                else BinaryPrimitives.WriteUInt32BigEndian(entry, checked((uint)segmentDuration));
            }

            total += segmentDuration;
        }

        return total;
    }

    private static Mp4Box CreateFtyp()
    {
        var payload = new byte[20];
        Encoding.Latin1.GetBytes("M4A ", payload);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0x200);
        Encoding.Latin1.GetBytes("M4A mp42isom", payload.AsSpan(8));
        return new Mp4Box("ftyp", payload);
    }

    private static void RebuildSampleTable(Mp4Box stbl, List<SampleRun> runs, long firstChunkOffset, bool useCo64)
    {
        var stsd = stbl.RequiredChild("stsd");
        stbl.Children!.Clear();
        stbl.Children.Add(stsd);
        stbl.Children.Add(BuildStts(runs));
        stbl.Children.Add(BuildStsc(runs));
        stbl.Children.Add(BuildStsz(runs));
        stbl.Children.Add(BuildChunkOffsets(runs, firstChunkOffset, useCo64));
    }

    private static Mp4Box BuildStts(List<SampleRun> runs)
    {
        var entries = new List<(uint Count, uint Delta)>();
        foreach (var duration in runs.SelectMany(r => r.SampleDurations))
        {
            if (entries.Count > 0 && entries[^1].Delta == duration)
                entries[^1] = (entries[^1].Count + 1, duration);
            else
                entries.Add((1, duration));
        }

        var payload = new byte[8 + (entries.Count * 8)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + (i * 8)), entries[i].Count);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12 + (i * 8)), entries[i].Delta);
        }

        return new Mp4Box("stts", payload);
    }

    private static Mp4Box BuildStsc(List<SampleRun> runs)
    {
        // One chunk per fragment run.
        var entries = new List<(uint FirstChunk, uint SamplesPerChunk)>();
        for (var i = 0; i < runs.Count; i++)
        {
            var samplesPerChunk = (uint)runs[i].SampleSizes.Count;
            if (entries.Count == 0 || entries[^1].SamplesPerChunk != samplesPerChunk)
                entries.Add(((uint)i + 1, samplesPerChunk));
        }

        var payload = new byte[8 + (entries.Count * 12)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + (i * 12)), entries[i].FirstChunk);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12 + (i * 12)), entries[i].SamplesPerChunk);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16 + (i * 12)), 1); // sample_description_index
        }

        return new Mp4Box("stsc", payload);
    }

    private static Mp4Box BuildStsz(List<SampleRun> runs)
    {
        var sizes = runs.SelectMany(r => r.SampleSizes).ToList();
        var constantSize = sizes.All(s => s == sizes[0]) ? sizes[0] : 0;

        var payload = new byte[12 + (constantSize == 0 ? sizes.Count * 4 : 0)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), constantSize);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), (uint)sizes.Count);
        if (constantSize == 0)
        {
            for (var i = 0; i < sizes.Count; i++)
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12 + (i * 4)), sizes[i]);
        }

        return new Mp4Box("stsz", payload);
    }

    private static Mp4Box BuildChunkOffsets(List<SampleRun> runs, long firstChunkOffset, bool useCo64)
    {
        var entrySize = useCo64 ? 8 : 4;
        var payload = new byte[8 + (runs.Count * entrySize)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)runs.Count);

        var offset = firstChunkOffset;
        for (var i = 0; i < runs.Count; i++)
        {
            if (useCo64) BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8 + (i * 8)), (ulong)offset);
            else BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8 + (i * 4)), (uint)offset);
            offset += runs[i].TotalSize;
        }

        return new Mp4Box(useCo64 ? "co64" : "stco", payload);
    }

    private static M4aAudioInfo GetAudioInfo(Mp4Box moov, Mp4Box stbl, long? mediaDuration)
    {
        var mdia = GetSingleTrack(moov).RequiredChild("mdia");
        var mediaTimescale = ReadTimescale(mdia.RequiredChild("mdhd"));

        if (mediaDuration is null)
        {
            var mdhd = mdia.RequiredChild("mdhd").Payload!;
            mediaDuration = mdhd[0] == 1
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(mdhd.AsSpan(24))
                : BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(16));
        }

        var stsd = stbl.RequiredChild("stsd").Payload!.AsSpan();
        // version/flags(4) entry_count(4), then the first sample entry box.
        if (stsd.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(stsd[4..]) == 0)
            throw new InvalidDataException("The MP4 track has no sample description.");

        var (entryType, entryHeaderSize, entrySize) = Mp4Box.ReadHeader(stsd[8..], stsd.Length - 8);
        var entry = stsd.Slice(8 + entryHeaderSize, (int)entrySize - entryHeaderSize);

        // SampleEntry: reserved(6) data_reference_index(2); AudioSampleEntry: version(2) revision(2) vendor(4)
        // channelcount(2) samplesize(2) pre_defined(2) reserved(2) samplerate(4, 16.16)
        if (entry.Length < 28) throw new InvalidDataException("The MP4 audio sample entry is truncated.");
        var channels = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);
        var sampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(entry[24..]) >> 16);

        int? objectTypeIndication = null, audioObjectType = null, averageBitrate = null;
        if (entryType == "mp4a")
        {
            var soundDescriptionVersion = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
            var childrenStart = 28 + soundDescriptionVersion switch { 1 => 16, 2 => 36, _ => 0 };
            if (childrenStart <= entry.Length)
            {
                var esds = Mp4Box.ParseChildren(entry[childrenStart..]).FirstOrDefault(c => c.Type == "esds");
                if (esds?.Payload is { } esdsPayload)
                    (objectTypeIndication, audioObjectType, averageBitrate) = ParseEsds(esdsPayload.AsSpan(4));
            }
        }

        var duration = mediaTimescale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)mediaDuration.Value / mediaTimescale);

        // Prefer the presentation duration, which excludes encoder priming samples trimmed by the edit list.
        var mvhd = moov.RequiredChild("mvhd").Payload!;
        var movieTimescale = ReadTimescale(moov.RequiredChild("mvhd"));
        var movieDuration = mvhd[0] == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
        if (movieTimescale != 0 && movieDuration > 0)
            duration = TimeSpan.FromSeconds((double)movieDuration / movieTimescale);

        return new M4aAudioInfo(entryType, objectTypeIndication, audioObjectType, channels, sampleRate, duration, averageBitrate is > 0 ? averageBitrate : null);
    }

    private static (int? ObjectTypeIndication, int? AudioObjectType, int? AverageBitrate) ParseEsds(ReadOnlySpan<byte> data)
    {
        if (!TryReadDescriptor(ref data, out var tag, out var esDescriptor) || tag != 0x03 || esDescriptor.Length < 3)
            return default;

        var esFlags = esDescriptor[2];
        var p = 3;
        if ((esFlags & 0x80) != 0) p += 2;
        if ((esFlags & 0x40) != 0 && p < esDescriptor.Length) p += 1 + esDescriptor[p];
        if ((esFlags & 0x20) != 0) p += 2;
        if (p > esDescriptor.Length) return default;

        var rest = esDescriptor[p..];
        while (TryReadDescriptor(ref rest, out tag, out var descriptor))
        {
            if (tag != 0x04 || descriptor.Length < 13) continue;

            // objectTypeIndication(1) streamType(1) bufferSizeDB(3) maxBitrate(4) avgBitrate(4)
            int objectTypeIndication = descriptor[0];
            var averageBitrate = (int)BinaryPrimitives.ReadUInt32BigEndian(descriptor[9..]);

            int? audioObjectType = null;
            var decoderConfigRest = descriptor[13..];
            if (TryReadDescriptor(ref decoderConfigRest, out var specificTag, out var specificInfo) && specificTag == 0x05 && specificInfo.Length >= 1)
                audioObjectType = specificInfo[0] >> 3;

            return (objectTypeIndication, audioObjectType, averageBitrate);
        }

        return default;
    }

    private static bool TryReadDescriptor(ref ReadOnlySpan<byte> data, out byte tag, out ReadOnlySpan<byte> contents)
    {
        tag = 0;
        contents = default;
        if (data.Length < 2) return false;

        tag = data[0];
        var length = 0;
        var p = 1;
        for (var i = 0; i < 4; i++)
        {
            if (p >= data.Length) return false;
            var b = data[p++];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }

        if (length > data.Length - p) return false;

        contents = data.Slice(p, length);
        data = data[(p + length)..];
        return true;
    }
}
