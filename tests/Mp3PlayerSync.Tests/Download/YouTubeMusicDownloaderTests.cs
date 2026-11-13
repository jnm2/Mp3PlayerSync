using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeMusicDownloaderTests
{
    private string directory = null!;
    private string cachePath = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        cachePath = Path.Join(directory, "cache", "YouTubeMatches.json");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    private static readonly ExportedTrack UnderPressure = Track("Under Pressure", "Queen;David Bowie", 248, uri: "spotify:track:under-pressure", isrc: "GBUM71029604") with { Album = "Hot Space" };

    private static ExportedTrack Track(string title, string artists, int seconds, string uri = "", string isrc = "") =>
        new(0, uri, title, artists.Split(';'), "", TimeSpan.FromSeconds(seconds), "", null, "", null, isrc, "");

    private TrackDownloadRequest Request(TrackResolution resolution, string name) => new(resolution, Path.Join(directory, name + ".m4a"));

    private YouTubeMusicDownloader CreateDownloader(FakeSource source, int maxParallelDownloads = 3, int maxParallelLookups = 3, int maxParallelLookupsAndDownloads = 6) => new(
        new YouTubeMusicDownloaderOptions { ToolCacheDirectory = directory, ResolutionCachePath = cachePath, MaxParallelDownloads = maxParallelDownloads, MaxParallelLookups = maxParallelLookups, MaxParallelLookupsAndDownloads = maxParallelLookupsAndDownloads },
        source);

    /// <summary>Adds a track whose Songs search has one confident match.</summary>
    private static ExportedTrack AddSong(FakeSource source, string title)
    {
        source.AddSearch($"{title} Artist", [YtDlpSamples.Video(("v" + title.Replace(" ", "")).PadRight(11, '0'), title, title, ["Artist"], "", 200, 1, "Artist")]);
        return Track(title, "Artist", 200, uri: "spotify:track:" + title.Replace(" ", ""));
    }

    private static string[] SongSearches(FakeSource source)
    {
        lock (source) return source.Searches.Where(s => s.Section == YouTubeMusicSearchSection.Songs).Select(s => s.SearchText.Replace(" Artist", "")).ToArray();
    }

    private static async Task WaitUntilIdleAsync(YouTubeMusicDownloader downloader)
    {
        for (var i = 0; downloader.PendingLookupCount > 0; i++)
        {
            if (i > 500) Assert.Fail("Lookups didn't finish.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; !condition(); i++)
        {
            if (i > 500) Assert.Fail("Timed out.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Finds_in_background_returning_known_results_instantly()
    {
        var source = new FakeSource();
        var tracks = new[] { AddSong(source, "A"), AddSong(source, "B"), AddSong(source, "C"), AddSong(source, "D") };
        using var downloader = CreateDownloader(source, maxParallelLookups: 2);
        downloader.Skip(tracks[1]);
        var completed = new List<TrackResolutionProgress>();
        downloader.LookupProgress += (_, p) => { lock (completed) if (p.Stage == TrackResolutionStage.Completed) completed.Add(p); };

        var immediate = downloader.FindInBackground(tracks);

        Assert.That(immediate.Select(r => r?.State), Is.EqualTo(new TrackResolutionState?[] { null, TrackResolutionState.Skipped, null, null }));
        await WaitUntilIdleAsync(downloader);
        await WaitUntilAsync(() => { lock (completed) return completed.Count == 3; });

        Assert.That(completed.Select(p => (p.Track, p.Resolution!.State)), Is.EquivalentTo(new[]
        {
            (tracks[0], TrackResolutionState.Resolved), (tracks[2], TrackResolutionState.Resolved), (tracks[3], TrackResolutionState.Resolved),
        }));
        Assert.That(SongSearches(source), Is.EquivalentTo(new[] { "A", "C", "D" }));
        Assert.That(source.MaxConcurrentSearches, Is.LessThanOrEqualTo(2));
        Assert.That(downloader.FindInBackground(tracks).Select(r => r?.State), Is.All.Not.Null, "Remembered");
    }

    [Test]
    public async Task Stopping_background_finding_drops_queued_lookups_and_keeps_what_was_found()
    {
        var source = new FakeSource();
        var songs = new[] { AddSong(source, "A"), AddSong(source, "B"), AddSong(source, "C") };
        using var downloader = CreateDownloader(source, maxParallelLookups: 1);
        source.CloseGate();

        downloader.FindInBackground(songs);
        await WaitUntilAsync(() => SongSearches(source).Length == 1);
        downloader.FindInBackground([]);
        source.OpenGate();
        await WaitUntilIdleAsync(downloader);

        Assert.That(SongSearches(source), Is.EqualTo(new[] { "A" }), "B and C were dropped");
        Assert.That(downloader.GetCachedResolution(songs[0])?.State, Is.EqualTo(TrackResolutionState.Resolved), "The lookup under way finished and was remembered");
    }

    [Test]
    public async Task Switching_playlists_drops_queued_lookups_for_the_old_one()
    {
        var source = new FakeSource();
        var first = new[] { AddSong(source, "A"), AddSong(source, "B"), AddSong(source, "C") };
        var second = new[] { AddSong(source, "D"), AddSong(source, "C"), AddSong(source, "E") };
        using var downloader = CreateDownloader(source, maxParallelLookups: 1);
        source.CloseGate();

        downloader.FindInBackground(first);
        await WaitUntilAsync(() => SongSearches(source).Length == 1);
        downloader.FindInBackground(second);
        Assert.That(downloader.PendingLookupCount, Is.EqualTo(4), "A (running), D, C, E");
        source.OpenGate();
        await WaitUntilIdleAsync(downloader);

        Assert.That(SongSearches(source), Is.EqualTo(new[] { "A", "D", "C", "E" }));
        Assert.That(downloader.GetCachedResolution(first[0])?.State, Is.EqualTo(TrackResolutionState.Resolved), "The started lookup finished and was remembered");
        Assert.That(downloader.GetCachedResolution(first[1]), Is.Null);
    }

    [Test]
    public async Task Awaited_lookups_go_ahead_of_background_lookups_and_join_ones_in_progress()
    {
        var source = new FakeSource();
        var background = new[] { AddSong(source, "A"), AddSong(source, "B"), AddSong(source, "C") };
        var urgent = AddSong(source, "D");
        using var downloader = CreateDownloader(source, maxParallelLookups: 1);
        source.CloseGate();

        downloader.FindInBackground(background);
        await WaitUntilAsync(() => SongSearches(source).Length == 1);
        var resolving = downloader.ResolveTracksAsync([urgent, background[0]], null, CancellationToken.None);
        source.OpenGate();
        var results = await resolving;
        await WaitUntilIdleAsync(downloader);

        Assert.That(results.Select(r => r.State), Is.All.EqualTo(TrackResolutionState.Resolved));
        Assert.That(results[1].Track, Is.SameAs(background[0]));
        Assert.That(SongSearches(source), Is.EqualTo(new[] { "A", "D", "B", "C" }), "A was joined, not searched again");
    }

    [Test]
    public async Task Downloads_a_background_find_without_searching_or_extracting_again()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);
        var found = new TaskCompletionSource<TrackResolution>();
        downloader.LookupProgress += (_, p) => { if (p.Resolution is { } r) found.TrySetResult(r); };

        downloader.FindInBackground([UnderPressure]);
        var resolution = await found.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var searches = source.Searches.Count;

        var result = await downloader.DownloadTrackAsync(Request(resolution, "track"), null, CancellationToken.None);

        Assert.That(result.Succeeded, result.FailureReason);
        Assert.That(source.Searches, Has.Count.EqualTo(searches));
        Assert.That(source.Extractions, Is.EqualTo(new[] { "BaxnrepwXck" }), "Only the lookup's extraction");
        Assert.That(source.Downloads.Single().FromInfoJson);
    }

    private static FakeSource UnderPressureSource()
    {
        var source = new FakeSource();
        source.AddSearch("Under Pressure Queen David Bowie", YtDlpSamples.UnderPressureSearch);
        return source;
    }

    private static FakeSource LoseYourselfSource(bool includeExplicit = true, bool includeClean = true, bool badges = true)
    {
        var source = new FakeSource();
        var lines = new List<string>();
        if (includeExplicit) lines.Add(YtDlpSamples.Video("explicit001", "Lose Yourself", "Lose Yourself", ["Eminem"], "8 Mile", 320, lines.Count + 1, "Eminem"));
        if (includeClean) lines.Add(YtDlpSamples.Video("clean000001", "Lose Yourself", "Lose Yourself", ["Eminem"], "8 Mile", 320, lines.Count + 1, "Eminem"));
        source.AddSearch("Lose Yourself Eminem", lines);
        if (badges)
        {
            source.ExplicitBadges["explicit001"] = true;
            source.ExplicitBadges["clean000001"] = false;
        }

        return source;
    }

    private static ExportedTrack LoseYourself(bool? isExplicit) =>
        Track("Lose Yourself", "Eminem", 320, uri: "spotify:track:lose-yourself") with { Album = "8 Mile", Explicit = isExplicit };

    [TestCase(true, "explicit001")]
    [TestCase(false, "clean000001")]
    public async Task Picks_the_playlists_explicit_or_clean_version(bool isExplicit, string expectedVideoId)
    {
        var source = LoseYourselfSource();
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit)], null, CancellationToken.None)).Single();

        Assert.That(resolution.Video?.VideoId, Is.EqualTo(expectedVideoId), resolution.Reason);
        Assert.That(source.Extractions, Is.EqualTo(new[] { expectedVideoId }), "The right version is verified first");
    }

    [Test]
    public async Task Sends_to_review_when_only_the_other_version_is_found()
    {
        using var downloader = CreateDownloader(LoseYourselfSource(includeClean: false));

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit: false)], null, CancellationToken.None)).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.NeedsReview));
        Assert.That(resolution.Reason, Is.EqualTo("YouTube Music only seems to have the explicit version; your playlist has the clean one."));
        Assert.That(resolution.Candidates.Single().Score.Explicit, Is.EqualTo(ExplicitMatch.Mismatch));
        Assert.That(resolution.Candidates.Single().Video.IsExplicit, Is.True);
    }

    [Test]
    public async Task Sends_to_review_when_the_versions_cant_be_told_apart()
    {
        var source = LoseYourselfSource(badges: false);
        source.ExplicitBadges["clean000001"] = false;
        source.ExplicitBadges.Remove("explicit001");
        // Only the clean one is badged as such; the first result's version is unknown.
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit: true)], null, CancellationToken.None)).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.NeedsReview));
        Assert.That(resolution.Reason, Is.EqualTo("YouTube Music has both versions, and it isn't clear which one is the explicit version in your playlist."));
    }

    [Test]
    public async Task Ignores_versions_when_nothing_says_which_is_which()
    {
        using var downloader = CreateDownloader(LoseYourselfSource(badges: false));

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit: true)], null, CancellationToken.None)).Single();

        Assert.That(resolution.Video?.VideoId, Is.EqualTo("explicit001"), "Same as before: the first confident match");
    }

    [Test]
    public async Task Remembers_explicit_badges_of_review_candidates()
    {
        using (var downloader = CreateDownloader(LoseYourselfSource(includeClean: false)))
            await downloader.ResolveTracksAsync([LoseYourself(isExplicit: false)], null, CancellationToken.None);

        using var reloaded = CreateDownloader(new FakeSource());
        var cached = reloaded.GetCachedResolution(LoseYourself(isExplicit: false))!;

        Assert.That(cached.Candidates.Single().Video.IsExplicit, Is.True);
    }

    [Test]
    public async Task Resolves_a_confident_match_by_extracting_only_the_best_flat_result()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);
        var stages = new List<TrackResolutionStage>();

        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], new SynchronousProgress<TrackResolutionProgress>(p => stages.Add(p.Stage)), CancellationToken.None)).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.Resolved));
        Assert.That(resolution.FromCache, Is.False);
        var video = resolution.Video!;
        Assert.That((video.VideoId, video.Title, video.Album, video.Duration, video.Source), Is.EqualTo(("BaxnrepwXck", "Under Pressure", "Hot Space", (TimeSpan?)TimeSpan.FromSeconds(248), ResolutionSource.Automatic)));
        Assert.That(video.Artists, Is.EqualTo(new[] { "Queen", "David Bowie" }));
        Assert.That(video.Url, Is.EqualTo("https://music.youtube.com/watch?v=BaxnrepwXck"));
        Assert.That(source.Extractions, Is.EqualTo(new[] { "BaxnrepwXck" }));
        Assert.That(stages, Is.EqualTo(new[] { TrackResolutionStage.Queued, TrackResolutionStage.Searching, TrackResolutionStage.Completed }));
    }

    [Test]
    public async Task Compares_a_good_match_without_album_match_against_results_with_equally_good_titles()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([UnderPressure with { Album = "Greatest Hits II" }], null, CancellationToken.None)).Single();

        Assert.That(resolution.Video?.VideoId, Is.EqualTo("BaxnrepwXck"));
        Assert.That(source.Extractions, Is.EqualTo(new[] { "BaxnrepwXck", "nbwn21TU4ec" }), "Not the live versions or other songs");
    }

    [Test]
    public async Task Verifies_the_flat_winner_and_moves_on_when_full_metadata_rejects_it()
    {
        var source = new FakeSource();
        source.AddSearch("Song Artist",
        [
            YtDlpSamples.Video("cover000001", "Song", "Song", ["Tribute Band"], "", 200, 1, "Tribute Band - Topic"),
            YtDlpSamples.Video("original001", "Song", "Song", ["Artist"], "", 201, 2, "Artist - Topic"),
        ]);
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([Track("Song", "Artist", 200)], null, CancellationToken.None)).Single();

        Assert.That(resolution.Video?.VideoId, Is.EqualTo("original001"));
        Assert.That(source.Extractions, Is.EqualTo(new[] { "cover000001", "original001" }));
    }

    [Test]
    public async Task Remembers_resolutions_by_uri_and_then_isrc()
    {
        using (var downloader = CreateDownloader(UnderPressureSource()))
            await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None);

        var source = new FakeSource();
        using var reloaded = CreateDownloader(source);
        var sameRecordingOnACompilation = UnderPressure with { Uri = "spotify:track:compilation" };

        var resolutions = await reloaded.ResolveTracksAsync([UnderPressure, sameRecordingOnACompilation], null, CancellationToken.None);

        Assert.That(resolutions.Select(r => (r.State, r.FromCache, r.Video?.VideoId, r.Video?.Source)), Is.EqualTo(new[]
        {
            (TrackResolutionState.Resolved, true, "BaxnrepwXck", ResolutionSource.Automatic),
            (TrackResolutionState.Resolved, true, "BaxnrepwXck", ResolutionSource.Automatic),
        }));
        Assert.That(resolutions[0].Video!.Title, Is.EqualTo("Under Pressure"));
        Assert.That(source.Searches, Is.Empty);
    }

    [Test]
    public async Task Offers_candidates_for_review_when_nothing_matches_and_remembers_them_for_a_while()
    {
        var source = new FakeSource();
        source.AddSearch("Song Artist",
        [
            YtDlpSamples.Video("other000001", "Other Song", "Other Song", ["Someone Else"], "", 100, 1, "Someone Else"),
            YtDlpSamples.Video("cover000001", "Song", "Song", ["Tribute Band"], "", 200, 2, "Tribute Band"),
        ]);
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        TrackResolution resolution;

        using (var downloader = CreateDownloader(source))
            resolution = (await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.NeedsReview));
        Assert.That(resolution.NeedsReview);
        Assert.That(resolution.Video, Is.Null);
        Assert.That(resolution.Candidates.Select(c => c.Video.Id), Is.EqualTo(new[] { "cover000001", "other000001" }));
        Assert.That(resolution.Candidates[0].Video.Artists, Is.EqualTo(new[] { "Tribute Band" }));
        Assert.That(resolution.Candidates[0].HasAcceptableAudio);
        Assert.That(resolution.Reason, Is.EqualTo("No confident match on YouTube Music. The closest result was \"Song\" by Tribute Band (3:20)."));

        using (var reloaded = CreateDownloader(source))
        {
            var remembered = (await reloaded.ResolveTracksAsync([track], null, CancellationToken.None)).Single();

            Assert.That((remembered.State, remembered.FromCache, remembered.Reason), Is.EqualTo((TrackResolutionState.NeedsReview, true, resolution.Reason)));
            Assert.That(remembered.Candidates.Select(c => (c.Video.Id, c.Video.Url, c.Video.Album, c.Video.Duration, c.Score, c.Format)),
                Is.EqualTo(resolution.Candidates.Select(c => (c.Video.Id, c.Video.Url, c.Video.Album, c.Video.Duration, c.Score, c.Format))));
            Assert.That(remembered.Candidates[0].Video.Artists, Is.EqualTo(new[] { "Tribute Band" }));
            Assert.That(source.Searches, Has.Count.EqualTo(2), "Not looked up again");
        }

        using var expired = new YouTubeMusicDownloader(
            new YouTubeMusicDownloaderOptions { ToolCacheDirectory = directory, ResolutionCachePath = cachePath, ReviewCacheLifetime = TimeSpan.Zero },
            source);
        Assert.That((await expired.ResolveTracksAsync([track], null, CancellationToken.None)).Single().FromCache, Is.False);
        Assert.That(source.Searches, Has.Count.EqualTo(4), "Looked up again after the review result expired");
    }

    [Test]
    public async Task Retry_bypasses_the_remembered_result()
    {
        var source = new FakeSource();
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using var downloader = CreateDownloader(source);
        Assert.That((await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).Single().State, Is.EqualTo(TrackResolutionState.NeedsReview));

        source.AddSearch("Song Artist", [YtDlpSamples.Video("song0000001", "Song", "Song", ["Artist"], "", 200, 1, "Artist")]);
        Assert.That((await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).Single().FromCache, Is.True);

        var retried = await downloader.RetryAsync(track, CancellationToken.None);

        Assert.That((retried.State, retried.Video?.VideoId, retried.FromCache), Is.EqualTo((TrackResolutionState.Resolved, "song0000001", false)));
        Assert.That(downloader.GetCachedResolution(track)?.Video?.VideoId, Is.EqualTo("song0000001"));
    }

    [Test]
    public async Task Match_without_acceptable_audio_is_unavailable()
    {
        var source = new FakeSource();
        source.AddSearch("Song Artist", [YtDlpSamples.Video("opusonly001", "Song", "Song", ["Artist"], "", 200, 1, "Artist", YtDlpSamples.OpusOnlyFormats)]);
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([Track("Song", "Artist", 200)], null, CancellationToken.None)).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.Unavailable));
        Assert.That(resolution.NeedsReview);
        Assert.That(resolution.Reason, Does.Contain("no AAC"));
        Assert.That(resolution.Candidates.Single().FormatProblem, Is.EqualTo(resolution.Reason));
    }

    [Test]
    public async Task Search_failure_is_remembered_for_this_run_only()
    {
        var source = new FakeSource { SearchError = "ERROR: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>" };
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.LookupFailed));
        Assert.That(resolution.Reason, Does.Contain("getaddrinfo failed"));
        Assert.That(downloader.GetCachedResolution(UnderPressure)?.State, Is.EqualTo(TrackResolutionState.LookupFailed));

        downloader.Dispose();
        using var reloaded = CreateDownloader(UnderPressureSource());
        Assert.That(reloaded.GetCachedResolution(UnderPressure), Is.Null);
    }

    [Test]
    public async Task Manual_choice_is_validated_and_remembered()
    {
        var source = new FakeSource();
        source.AddVideo(YtDlpSamples.Video("chosen00001", "Song (Live)", "Song (Live)", ["Artist"], "", 250, 1, "Artist"));
        source.AddVideo(YtDlpSamples.Video("opusonly001", "Song", "Song", ["Artist"], "", 200, 1, "Artist", YtDlpSamples.OpusOnlyFormats));
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");

        using (var downloader = CreateDownloader(source))
        {
            Assert.That((await downloader.ChooseVideoAsync(track, "https://music.youtube.com/playlist?list=OLAK5uy_abc", CancellationToken.None)).Error, Does.Contain("playlist"));
            Assert.That((await downloader.ChooseVideoAsync(track, "https://youtu.be/missing0001", CancellationToken.None)).Error, Does.StartWith("YouTube couldn't load that video."));
            Assert.That((await downloader.ChooseVideoAsync(track, "opusonly001", CancellationToken.None)).Error, Does.Contain("no AAC"));

            var chosen = await downloader.ChooseVideoAsync(track, "https://www.youtube.com/watch?v=chosen00001&list=RDchosen00001", CancellationToken.None);

            Assert.That(chosen.Error, Is.Null);
            Assert.That(chosen.Resolution!.State, Is.EqualTo(TrackResolutionState.Resolved));
            Assert.That(chosen.Resolution.Video!.VideoId, Is.EqualTo("chosen00001"));
            Assert.That(chosen.Resolution.Video.Source, Is.EqualTo(ResolutionSource.Manual));
        }

        using var reloaded = CreateDownloader(new FakeSource());
        var remembered = (await reloaded.ResolveTracksAsync([track], null, CancellationToken.None)).Single();
        Assert.That((remembered.Video?.VideoId, remembered.Video?.Source), Is.EqualTo(("chosen00001", ResolutionSource.Manual)));
    }

    [Test]
    public async Task Skips_are_remembered_until_forgotten()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);

        Assert.That(downloader.Skip(UnderPressure).State, Is.EqualTo(TrackResolutionState.Skipped));

        using (var reloaded = CreateDownloader(new FakeSource()))
        {
            var skipped = (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();
            Assert.That((skipped.State, skipped.FromCache), Is.EqualTo((TrackResolutionState.Skipped, true)));
        }

        Assert.That(downloader.Forget(UnderPressure));
        var resolved = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();
        Assert.That((resolved.State, resolved.FromCache), Is.EqualTo((TrackResolutionState.Resolved, false)));
    }

    [Test]
    public async Task Downloads_resolved_track_as_progressive_aac_without_extracting_again()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);
        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();
        var request = Request(resolution, "track");
        var stages = new List<TrackDownloadStage>();

        var result = await downloader.DownloadTrackAsync(request, p => stages.Add(p.Stage), CancellationToken.None);

        Assert.That(result.FailureReason, Is.Null);
        Assert.That(result.Format!.FormatId, Is.EqualTo("140"));
        Assert.That(result.Audio!.IsAac);
        Assert.That(source.Extractions, Has.Count.EqualTo(1));
        Assert.That(source.Downloads, Is.EqualTo(new[] { ("BaxnrepwXck", true, "140/bestaudio[acodec^=mp4a][abr>=96][protocol=https]") }));
        Assert.That(M4aRemuxer.ReadAudioInfo(request.DestinationPath).IsAac);
        Assert.That(stages.Distinct(), Is.EqualTo(new[] { TrackDownloadStage.Queued, TrackDownloadStage.Downloading, TrackDownloadStage.Remuxing, TrackDownloadStage.Succeeded }));
        Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { request.DestinationPath }), "No intermediate files are left behind");
    }

    [Test]
    public async Task Cached_resolution_is_checked_before_downloading_and_forgotten_if_the_video_is_gone()
    {
        using (var downloader = CreateDownloader(UnderPressureSource()))
            await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None);

        var source = new FakeSource();
        using var reloaded = CreateDownloader(source);
        var cached = (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();

        var result = await reloaded.DownloadTrackAsync(Request(cached, "track"), null, CancellationToken.None);

        Assert.That(result.NeedsReview);
        Assert.That(result.FailureReason, Does.StartWith("The YouTube video is no longer available."));
        Assert.That(source.Downloads, Is.Empty);

        using var afterward = CreateDownloader(UnderPressureSource());
        Assert.That((await afterward.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single().FromCache, Is.False);
    }

    [TestCase("ERROR: [youtube] chosen00001: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.")]
    [TestCase("ERROR: [youtube] chosen00001: Video unavailable. This content isn't available, try again later.")]
    [TestCase("ERROR: [youtube] chosen00001: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed> (caused by TransportError)")]
    [TestCase("ERROR: [youtube] chosen00001: Unable to download webpage: HTTP Error 429: Too Many Requests")]
    public async Task Manual_choice_survives_a_transient_failure_while_downloading(string transientError)
    {
        var source = new FakeSource();
        source.AddVideo(YtDlpSamples.Video("chosen00001", "Song", "Song", ["Artist"], "", 200, 1, "Artist"));
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using (var downloader = CreateDownloader(source))
            Assert.That((await downloader.ChooseVideoAsync(track, "chosen00001", CancellationToken.None)).Succeeded);

        source.ExtractError = transientError;
        using (var reloaded = CreateDownloader(source))
        {
            var cached = reloaded.GetCachedResolution(track)!;
            var result = await reloaded.DownloadTrackAsync(Request(cached, "track"), null, CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.NeedsReview, Is.False);
            Assert.That(result.FailureReason, Does.StartWith("Couldn't reach YouTube; try again later.").Or.EqualTo(YouTubeMusicDownloader.YouTubeBusyMessage));
        }

        source.ExtractError = null;
        using var afterward = CreateDownloader(source);
        var kept = afterward.GetCachedResolution(track);
        Assert.That((kept?.State, kept?.Video?.VideoId, kept?.Video?.Source), Is.EqualTo((TrackResolutionState.Resolved, "chosen00001", ResolutionSource.Manual)));
        Assert.That((await afterward.DownloadTrackAsync(Request(kept!, "track"), null, CancellationToken.None)).Succeeded, "Downloads once YouTube is reachable again");
    }

    [Test]
    public async Task Download_names_the_cover_art_of_the_video_it_downloaded()
    {
        var source = new FakeSource();
        source.AddVideo(YtDlpSamples.Video("chosen00001", "Song", "Song", ["Artist"], "", 200, 1, "Artist")
            .Replace("\"_type\"", "\"thumbnails\": [{\"url\": \"https://yt3.googleusercontent.com/art=w544-h544-l90-rj\", \"width\": 544, \"height\": 544}], \"_type\"", StringComparison.Ordinal));
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using var downloader = CreateDownloader(source);
        var chosen = await downloader.ChooseVideoAsync(track, "chosen00001", CancellationToken.None);

        var result = await downloader.DownloadTrackAsync(Request(chosen.Resolution!, "track"), null, CancellationToken.None);

        Assert.That(result.Succeeded, result.FailureReason);
        Assert.That(result.CoverArtUrl, Is.EqualTo("https://yt3.googleusercontent.com/art=w500-h500-l90-rj"));
    }

    [Test]
    public async Task Manual_choice_is_forgotten_when_the_video_is_confirmed_gone()
    {
        var source = new FakeSource();
        source.AddVideo(YtDlpSamples.Video("chosen00001", "Song", "Song", ["Artist"], "", 200, 1, "Artist"));
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using (var downloader = CreateDownloader(source))
            Assert.That((await downloader.ChooseVideoAsync(track, "chosen00001", CancellationToken.None)).Succeeded);

        source.ExtractError = "ERROR: [youtube] chosen00001: Private video. Sign in if you've been granted access to this video.";
        using var reloaded = CreateDownloader(source);
        var result = await reloaded.DownloadTrackAsync(Request(reloaded.GetCachedResolution(track)!, "track"), null, CancellationToken.None);

        Assert.That(result.NeedsReview);
        Assert.That(result.FailureReason, Does.StartWith("The YouTube video is no longer available."));
        Assert.That(reloaded.GetCachedResolution(track), Is.Null);
    }

    [Test]
    public async Task Throttled_lookup_is_a_transient_failure_and_not_a_review_result()
    {
        var source = UnderPressureSource();
        source.ExtractError = "ERROR: [youtube] BaxnrepwXck: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.";

        using (var downloader = CreateDownloader(source))
        {
            var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();

            Assert.That((resolution.State, resolution.IsThrottled, resolution.Reason), Is.EqualTo((TrackResolutionState.LookupFailed, true, YouTubeMusicDownloader.YouTubeBusyMessage)));
            Assert.That(resolution.Candidates, Is.Empty);
            Assert.That(downloader.GetCachedResolution(UnderPressure)?.IsThrottled, Is.True, "Remembered briefly so it isn't retried right away");
        }

        source.ExtractError = null;
        using var reloaded = CreateDownloader(source);
        Assert.That(reloaded.GetCachedResolution(UnderPressure), Is.Null, "Not saved: looked up again next run");
        Assert.That((await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single().State, Is.EqualTo(TrackResolutionState.Resolved));
    }

    [Test]
    public async Task Throttled_download_keeps_the_match_and_says_youtube_is_busy()
    {
        using (var downloader = CreateDownloader(UnderPressureSource()))
            await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None);

        var source = UnderPressureSource();
        source.ExtractError = "WARNING: [youtube] BaxnrepwXck: Unable to download webpage: HTTP Error 429: Too Many Requests";
        using var reloaded = CreateDownloader(source);

        var result = await reloaded.DownloadTrackAsync(Request(reloaded.GetCachedResolution(UnderPressure)!, "track"), null, CancellationToken.None);

        Assert.That((result.IsThrottled, result.NeedsReview, result.FailureReason), Is.EqualTo((true, false, YouTubeMusicDownloader.YouTubeBusyMessage)));
        Assert.That(reloaded.GetCachedResolution(UnderPressure)?.Video?.VideoId, Is.EqualTo("BaxnrepwXck"));
    }

    [Test]
    public async Task Throttled_manual_choice_says_youtube_is_busy()
    {
        var source = new FakeSource { ExtractError = "ERROR: [youtube] chosen00001: HTTP Error 429: Too Many Requests" };
        using var downloader = CreateDownloader(source);

        var result = await downloader.ChooseVideoAsync(Track("Song", "Artist", 200, uri: "spotify:track:song"), "chosen00001", CancellationToken.None);

        Assert.That((result.Succeeded, result.IsThrottled, result.Error), Is.EqualTo((false, true, YouTubeMusicDownloader.YouTubeBusyMessage)));
    }

    [TestCase("WARNING: [youtube] x: Unable to download webpage: HTTP Error 429: Too Many Requests (caused by <HTTPError 429: Too Many Requests>)", true)]
    [TestCase("ERROR: [youtube] x: Sign in to confirm you’re not a bot. This helps protect our community.", true)]
    [TestCase("ERROR: [youtube] x: Video unavailable. This content isn't available, try again later.", true)]
    [TestCase("ERROR: [youtube] x: Video unavailable", false)]
    [TestCase("ERROR: unable to download video data: HTTP Error 403: Forbidden", false)]
    [TestCase("ERROR: [youtube] x: Sign in to confirm your age.", false)]
    [TestCase("ERROR: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>", false)]
    public void Classifies_throttling(string message, bool isThrottled)
    {
        Assert.That(YtDlpException.IsThrottledMessage(message), Is.EqualTo(isThrottled));
    }

    [TestCase("ERROR: [youtube] x: Video unavailable", true)]
    [TestCase("ERROR: [youtube] x: Video unavailable. This video has been removed by the uploader", true)]
    [TestCase("ERROR: [youtube] x: Private video. Sign in if you've been granted access to this video", true)]
    [TestCase("ERROR: [youtube] x: The uploader has not made this video available in your country", true)]
    [TestCase("ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.", true)]
    [TestCase("ERROR: [youtube] x: Video unavailable. This content isn't available, try again later.", false)]
    [TestCase("ERROR: [youtube] x: Sign in to confirm you’re not a bot.", false)]
    [TestCase("ERROR: Unable to download webpage: HTTP Error 503: Service Unavailable", false)]
    [TestCase("yt-dlp exited with code 1.", false)]
    public void Classifies_gone_videos(string message, bool isGone)
    {
        Assert.That(YtDlpException.IsVideoGoneMessage(message), Is.EqualTo(isGone));
    }

    [Test]
    public async Task Cached_resolution_downloads_after_checking_the_video()
    {
        using (var downloader = CreateDownloader(UnderPressureSource()))
            await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None);

        var source = UnderPressureSource();
        using var reloaded = CreateDownloader(source);
        var cached = (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();
        var stages = new List<TrackDownloadStage>();

        var result = await reloaded.DownloadTrackAsync(Request(cached, "track"), p => stages.Add(p.Stage), CancellationToken.None);

        Assert.That(result.Succeeded, result.FailureReason);
        Assert.That(source.Extractions, Is.EqualTo(new[] { "BaxnrepwXck" }));
        Assert.That(stages, Does.Contain(TrackDownloadStage.Preparing));
    }

    [Test]
    public async Task Rejects_and_deletes_a_download_that_is_not_aac()
    {
        var source = UnderPressureSource();
        source.DownloadedFile = Mp4TestFiles.CreateFragmented([[100, 100]], sampleEntryType: "Opus");
        using var downloader = CreateDownloader(source);
        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();

        var result = await downloader.DownloadTrackAsync(Request(resolution, "track"), null, CancellationToken.None);

        Assert.That(result.FailureReason, Is.EqualTo("YouTube sent Opus audio instead of AAC."));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public async Task Refuses_to_download_unresolved_tracks()
    {
        using var downloader = CreateDownloader(new FakeSource());

        var result = await downloader.DownloadTrackAsync(Request(downloader.Skip(UnderPressure), "track"), null, CancellationToken.None);

        Assert.That(result.FailureReason, Is.EqualTo("The track hasn't been resolved to a YouTube video."));
    }

    [Test]
    public async Task Track_requested_without_resolving_is_resolved_when_downloading()
    {
        var source = UnderPressureSource();
        source.AddSearch("Song Artist", [YtDlpSamples.Video("cover000001", "Song", "Song", ["Tribute Band"], "", 200, 1, "Tribute Band")]);
        using var downloader = CreateDownloader(source);

        var results = await downloader.DownloadTracksAsync(
            [new TrackDownloadRequest(UnderPressure, Path.Join(directory, "a.m4a")), new TrackDownloadRequest(Track("Song", "Artist", 200), Path.Join(directory, "b.m4a"))],
            null,
            CancellationToken.None);

        Assert.That(results[0].Succeeded, results[0].FailureReason);
        Assert.That(results[1].FailureReason, Does.StartWith("No confident match"));
    }

    [Test]
    public async Task Downloads_in_parallel_up_to_the_limit_and_isolates_failures()
    {
        var source = new FakeSource { DownloadDelay = TimeSpan.FromMilliseconds(100) };
        var tracks = new List<ExportedTrack>();
        for (var i = 0; i < 6; i++)
        {
            var title = $"Song {i}";
            source.AddSearch($"{title} Artist", [YtDlpSamples.Video($"video{i:000000}", title, title, ["Artist"], "", 200, 1, "Artist")]);
            tracks.Add(Track(title, "Artist", 200));
        }

        source.FailingVideoIds.Add("video000002");
        using var downloader = CreateDownloader(source, maxParallelDownloads: 2);
        var resolutions = await downloader.ResolveTracksAsync(tracks, null, CancellationToken.None);
        var requests = resolutions.Select((r, i) => Request(r, $"track{i}")).ToList();

        var results = await downloader.DownloadTracksAsync(requests, null, CancellationToken.None);

        Assert.That(results.Select(r => r.Request), Is.EqualTo(requests));
        Assert.That(results.Select(r => r.Succeeded), Is.EqualTo(new[] { true, true, false, true, true, true }));
        Assert.That(results[2].FailureReason, Is.EqualTo("ERROR: [youtube] video000002: Video unavailable"));
        Assert.That(source.MaxConcurrentDownloads, Is.EqualTo(2));
    }

    [Test]
    public async Task A_throttled_lookup_waits_out_the_cool_down_instead_of_failing()
    {
        var source = new FakeSource { ThrottledSearches = 1 };
        var track = AddSong(source, "A");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var checks = 0;
        using var traffic = new YouTubeTraffic(Path.Join(directory, "YouTubeTraffic.json"), _ => { Interlocked.Increment(ref checks); return Task.FromResult(true); }, clock);
        using var downloader = new YouTubeMusicDownloader(
            new YouTubeMusicDownloaderOptions { ToolCacheDirectory = directory, ResolutionCachePath = cachePath, Traffic = traffic },
            source);

        var lookup = downloader.ResolveTracksAsync([track], null, CancellationToken.None);
        await WaitUntilAsync(() => traffic.BusyUntil is not null);
        await Task.Delay(100);
        Assert.That(lookup.IsCompleted, Is.False);
        Assert.That(downloader.GetCachedResolution(track), Is.Null, "Nothing is remembered about the track while YouTube is busy.");

        clock.Advance(YouTubeTraffic.Cooldowns[0]);
        var resolution = (await lookup.WaitAsync(TimeSpan.FromSeconds(30))).Single();

        Assert.That(resolution.State, Is.EqualTo(TrackResolutionState.Resolved));
        Assert.That(checks, Is.EqualTo(1));
    }

    [Test]
    public async Task Choosing_a_video_during_a_cool_down_says_YouTube_is_busy_at_once()
    {
        var source = new FakeSource { ThrottledSearches = 1 };
        var track = AddSong(source, "A");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var traffic = new YouTubeTraffic(null, _ => Task.FromResult(true), clock);
        using var downloader = new YouTubeMusicDownloader(
            new YouTubeMusicDownloaderOptions { ToolCacheDirectory = directory, ResolutionCachePath = cachePath, Traffic = traffic },
            source);
        _ = downloader.ResolveTracksAsync([track], null, CancellationToken.None);
        await WaitUntilAsync(() => traffic.BusyUntil is not null);

        var choice = await downloader.ChooseVideoAsync(track, "BaxnrepwXck", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That((choice.Resolution, choice.Error, choice.IsThrottled), Is.EqualTo(((TrackResolution?)null, "YouTube is busy; continuing in 60 min", true)));
    }

    [Test]
    public async Task Lookups_and_downloads_share_one_limit()
    {
        var source = new FakeSource { DownloadDelay = TimeSpan.FromMilliseconds(50) };
        var toDownload = new[] { AddSong(source, "A"), AddSong(source, "B") };
        var toFind = new[] { AddSong(source, "C"), AddSong(source, "D") };
        using var downloader = CreateDownloader(source, maxParallelDownloads: 2, maxParallelLookups: 2, maxParallelLookupsAndDownloads: 2);
        var resolutions = await downloader.ResolveTracksAsync(toDownload, null, CancellationToken.None);

        source.CloseGate();
        downloader.FindInBackground(toFind);
        await WaitUntilAsync(() => source.ConcurrentSearches == 2);
        var downloads = downloader.DownloadTracksAsync(resolutions.Select((r, i) => Request(r, $"track{i}")).ToList(), null, CancellationToken.None);
        await Task.Delay(100);
        lock (source) Assert.That(source.Downloads, Is.Empty, "Downloads started while both slots were taken by lookups.");

        source.OpenGate();
        var results = await downloads;

        Assert.That(results.Select(r => r.Succeeded), Is.All.True);
        Assert.That(source.MaxConcurrentSearchesAndDownloads, Is.EqualTo(2));
    }

    [Test]
    public async Task Cancellation_throws_and_cleans_up()
    {
        var source = UnderPressureSource();
        source.DownloadDelay = Timeout.InfiniteTimeSpan;
        using var downloader = CreateDownloader(source);
        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).Single();
        using var cancellation = new CancellationTokenSource();

        var task = downloader.DownloadTrackAsync(Request(resolution, "track"), p =>
        {
            if (p.Stage == TrackDownloadStage.Downloading) cancellation.CancelAfter(50);
        }, cancellation.Token);

        Assert.That(async () => await task, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this) report(value);
        }
    }

    private sealed class FakeSource : IYouTubeMusicSource
    {
        private readonly Dictionary<string, List<string>> searches = [];
        private readonly Dictionary<string, string> videos = [];
        private int concurrentDownloads;

        public HashSet<string> FailingVideoIds { get; } = [];
        public byte[] DownloadedFile { get; set; } = Mp4TestFiles.CreateFragmented([[371, 422], [396]]);
        public TimeSpan DownloadDelay { get; set; }
        public string? SearchError { get; init; }

        /// <summary>YouTube Music's explicit badge per video ID, as read from the search page.</summary>
        public Dictionary<string, bool> ExplicitBadges { get; } = [];

        /// <summary>When set, extractions fail with this yt-dlp error.</summary>
        public string? ExtractError { get; set; }
        public List<(string SearchText, YouTubeMusicSearchSection Section)> Searches { get; } = [];
        public List<string> Extractions { get; } = [];
        public List<(string VideoId, bool FromInfoJson, string FormatSelector)> Downloads { get; } = [];
        public int MaxConcurrentDownloads { get; private set; }

        /// <summary>Adds results to the Songs section of a search.</summary>
        public void AddSearch(string searchText, IEnumerable<string> videoJsonLines)
        {
            foreach (var line in videoJsonLines)
            {
                AddVideo(line);
                (searches.TryGetValue(searchText, out var list) ? list : searches[searchText] = []).Add(line);
            }
        }

        public void AddVideo(string videoJson) => videos[YouTubeVideoInfo.Parse(videoJson).Id] = videoJson;

        public Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string searchText, YouTubeMusicSearchSection section, int maxResults, CancellationToken cancellationToken)
        {
            lock (this)
            {
                Searches.Add((searchText, section));
                MaxConcurrentSearches = Math.Max(MaxConcurrentSearches, ++concurrentSearches);
                MaxConcurrentSearchesAndDownloads = Math.Max(MaxConcurrentSearchesAndDownloads, ++concurrentSearchesAndDownloads);
            }

            return SearchCoreAsync(searchText, section, maxResults, cancellationToken);
        }

        public int MaxConcurrentSearches { get; private set; }

        /// <summary>How many searches YouTube refuses with a 429 before taking them again.</summary>
        public int ThrottledSearches { get; set; }
        public int ConcurrentSearches { get { lock (this) return concurrentSearches; } }
        private int concurrentSearches;

        public int MaxConcurrentSearchesAndDownloads { get; private set; }
        private int concurrentSearchesAndDownloads;
        private TaskCompletionSource? closedGate;

        /// <summary>Makes searches wait until <see cref="OpenGate"/>.</summary>
        public void CloseGate() => closedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OpenGate() => closedGate?.TrySetResult();

        private async Task<IReadOnlyList<YouTubeVideoInfo>> SearchCoreAsync(string searchText, YouTubeMusicSearchSection section, int maxResults, CancellationToken cancellationToken)
        {
            try
            {
                if (closedGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
                if (SearchError is not null) throw new YtDlpException(SearchError, 1);
                lock (this)
                {
                    if (ThrottledSearches > 0)
                    {
                        ThrottledSearches--;
                        throw new YtDlpException("ERROR: [youtube:search] HTTP Error 429: Too Many Requests", 1);
                    }
                }

                var lines = section == YouTubeMusicSearchSection.Songs ? searches.GetValueOrDefault(searchText) ?? [] : [];

                // Flat results only have an ID, title, and rank.
                return lines.Take(maxResults)
                    .Select(YouTubeVideoInfo.Parse)
                    .Select(v => new YouTubeVideoInfo(v.Id, v.Url, v.Title, null, [], null, null, null, v.SearchRank, []) { IsExplicit = ExplicitBadges.TryGetValue(v.Id, out var e) ? e : null })
                    .ToList();
            }
            finally
            {
                lock (this)
                {
                    concurrentSearches--;
                    concurrentSearchesAndDownloads--;
                }
            }
        }

        public Task<IReadOnlyList<YouTubeVideoInfo>> ExtractAsync(IEnumerable<string> videoUrls, CancellationToken cancellationToken)
        {
            if (ExtractError is not null) throw new YtDlpException(ExtractError, 1);

            var results = new List<YouTubeVideoInfo>();
            foreach (var url in videoUrls)
            {
                var id = url[(url.IndexOf("v=", StringComparison.Ordinal) + 2)..];
                lock (this) Extractions.Add(id);

                // A single-video extraction has no playlist_index.
                if (videos.TryGetValue(id, out var json))
                    results.Add(YouTubeVideoInfo.Parse(json.Replace("\"playlist_index\"", "\"unused_index\"", StringComparison.Ordinal)));
            }

            if (results.Count == 0) throw new YtDlpException("ERROR: [youtube] Video unavailable", 1);
            return Task.FromResult<IReadOnlyList<YouTubeVideoInfo>>(results);
        }

        public async Task DownloadFormatAsync(YouTubeVideoInfo video, string formatSelector, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            lock (this)
            {
                Downloads.Add((video.Id, video.IsFullyExtracted && video.InfoJson is not null, formatSelector));
                MaxConcurrentDownloads = Math.Max(MaxConcurrentDownloads, ++concurrentDownloads);
                MaxConcurrentSearchesAndDownloads = Math.Max(MaxConcurrentSearchesAndDownloads, ++concurrentSearchesAndDownloads);
            }

            try
            {
                await File.WriteAllBytesAsync(outputPath, DownloadedFile.AsMemory(0, DownloadedFile.Length / 2), cancellationToken);
                progress?.Report(0.5);
                await Task.Delay(DownloadDelay, cancellationToken);

                if (FailingVideoIds.Contains(video.Id))
                    throw new YtDlpException($"ERROR: [youtube] {video.Id}: Video unavailable", 1);

                await File.WriteAllBytesAsync(outputPath, DownloadedFile, cancellationToken);
                progress?.Report(1);
            }
            finally
            {
                lock (this)
                {
                    concurrentDownloads--;
                    concurrentSearchesAndDownloads--;
                }
            }
        }
    }
}
