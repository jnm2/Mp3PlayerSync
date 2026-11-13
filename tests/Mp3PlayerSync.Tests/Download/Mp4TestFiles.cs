using System.Buffers.Binary;
using System.Text;

namespace Mp3PlayerSync.Tests.Download;

/// <summary>
/// Builds small MP4 files shaped like YouTube's DASH audio (ftyp, moov with mvex, sidx, then moof/mdat pairs).
/// </summary>
internal static class Mp4TestFiles
{
    public const uint MediaTimescale = 44100;
    public const uint SampleDuration = 1024;

    public static byte[] SampleData(int fragment, int sample, int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)((fragment * 31) + (sample * 7) + i);
        return data;
    }

    public static byte[] CreateFragmented(int[][] fragmentSampleSizes, string sampleEntryType = "mp4a", uint editListMediaTime = 1600)
    {
        using var stream = new MemoryStream();
        stream.Write(Box("ftyp", Ascii("dash"), U32(0), Ascii("iso6mp41")));
        stream.Write(Box("moov",
            FullBox("mvhd", 0, U32(0), U32(0), U32(1000), U32(0), new byte[80]),
            Box("mvex", FullBox("trex", 0, U32(1), U32(1), U32(SampleDuration), U32(0), U32(0))),
            Box("trak",
                FullBox("tkhd", 3, U32(0), U32(0), U32(1), U32(0), U32(0), new byte[60]),
                Box("edts", FullBox("elst", 0, U32(1), U32(0), U32(editListMediaTime), U32(0x10000))),
                Box("mdia",
                    FullBox("mdhd", 0, U32(0), U32(0), U32(MediaTimescale), U32(0), U16(0x55C4), U16(0)),
                    FullBox("hdlr", 0, U32(0), Ascii("soun"), new byte[12], [0]),
                    Box("minf",
                        FullBox("smhd", 0, U32(0)),
                        Box("dinf", FullBox("dref", 0, U32(1), FullBox("url ", 1))),
                        Box("stbl",
                            FullBox("stsd", 0, U32(1), AudioSampleEntry(sampleEntryType)),
                            FullBox("stts", 0, U32(0)),
                            FullBox("stsc", 0, U32(0)),
                            FullBox("stsz", 0, U32(0), U32(0)),
                            FullBox("stco", 0, U32(0))))))));
        stream.Write(FullBox("sidx", 0, new byte[24]));

        for (var fragment = 0; fragment < fragmentSampleSizes.Length; fragment++)
        {
            var sizes = fragmentSampleSizes[fragment];
            byte[] Moof(int dataOffset) => Box("moof",
                FullBox("mfhd", 0, U32((uint)fragment + 1)),
                Box("traf",
                    FullBox("tfhd", 0, U32(1)),
                    FullBox("tfdt", 0, U32(0)),
                    FullBox("trun", 0x201, [U32((uint)sizes.Length), U32((uint)dataOffset), .. sizes.Select(s => U32((uint)s))])));

            var moofSize = Moof(0).Length;
            stream.Write(Moof(moofSize + 8));
            stream.Write(Box("mdat", [.. sizes.Select((size, sample) => SampleData(fragment, sample, size))]));
        }

        return stream.ToArray();
    }

    private static byte[] AudioSampleEntry(string type)
    {
        var child = type == "mp4a"
            ? FullBox("esds", 0, Esds())
            : Box("dOps", new byte[11]);

        return Box(type, new byte[6], U16(1), new byte[8], U16(2), U16(16), U16(0), U16(0), U32(MediaTimescale << 16), child);
    }

    private static byte[] Esds()
    {
        byte[] decoderSpecificInfo = [0x05, 2, 0x12, 0x10]; // AAC-LC, 44.1 kHz, stereo
        byte[] decoderConfig = [0x04, (byte)(13 + decoderSpecificInfo.Length), 0x40, 0x15, 0, 0, 0, .. U32(0), .. U32(128000), .. decoderSpecificInfo];
        return [0x03, (byte)(3 + decoderConfig.Length), 0, 1, 0, .. decoderConfig];
    }

    public static byte[] Box(string type, params byte[][] parts)
    {
        var size = 8 + parts.Sum(p => p.Length);
        return [.. U32((uint)size), .. Ascii(type), .. parts.SelectMany(p => p)];
    }

    public static byte[] FullBox(string type, uint versionAndFlags, params byte[][] parts) => Box(type, [U32(versionAndFlags), .. parts]);

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
}
