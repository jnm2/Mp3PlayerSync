namespace Mp3PlayerSync.Core.Scanning;

public enum TrackMatchKind
{
    /// <summary>Not on the player.</summary>
    None,

    /// <summary>A file has the same ISRC.</summary>
    Isrc,

    /// <summary>A file has the same normalized title and a shared artist, and the durations agree if both are known.</summary>
    ArtistAndTitle,

    /// <summary>
    /// A file has the same title apart from a trailing " - …" or bracketed part (such as "From the Movie"), a shared
    /// artist, and a duration within the tolerance.
    /// </summary>
    SimilarTitle,

    /// <summary>A file with no artist tag has the same title and a duration within the tolerance.</summary>
    TitleAndDuration,
}

/// <param name="Files">The matching files on the player, best match first. Empty when not on the player.</param>
public sealed record TrackMatch(ExportedTrack Track, TrackMatchKind Kind, IReadOnlyList<IndexedFile> Files)
{
    public bool IsOnPlayer => Files.Count > 0;
}

/// <summary>
/// A normalized album identity: album artist and album title keys from <see cref="TrackNormalizer"/>.
/// </summary>
public readonly record struct AlbumKey(string Artist, string Album)
{
    public static AlbumKey Create(string albumArtist, string album) =>
        new(TrackNormalizer.ArtistKey(albumArtist), TrackNormalizer.AlbumKey(album));

    /// <summary>
    /// The album identity of a file: its album artist, or its first artist when it has no album artist tag.
    /// </summary>
    public static AlbumKey For(AudioTags tags)
    {
        var artist = tags.AlbumArtists.FirstOrDefault()
            ?? tags.Artists.Select(a => TrackNormalizer.SplitArtists(a).FirstOrDefault()).FirstOrDefault(a => a is not null)
            ?? "";
        return Create(artist, tags.Album);
    }
}

/// <summary>
/// An immutable view of the files on the player, with lookups for matching playlist tracks and finding albums.
/// Safe to use from any thread.
/// </summary>
public sealed class PlayerIndexSnapshot
{
    public static readonly TimeSpan DefaultDurationTolerance = TimeSpan.FromSeconds(4);

    private readonly Lazy<Lookups> lookups;

    public PlayerIndexSnapshot(IEnumerable<IndexedFile> files, TimeSpan? durationTolerance = null, bool isScanComplete = true, long version = 0)
    {
        Files = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        DurationTolerance = durationTolerance ?? DefaultDurationTolerance;
        IsScanComplete = isScanComplete;
        Version = version;
        lookups = new Lazy<Lookups>(() => new Lookups(Files));
    }

    /// <summary>All indexed audio files, ordered by path.</summary>
    public IReadOnlyList<IndexedFile> Files { get; }

    /// <summary>
    /// Whether the initial scan had finished when this snapshot was taken. Before then, some files may be missing.
    /// </summary>
    public bool IsScanComplete { get; }

    /// <summary>Increases whenever the index changes.</summary>
    public long Version { get; }

    public TimeSpan DurationTolerance { get; }

    /// <summary>
    /// Indexed files grouped by <see cref="AlbumKey.For"/>. Files without an album tag are left out.
    /// </summary>
    public IReadOnlyDictionary<AlbumKey, IReadOnlyList<IndexedFile>> Albums => lookups.Value.Albums;

    public IReadOnlyList<TrackMatch> Match(IEnumerable<ExportedTrack> tracks) => tracks.Select(Match).ToArray();

    /// <summary>
    /// Finds the files on the player that are the given track: by ISRC, then by normalized artist and title with
    /// durations within <see cref="DurationTolerance"/> when both are known.
    /// </summary>
    public TrackMatch Match(ExportedTrack track)
    {
        var found = new List<IndexedFile>();
        var kind = TrackMatchKind.None;

        void Add(IEnumerable<Entry> entries, TrackMatchKind entryKind)
        {
            foreach (var entry in entries)
            {
                if (found.Contains(entry.File)) continue;
                found.Add(entry.File);
                if (kind == TrackMatchKind.None) kind = entryKind;
            }
        }

        var isrc = TrackNormalizer.IsrcKey(track.Isrc);
        if (isrc.Length > 0 && lookups.Value.ByIsrc.TryGetValue(isrc, out var sameIsrc))
            Add(sameIsrc, TrackMatchKind.Isrc);

        var title = TrackNormalizer.StripTitleDecorations(track.Title, out var featured, out var titleMarker);
        var titleKey = TrackNormalizer.Key(title);
        if (titleKey.Length == 0) return new TrackMatch(track, kind, found);

        var artistKeys = TrackNormalizer.ArtistKeys(track.Artists.Concat(featured));
        var trackExplicit = track.Explicit ?? titleMarker ?? TrackNormalizer.ExplicitMarker(track.Album);

        // Apart from ISRCs (which differ between versions), a known explicit file isn't the clean track or vice versa.
        bool SameVersion(Entry e) => trackExplicit is null || e.Explicit is null || trackExplicit == e.Explicit;

        if (lookups.Value.ByTitle.TryGetValue(titleKey, out var sameTitle))
        {
            Add(sameTitle.Where(e => SameVersion(e) && e.ArtistKeys.Overlaps(artistKeys) && DurationsAgree(track.Duration, e.File.Tags.Duration, required: false)), TrackMatchKind.ArtistAndTitle);
            Add(sameTitle.Where(e => SameVersion(e) && (e.ArtistKeys.Count == 0 || artistKeys.Count == 0) && DurationsAgree(track.Duration, e.File.Tags.Duration, required: true)), TrackMatchKind.TitleAndDuration);
        }

        var baseTitleKey = TrackNormalizer.BaseTitleKey(track.Title);
        if (found.Count == 0 && baseTitleKey.Length > 0 && lookups.Value.ByBaseTitle.TryGetValue(baseTitleKey, out var similarTitle))
            Add(similarTitle.Where(e => SameVersion(e) && e.ArtistKeys.Overlaps(artistKeys) && DurationsAgree(track.Duration, e.File.Tags.Duration, required: true)), TrackMatchKind.SimilarTitle);

        return new TrackMatch(track, kind, found);
    }

