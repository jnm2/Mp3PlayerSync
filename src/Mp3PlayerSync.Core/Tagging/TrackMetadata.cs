using System.Globalization;
using Mp3PlayerSync.Core.Scanning;

namespace Mp3PlayerSync.Core.Tagging;

/// <summary>
/// Everything known about a track that maps to a mainstream audio tag.
/// </summary>
public sealed record TrackMetadata
{
    private static readonly string[] PitchClasses = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public string Title { get; init; } = "";

    public IReadOnlyList<string> Artists { get; init; } = [];

    public string Album { get; init; } = "";

    public IReadOnlyList<string> AlbumArtists { get; init; } = [];

    /// <summary>Spotify's album release date: "YYYY", "YYYY-MM" or "YYYY-MM-DD".</summary>
    public string ReleaseDate { get; init; } = "";

    public int? TrackNumber { get; init; }

    public int? DiscNumber { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public string Isrc { get; init; } = "";

    /// <summary>Record label, written as the publisher.</summary>
    public string Label { get; init; } = "";

    public string Copyright { get; init; } = "";

    public bool? Explicit { get; init; }

    /// <summary>Spotify's audio-features tempo in beats per minute.</summary>
    public double? Tempo { get; init; }

    /// <summary>Spotify's audio-features key as a pitch class (0 = C … 11 = B); null or -1 when unknown.</summary>
    public int? Key { get; init; }

    /// <summary>Spotify's audio-features mode: 1 = major, 0 = minor.</summary>
    public int? Mode { get; init; }

    /// <summary>Cover art URL (Spotify's image CDN).</summary>
    public string AlbumImageUrl { get; init; } = "";

    public string PrimaryArtist => Artists.Count > 0 ? Artists[0] : "";

    public string PrimaryAlbumArtist => AlbumArtists.Count > 0 ? AlbumArtists[0] : PrimaryArtist;

    /// <summary>The release year, or null when the release date is missing or "0000".</summary>
    public int? Year => ParseYear(ReleaseDate);

    /// <summary>The release date normalized to "YYYY", "YYYY-MM" or "YYYY-MM-DD", or "" when unknown.</summary>
    public string NormalizedReleaseDate
    {
        get
        {
            if (Year is null) return "";
            foreach (var format in (string[])["yyyy-MM-dd", "yyyy-MM"])
            {
                if (DateTime.TryParseExact(ReleaseDate.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return date.ToString(format, CultureInfo.InvariantCulture);
            }

            return Year.Value.ToString("D4", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Tempo rounded to whole beats per minute, or null when unknown.</summary>
    public int? BeatsPerMinute => Tempo is > 0 and < 1000 ? (int)Math.Round(Tempo.Value, MidpointRounding.AwayFromZero) : null;

    /// <summary>The musical key in ID3v2 TKEY notation (e.g. "C", "F#m"), or "" when unknown.</summary>
    public string InitialKey => Key is >= 0 and <= 11
        ? PitchClasses[Key.Value] + (Mode == 0 ? "m" : "")
        : "";

    public static int? ParseYear(string releaseDate)
    {
        var text = releaseDate.Trim();
        return text.Length >= 4
            && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year > 0
            && (text.Length == 4 || text[4] == '-')
                ? year
                : null;
    }

    /// <summary>
    /// Maps the tracks of one album, in Spotify's album order (the export's row order), to tag metadata.
    /// A track's number is its position in the album unless the export has a Track Number column, and every
    /// track gets the album's artist (<see cref="AlbumArtistsOf"/>) unless the export has an Album Artist Name(s) column.
    /// </summary>
    /// <returns>The metadata for each track, in the same order.</returns>
    public static IReadOnlyList<TrackMetadata> FromAlbum(IReadOnlyList<ExportedTrack> albumTracks)
    {
        var albumArtists = AlbumArtistsOf(albumTracks);
        return albumTracks.Select((track, index) => FromExportedTrack(track, albumPosition: index + 1) is var metadata && metadata.AlbumArtists.Count == 0
                ? metadata with { AlbumArtists = albumArtists }
                : metadata)
            .ToList();
    }

    public const string VariousArtists = "Various Artists";

    /// <summary>
    /// The album artist for a whole album when the export doesn't say: the artist credited on every track (the
    /// first-credited one if several are), or <see cref="VariousArtists"/> for a compilation or soundtrack.
    /// </summary>
    public static IReadOnlyList<string> AlbumArtistsOf(IReadOnlyList<ExportedTrack> albumTracks)
    {
        var credited = albumTracks.Where(t => t.Artists.Count > 0).ToList();
        if (credited.Count == 0) return [];

        var common = credited[0].Artists.FirstOrDefault(artist => credited.All(t => t.Artists.Any(a => TrackNormalizer.ArtistKey(a) == TrackNormalizer.ArtistKey(artist))));
        return [common ?? VariousArtists];
    }

    /// <summary>
    /// Maps an Exportify CSV row (exportify.net or exportify.app layout) to tag metadata.
    /// </summary>
    /// <param name="albumPosition">
    /// The track's 1-based position in its album's Spotify order, used as the track number when the export has no
    /// Track Number column. Leave null for playlist tracks: a playlist position isn't a track number.
    /// </param>
    /// <remarks>
    /// exportify.net has no album artist column. One track can't tell its album's artist (a compilation's tracks each
    /// have their own artist), so the album artist is left empty; use <see cref="FromAlbum"/> when the whole album is known.
    /// </remarks>
    public static TrackMetadata FromExportedTrack(ExportedTrack track, int? albumPosition = null)
    {
        string Column(params string[] names)
        {
            foreach (var name in names)
            {
                if (track.Columns.TryGetValue(name, out var value) && value.Length > 0)
                    return value;
            }

            return "";
        }

        int? IntColumn(params string[] names) =>
            int.TryParse(Column(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        var albumArtists = ExportifyCsv.SplitEscapedCommaList(Column("Album Artist Name(s)"));
        var genres = new[] { track.Genres, Column("Artist Genres"), Column("Album Genres") }
            .SelectMany(list => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TrackMetadata
        {
            Title = track.Title,
            Artists = track.Artists,
            Album = track.Album,
            AlbumArtists = albumArtists,
            ReleaseDate = track.ReleaseDate,
            TrackNumber = IntColumn("Track Number") is > 0 and var trackNumber ? trackNumber : albumPosition is > 0 ? albumPosition : null,
            DiscNumber = IntColumn("Disc Number") is > 0 and var discNumber ? discNumber : null,
            Genres = genres,
            Isrc = track.Isrc,
            Label = Column("Record Label", "Label"),
            Copyright = Column("Copyrights", "Copyright"),
            Explicit = track.Explicit ?? (bool.TryParse(Column("Explicit?"), out var isExplicit) ? isExplicit : null),
            Tempo = double.TryParse(Column("Tempo"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo) ? tempo : null,
            Key = IntColumn("Key"),
            Mode = IntColumn("Mode"),
            AlbumImageUrl = Column("Album Image URL"),
        };
    }
}
