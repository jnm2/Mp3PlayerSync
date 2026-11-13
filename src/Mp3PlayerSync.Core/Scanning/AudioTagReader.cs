using System.Text.RegularExpressions;

namespace Mp3PlayerSync.Core.Scanning;

/// <summary>
/// The tags of an audio file on the player. Missing text is empty and missing numbers are <see langword="null"/>.
/// </summary>
/// <param name="Explicit">
/// The content advisory tag: <see langword="true"/> for explicit, <see langword="false"/> for clean, and
/// <see langword="null"/> when the file has none.
/// </param>
public sealed record AudioTags(
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    IReadOnlyList<string> AlbumArtists,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    TimeSpan? Duration,
    string Isrc,
    bool? Explicit = null)
{
    public static AudioTags Empty { get; } = new("", [], "", [], null, null, null, null, "");
}

/// <param name="Path">The full path of the file.</param>
/// <param name="HasTags">
/// Whether the tags were read from the file. When <see langword="false"/>, the file couldn't be read as audio and the
/// title and artist (if any) come from the file name.
/// </param>
public sealed record IndexedFile(string Path, long Size, DateTime LastWriteTimeUtc, AudioTags Tags, bool HasTags);

/// <summary>
/// Reads tags with TagLib#. Never throws for unreadable or unsupported files; falls back to the file name.
/// </summary>
public static partial class AudioTagReader
{
    public static IndexedFile Read(FileInfo file)
    {
        AudioTags? tags = null;
        try
        {
            tags = ReadTags(file.FullName);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // TagLib# throws many exception types for corrupt or unsupported files; locked or vanished files throw
            // IOException. Any of these just means the tags aren't available.
        }

        return new IndexedFile(file.FullName, file.Length, file.LastWriteTimeUtc, WithFileNameFallback(tags ?? AudioTags.Empty, file.Name), HasTags: tags is not null);
    }

    /// <param name="wrapFileStream">Wraps the file's stream, e.g. to observe what is read from it.</param>
    internal static AudioTags ReadTags(string path, Func<Stream, Stream>? wrapFileStream = null)
    {
        Stream stream = ReadOnlyFile.Open(path);
        if (wrapFileStream is not null) stream = wrapFileStream(stream);
        using var abstraction = new ReadOnlyFileAbstraction(path, new BufferedStream(stream, ReadBufferSize));
        using var tagFile = TagLib.File.Create(abstraction, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
        var tag = tagFile.Tag;

        return new AudioTags(
            Title: Clean(tag.Title),
            Artists: CleanList(tag.Performers),
            Album: Clean(tag.Album),
            AlbumArtists: CleanList(tag.AlbumArtists),
            TrackNumber: tag.Track > 0 ? (int)tag.Track : null,
            DiscNumber: tag.Disc > 0 ? (int)tag.Disc : null,
            Year: tag.Year > 0 ? (int)tag.Year : null,
            Duration: tagFile.Properties?.Duration is { Ticks: > 0 } duration ? duration : null,
            Isrc: Clean(tag.ISRC),
            Explicit: ReadAdvisory(tagFile));
    }

    private static readonly TagLib.ReadOnlyByteVector RatingBox = "rtng";

    // iTunes content advisory, stored as the MP4 "rtng" atom or as an ITUNESADVISORY text field elsewhere.
    private static bool? ReadAdvisory(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple
            && apple.DataBoxes(RatingBox).FirstOrDefault(b => b.Data.Count > 0) is { } box)
        {
            return AdvisoryValue(box.Data[0]);
        }

        var text = file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3
            ? TagLib.Id3v2.UserTextInformationFrame.Get(id3, "ITUNESADVISORY", TagLib.Id3v2.Tag.DefaultEncoding, create: false, caseSensitive: false)?.Text.FirstOrDefault()
            : null;
        text ??= (file.GetTag(TagLib.TagTypes.Xiph) as TagLib.Ogg.XiphComment)?.GetFirstField("ITUNESADVISORY");

        return int.TryParse(text?.Trim(), out var value) ? AdvisoryValue(value) : null;
    }

    // 1 is explicit (4 in some older files), 2 is clean, and 0 means no advisory.
    internal static bool? AdvisoryValue(int value) => value switch
    {
        1 or 4 => true,
        2 => false,
        _ => null,
    };

    internal static AudioTags WithFileNameFallback(AudioTags tags, string fileName)
    {
        if (tags.Title.Length > 0 && tags.Artists.Count > 0) return tags;

        var (artist, title) = ParseFileName(fileName);

        if (tags.Title.Length == 0)
            return tags with { Title = title, Artists = tags.Artists.Count == 0 && artist.Length > 0 ? [artist] : tags.Artists };

        // "Artist - Title.mp3" with only the title tagged.
        return artist.Length > 0 && TrackNormalizer.TitleKey(title) == TrackNormalizer.TitleKey(tags.Title)
            ? tags with { Artists = [artist] }
            : tags;
    }

    /// <summary>
    /// Reads "Artist - Title", "01 - Title", "01. Artist - Title" and similar file names.
    /// </summary>
    public static (string Artist, string Title) ParseFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim();
        name = LeadingTrackNumber().Replace(name, "");

        var separator = name.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? (name[..separator].Trim(), name[(separator + 3)..].Trim())
            : ("", name);
    }

    // "01 - ", "1. ", "02) ", "1-02 - " and zero-padded "05 ", but not "99 Luftballons".
    [GeneratedRegex(@"^(?:(?:\d{1,2}[-.])?\d{1,3}\s*[-.)]\s*|0\d{1,2}\s+)(?=\S)")]
    private static partial Regex LeadingTrackNumber();

    private static string Clean(string? value) => value?.Trim().TrimEnd('\0') ?? "";

    private static string[] CleanList(string[]? values) =>
        values is null ? [] : values.Select(Clean).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // One small read per region TagLib# looks at (the start of the file, the first audio frame, the end of the
    // file), rather than large reads that would pull audio data off slow media.
    private const int ReadBufferSize = 4096;

    /// <summary>
    /// Gives TagLib# a read-only stream. TagLib# may close and reopen it, so it stays open until disposed.
    /// </summary>
    private sealed class ReadOnlyFileAbstraction(string path, Stream stream) : TagLib.File.IFileAbstraction, IDisposable
    {
        public string Name => path;
        public Stream ReadStream => stream;
        public Stream WriteStream => throw new NotSupportedException("The player index never writes tags.");
        public void CloseStream(Stream stream) { }
        public void Dispose() => stream.Dispose();
    }
}