    /// <summary>
    /// The files on the player from the given album: the album title must match after normalization (ignoring
    /// suffixes like "(Remastered)"), and the file's album artist or artist must share a name with
    /// <paramref name="artists"/>. With no artists given, any files with that album title are returned.
    /// </summary>
    public IReadOnlyList<IndexedFile> FindAlbum(IEnumerable<string> artists, string album)
    {
        var albumKey = TrackNormalizer.AlbumKey(album);
        if (albumKey.Length == 0 || !lookups.Value.ByAlbum.TryGetValue(albumKey, out var entries)) return [];

        var artistKeys = TrackNormalizer.ArtistKeys(artists);
        return entries
            .Where(e => artistKeys.Count == 0 || e.AlbumArtistKeys.Overlaps(artistKeys) || e.ArtistKeys.Overlaps(artistKeys))
            .Select(e => e.File)
            .ToArray();
    }

    public IReadOnlyList<IndexedFile> FindAlbum(string albumArtist, string album) => FindAlbum([albumArtist], album);

    /// <summary>The files on the player from the album of the given playlist track.</summary>
    public IReadOnlyList<IndexedFile> FindAlbum(ExportedTrack track) => FindAlbum(track.Artists, track.Album);

    private bool DurationsAgree(TimeSpan? a, TimeSpan? b, bool required) =>
        a is { } x && b is { } y ? (x - y).Duration() <= DurationTolerance : !required;

    private sealed class Entry
    {
        public Entry(IndexedFile file)
        {
            File = file;
            TitleKey = TrackNormalizer.Key(TrackNormalizer.StripTitleDecorations(file.Tags.Title, out var featured, out var titleMarker));
            Explicit = file.Tags.Explicit ?? titleMarker ?? TrackNormalizer.ExplicitMarker(file.Tags.Album);
            BaseTitleKey = TrackNormalizer.BaseTitleKey(file.Tags.Title);
            ArtistKeys = TrackNormalizer.ArtistKeys(file.Tags.Artists.Concat(featured));
            AlbumArtistKeys = TrackNormalizer.ArtistKeys(file.Tags.AlbumArtists);
            IsrcKey = TrackNormalizer.IsrcKey(file.Tags.Isrc);
            AlbumKey = TrackNormalizer.AlbumKey(file.Tags.Album);
        }

        public IndexedFile File { get; }
        public bool? Explicit { get; }
        public string TitleKey { get; }
        public string BaseTitleKey { get; }
        public HashSet<string> ArtistKeys { get; }
        public HashSet<string> AlbumArtistKeys { get; }
        public string IsrcKey { get; }
        public string AlbumKey { get; }
    }

    private sealed class Lookups
    {
        public Lookups(IReadOnlyList<IndexedFile> files)
        {
            var entries = files.Select(f => new Entry(f)).ToArray();
            ByIsrc = Group(entries, e => e.IsrcKey);
            ByTitle = Group(entries, e => e.TitleKey);
            ByBaseTitle = Group(entries, e => e.BaseTitleKey);
            ByAlbum = Group(entries, e => e.AlbumKey);
            Albums = files
                .Where(f => f.Tags.Album.Length > 0)
                .GroupBy(f => AlbumKey.For(f.Tags))
                .ToDictionary(g => g.Key, g => (IReadOnlyList<IndexedFile>)g.ToArray());
        }

        public Dictionary<string, Entry[]> ByIsrc { get; }
        public Dictionary<string, Entry[]> ByTitle { get; }
        public Dictionary<string, Entry[]> ByBaseTitle { get; }
        public Dictionary<string, Entry[]> ByAlbum { get; }
        public IReadOnlyDictionary<AlbumKey, IReadOnlyList<IndexedFile>> Albums { get; }

        private static Dictionary<string, Entry[]> Group(IEnumerable<Entry> entries, Func<Entry, string> key) =>
            entries.Where(e => key(e).Length > 0).GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }
}
