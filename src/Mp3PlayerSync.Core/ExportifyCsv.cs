using System.Globalization;

namespace Mp3PlayerSync.Core;

public sealed record ExportedTrack(
    int Number,
    string Uri,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    TimeSpan? Duration,
    string ReleaseDate,
    DateTimeOffset? AddedAt,
    string AddedBy,
    bool? Explicit,
    string Isrc,
    string Genres)
{
    /// <summary>
    /// Every cell of the track's CSV row by column header (case-insensitive), with "undefined"/"null" blanked,
    /// so columns without a dedicated property (e.g. Record Label, Key, Tempo, Album Image URL) stay available.
    /// </summary>
    public IReadOnlyDictionary<string, string> Columns { get; init; } = EmptyColumns;

    private static readonly IReadOnlyDictionary<string, string> EmptyColumns = new Dictionary<string, string>();

    public string ArtistsText => string.Join(", ", Artists);

    public string DurationText => FormatDuration(Duration);

    public string AddedAtText => AddedAt?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "";

    // Used by XAML item containers as the accessible name.
    public override string ToString() => Artists.Count > 0 ? $"{Title} — {ArtistsText}" : Title;

    public static string FormatDuration(TimeSpan? duration) => duration switch
    {
        null => "",
        { TotalHours: >= 1 } d => d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture),
        var d => d.Value.ToString(@"m\:ss", CultureInfo.InvariantCulture),
    };
}

public sealed record ExportedPlaylist(IReadOnlyList<string> Headers, IReadOnlyList<ExportedTrack> Tracks)
{
    public TimeSpan TotalDuration => Tracks.Aggregate(TimeSpan.Zero, (sum, track) => sum + (track.Duration ?? TimeSpan.Zero));
}

/// <summary>
/// Reads the CSV produced by Exportify's per-playlist Export button.
/// </summary>
/// <remarks>
/// exportify.net (pavelkomarov/exportify) separates artist names with ';' and has a "Duration (ms)" column.
/// exportify.app (watsonbox/exportify) separates them with ", " (escaping commas in names as "\,") and has
/// "Track Duration (ms)". Both are accepted.
/// </remarks>
public static class ExportifyCsv
{
    public static ExportedPlaylist Parse(string csv)
    {
        var records = CsvReader.Parse(csv);
        if (records.Count == 0)
            throw new FormatException("The exported CSV is empty.");

        var headers = records[0];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Length; i++)
            columns.TryAdd(headers[i].Trim(), i);

        if (!columns.ContainsKey("Track Name"))
            throw new FormatException("The exported CSV has no \"Track Name\" column.");

        var isExportifyNet = columns.ContainsKey("Duration (ms)");

        var tracks = new List<ExportedTrack>(records.Count - 1);
        foreach (var record in records.Skip(1))
        {
            string Get(params string[] names)
            {
                foreach (var name in names)
                {
                    if (columns.TryGetValue(name, out var index) && index < record.Length)
                    {
                        var value = record[index].Trim();
                        // exportify.net stringifies missing values (e.g. removed tracks) as "undefined".
                        return value is "undefined" or "null" ? "" : value;
                    }
                }

                return "";
            }

            var artistNames = Get("Artist Name(s)");

            var rowColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in columns.Keys)
                rowColumns[name] = Get(name);

            tracks.Add(new ExportedTrack(
                Number: tracks.Count + 1,
                Uri: Get("Track URI"),
                Title: Get("Track Name"),
                Artists: isExportifyNet ? SplitSemicolonList(artistNames) : SplitEscapedCommaList(artistNames),
                Album: Get("Album Name"),
                Duration: long.TryParse(Get("Duration (ms)", "Track Duration (ms)"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                    ? TimeSpan.FromMilliseconds(ms)
                    : null,
                ReleaseDate: Get("Release Date", "Album Release Date"),
                AddedAt: DateTimeOffset.TryParse(Get("Added At"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var addedAt)
                    ? addedAt
                    : null,
                AddedBy: Get("Added By"),
                Explicit: bool.TryParse(Get("Explicit"), out var isExplicit) ? isExplicit : null,
                Isrc: Get("ISRC"),
                Genres: Get("Genres"))
            {
                Columns = rowColumns,
            });
        }

        return new ExportedPlaylist(headers, tracks);
    }

    private static string[] SplitSemicolonList(string value)
    {
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static List<string> SplitEscapedCommaList(string value)
    {
        var items = new List<string>();
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == ',')
            {
                current.Append(',');
                i++;
            }
            else if (value[i] == ',')
            {
                AddCurrent();
            }
            else
            {
                current.Append(value[i]);
            }
        }

        AddCurrent();
        return items;

        void AddCurrent()
        {
            var item = current.ToString().Trim();
            if (item.Length > 0) items.Add(item);
            current.Clear();
        }
    }
}
