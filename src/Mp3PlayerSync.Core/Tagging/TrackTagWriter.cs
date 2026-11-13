using System.Globalization;
using TagLib;
using TagLib.Id3v2;
using TagLib.Mpeg4;

namespace Mp3PlayerSync.Core.Tagging;

public sealed record TagWriteResult(bool CoverArtEmbedded);

/// <summary>
/// Writes <see cref="TrackMetadata"/> into an audio file's native mainstream tags:
/// iTunes-style MP4 atoms for .m4a and ID3v2.4 for .mp3.
/// </summary>
/// <remarks>
/// Every mapped field is rewritten; a field with no value is removed so stale data (e.g. from the download) doesn't linger.
/// Fields outside the mapping are left alone.
/// </remarks>
public sealed class TrackTagWriter(IAlbumArtFetcher? albumArtFetcher = null)
{
    // iTunes content advisory ("rtng") values.
    private const byte AdvisoryExplicit = 1;

    private static readonly ReadOnlyByteVector RatingBox = "rtng";

    private static readonly ReadOnlyByteVector DayBox = new(0xA9, (byte)'d', (byte)'a', (byte)'y');

    public async Task<TagWriteResult> WriteAsync(string path, TrackMetadata metadata, CancellationToken cancellationToken = default)
    {
        var art = albumArtFetcher is not null && metadata.AlbumImageUrl.Length > 0
            ? await albumArtFetcher.FetchAsync(metadata.AlbumImageUrl, cancellationToken).ConfigureAwait(false)
            : null;

        cancellationToken.ThrowIfCancellationRequested();
        Write(path, metadata, art);
        return new TagWriteResult(CoverArtEmbedded: art is not null);
    }

    /// <param name="art">Cover art to embed, or null to leave any existing pictures as they are.</param>
    public static void Write(string path, TrackMetadata metadata, AlbumArt? art)
    {
        using var file = TagLib.File.Create(path);

        switch (file)
        {
            case TagLib.Mpeg4.File mp4:
                WriteCommon(mp4.GetTag(TagTypes.Apple, create: true), metadata, art);
                WriteApple((AppleTag)mp4.GetTag(TagTypes.Apple), metadata);
                break;

            case TagLib.Mpeg.AudioFile mp3:
                var id3 = (TagLib.Id3v2.Tag)mp3.GetTag(TagTypes.Id3v2, create: true);
                id3.Version = 4;
                WriteCommon(id3, metadata, art);
                WriteId3v2(id3, metadata);
                if (mp3.GetTag(TagTypes.Id3v1) is { } id3v1) WriteCommon(id3v1, metadata, art: null);
                break;

            default:
                throw new NotSupportedException($"Tagging {Path.GetExtension(path)} files isn't supported.");
        }

        file.Save();
    }

    private static void WriteCommon(TagLib.Tag tag, TrackMetadata metadata, AlbumArt? art)
    {
        tag.Title = NullIfEmpty(metadata.Title);
        tag.Performers = JoinedOrEmpty(metadata.Artists);
        tag.AlbumArtists = JoinedOrEmpty(metadata.AlbumArtists);
        tag.Album = NullIfEmpty(metadata.Album);
        tag.Year = (uint)(metadata.Year ?? 0);
        tag.Track = (uint)(metadata.TrackNumber ?? 0);
        tag.Genres = metadata.Genres.Where(g => g.Length > 0).ToArray();

        if (tag is TagLib.Id3v1.Tag) return;

        tag.Disc = (uint)(metadata.DiscNumber ?? 0);
        tag.ISRC = NullIfEmpty(metadata.Isrc);
        tag.Publisher = NullIfEmpty(metadata.Label);
        tag.Copyright = NullIfEmpty(metadata.Copyright);
        tag.BeatsPerMinute = (uint)(metadata.BeatsPerMinute ?? 0);
        tag.InitialKey = NullIfEmpty(metadata.InitialKey);

        if (art is not null)
        {
            tag.Pictures =
            [
                new Picture(new ByteVector(art.Data))
                {
                    Type = PictureType.FrontCover,
                    MimeType = art.MimeType,
                    Description = "",
                },
            ];
        }
    }

    private static void WriteApple(AppleTag tag, TrackMetadata metadata)
    {
        // ©day holds the full release date, not just the year.
        var date = metadata.NormalizedReleaseDate;
        if (date.Length > 0) tag.SetText(DayBox, date);

        if (metadata.Explicit == true)
            tag.SetData(RatingBox, new ByteVector(AdvisoryExplicit), (uint)AppleDataBox.FlagType.ForTempo);
        else
            tag.ClearData(RatingBox);
    }

    private static void WriteId3v2(TagLib.Id3v2.Tag tag, TrackMetadata metadata)
    {
        // TDRC (recording time) holds the full release date in ID3v2.4.
        var date = metadata.NormalizedReleaseDate;
        if (date.Length > 0) tag.SetTextFrame("TDRC", date);
    }

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    // Players commonly show only the first value of a multi-valued artist tag, so artists are joined into one display value.
    private static string[] JoinedOrEmpty(IReadOnlyList<string> values) =>
        values.Count > 0 ? [string.Join(", ", values)] : [];
}
