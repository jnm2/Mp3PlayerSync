using System.Buffers.Binary;
using System.Text;

namespace Mp3PlayerSync.Core.Download;

/// <summary>
/// An ISO base media file format (MP4) box. Boxes of container types are parsed into <see cref="Children"/>; all
/// others keep their raw payload.
/// </summary>
internal sealed class Mp4Box
{
    private static readonly HashSet<string> ContainerTypes = ["moov", "trak", "mdia", "minf", "stbl", "edts", "mvex", "moof", "traf"];

    public Mp4Box(string type, byte[] payload)
    {
        Type = type;
        Payload = payload;
    }

    public Mp4Box(string type, List<Mp4Box> children)
    {
        Type = type;
        Children = children;
    }

    public string Type { get; }

    public byte[]? Payload { get; }

    public List<Mp4Box>? Children { get; }

    public Mp4Box? Child(string type) => Children?.FirstOrDefault(c => c.Type == type);

    public Mp4Box RequiredChild(string type) => Child(type) ?? throw new InvalidDataException($"The MP4 '{Type}' box has no '{type}' box.");

    public static Mp4Box Parse(string type, ReadOnlySpan<byte> payload)
    {
        return ContainerTypes.Contains(type)
            ? new Mp4Box(type, ParseChildren(payload))
            : new Mp4Box(type, payload.ToArray());
    }

    public static List<Mp4Box> ParseChildren(ReadOnlySpan<byte> data)
    {
        var boxes = new List<Mp4Box>();
        while (data.Length >= 8)
        {
            var (type, headerSize, size) = ReadHeader(data, data.Length);
            boxes.Add(Parse(type, data[headerSize..(int)size]));
            data = data[(int)size..];
        }

        return boxes;
    }

    /// <summary>
    /// Reads a box header. <paramref name="available"/> is the number of bytes from the start of the box to the end of
    /// its parent (or file), used for boxes whose size field is 0.
    /// </summary>
    public static (string Type, int HeaderSize, long Size) ReadHeader(ReadOnlySpan<byte> header, long available)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.Latin1.GetString(header[4..8]);
        var headerSize = 8;

        if (size == 1)
        {
            if (header.Length < 16) throw new InvalidDataException("The MP4 data ends within a box header.");
            size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]));
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = available;
        }

        if (size < headerSize || size > available)
            throw new InvalidDataException($"The MP4 '{type}' box has an invalid size.");

        return (type, headerSize, size);
    }

    public long Size => 8 + (Payload?.Length ?? Children!.Sum(c => c.Size));

    public void WriteTo(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)Size));
        Encoding.Latin1.GetBytes(Type, header[4..]);
        stream.Write(header);

        if (Payload is not null)
        {
            stream.Write(Payload);
        }
        else
        {
            foreach (var child in Children!)
                child.WriteTo(stream);
        }
    }
}
