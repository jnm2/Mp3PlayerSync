using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Download;

namespace Mp3PlayerSync.Tests.Download;

public class YouTubeMusicDownloaderTests
{
    private string directory = null!;
    private string cachePath = null!;

    [Before(Test)]
    public void SetUp()
    {
        directory = Path.Join(Path.GetTempPath(), "Mp3PlayerSync.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        cachePath = Path.Join(directory, "cache", "YouTubeMatches.json");
    }

    [After(Test)]
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
            if (i > 500) Fail.Test("Lookups didn't finish.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; !condition(); i++)
        {
            if (i > 500) Fail.Test("Timed out.");
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

        immediate.Select(r => r?.State).ShouldBe(new TrackResolutionState?[] { null, TrackResolutionState.Skipped, null, null });
        await WaitUntilIdleAsync(downloader);
        await WaitUntilAsync(() => { lock (completed) return completed.Count == 3; });

        completed.Select(p => (p.Track, p.Resolution!.State)).ShouldBe(new[]
        {
            (tracks[0], TrackResolutionState.Resolved), (tracks[2], TrackResolutionState.Resolved), (tracks[3], TrackResolutionState.Resolved),
        }, ignoreOrder: true);
        SongSearches(source).ShouldBe(new[] { "A", "C", "D" }, ignoreOrder: true);
        source.MaxConcurrentSearches.ShouldBeLessThanOrEqualTo(2);
        downloader.FindInBackground(tracks).Select(r => r?.State).ShouldAllBe(state => state != null, customMessage: "Remembered");
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

        SongSearches(source).ShouldBe(new[] { "A" }, customMessage: "B and C were dropped");
        downloader.GetCachedResolution(songs[0]).ShouldNotBeNull().State.ShouldBe(TrackResolutionState.Resolved, customMessage: "The lookup under way finished and was remembered");
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
        downloader.PendingLookupCount.ShouldBe(4, customMessage: "A (running), D, C, E");
        source.OpenGate();
        await WaitUntilIdleAsync(downloader);

        SongSearches(source).ShouldBe(new[] { "A", "D", "C", "E" });
        downloader.GetCachedResolution(first[0]).ShouldNotBeNull().State.ShouldBe(TrackResolutionState.Resolved, customMessage: "The started lookup finished and was remembered");
        downloader.GetCachedResolution(first[1]).ShouldBeNull();
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

        results.Select(r => r.State).ShouldAllBe(state => state == TrackResolutionState.Resolved);
        results[1].Track.ShouldBeSameAs(background[0]);
        SongSearches(source).ShouldBe(new[] { "A", "D", "B", "C" }, customMessage: "A was joined, not searched again");
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

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        source.Searches.Count.ShouldBe(searches);
        source.Extractions.ShouldBe(new[] { "BaxnrepwXck" }, ignoreOrder: false, customMessage: "Only the lookup's extraction");
        source.Downloads.ShouldHaveSingleItem().FromInfoJson.ShouldBeTrue();
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

    [Test]
    [Arguments(true, "explicit001")]
    [Arguments(false, "clean000001")]
    public async Task Picks_the_playlists_explicit_or_clean_version(bool isExplicit, string expectedVideoId)
    {
        var source = LoseYourselfSource();
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit)], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.Video.ShouldNotBeNull().VideoId.ShouldBe(expectedVideoId, customMessage: resolution.Reason);
        source.Extractions.ShouldBe(new[] { expectedVideoId }, ignoreOrder: false, customMessage: "The right version is verified first");
    }

    [Test]
    public async Task Sends_to_review_when_only_the_other_version_is_found()
    {
        using var downloader = CreateDownloader(LoseYourselfSource(includeClean: false));

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit: false)], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.NeedsReview);
        resolution.Reason.ShouldBe("YouTube Music only seems to have the explicit version; your playlist has the clean one.");
        resolution.Candidates.ShouldHaveSingleItem().Score.Explicit.ShouldBe(ExplicitMatch.Mismatch);
        resolution.Candidates.ShouldHaveSingleItem().Video.IsExplicit.ShouldBe(true);
    }

    [Test]
    public async Task Sends_to_review_when_the_versions_cant_be_told_apart()
    {
        var source = LoseYourselfSource(badges: false);
        source.ExplicitBadges["clean000001"] = false;
        source.ExplicitBadges.Remove("explicit001");
        // Only the clean one is badged as such; the first result's version is unknown.
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit: true)], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.NeedsReview);
        resolution.Reason.ShouldBe("YouTube Music has both versions, and it isn't clear which one is the explicit version in your playlist.");
    }

    [Test]
    public async Task Ignores_versions_when_nothing_says_which_is_which()
    {
        using var downloader = CreateDownloader(LoseYourselfSource(badges: false));

        var resolution = (await downloader.ResolveTracksAsync([LoseYourself(isExplicit: true)], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.Video.ShouldNotBeNull().VideoId.ShouldBe("explicit001", customMessage: "Same as before: the first confident match");
    }

    [Test]
    public async Task Remembers_explicit_badges_of_review_candidates()
    {
        using (var downloader = CreateDownloader(LoseYourselfSource(includeClean: false)))
            await downloader.ResolveTracksAsync([LoseYourself(isExplicit: false)], null, CancellationToken.None);

        using var reloaded = CreateDownloader(new FakeSource());
        var cached = reloaded.GetCachedResolution(LoseYourself(isExplicit: false))!;

        cached.Candidates.ShouldHaveSingleItem().Video.IsExplicit.ShouldBe(true);
    }

    [Test]
    public async Task Resolves_a_confident_match_by_extracting_only_the_best_flat_result()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);
        var stages = new List<TrackResolutionStage>();

        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], new SynchronousProgress<TrackResolutionProgress>(p => stages.Add(p.Stage)), CancellationToken.None)).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.Resolved);
        resolution.FromCache.ShouldBeFalse();
        var video = resolution.Video!;
        (video.VideoId, video.Title, video.Album, video.Duration, video.Source).ShouldBe(("BaxnrepwXck", "Under Pressure", "Hot Space", (TimeSpan?)TimeSpan.FromSeconds(248), ResolutionSource.Automatic));
        video.Artists.ShouldBe(new[] { "Queen", "David Bowie" });
        video.Url.ShouldBe("https://music.youtube.com/watch?v=BaxnrepwXck");
        source.Extractions.ShouldBe(new[] { "BaxnrepwXck" });
        stages.ShouldBe(new[] { TrackResolutionStage.Queued, TrackResolutionStage.Searching, TrackResolutionStage.Completed });
    }

    [Test]
    public async Task Compares_a_good_match_without_album_match_against_results_with_equally_good_titles()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([UnderPressure with { Album = "Greatest Hits II" }], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.Video.ShouldNotBeNull().VideoId.ShouldBe("BaxnrepwXck");
        source.Extractions.ShouldBe(new[] { "BaxnrepwXck", "nbwn21TU4ec" }, ignoreOrder: false, customMessage: "Not the live versions or other songs");
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

        var resolution = (await downloader.ResolveTracksAsync([Track("Song", "Artist", 200)], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.Video.ShouldNotBeNull().VideoId.ShouldBe("original001");
        source.Extractions.ShouldBe(new[] { "cover000001", "original001" });
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

        resolutions.Select(r => (r.State, r.FromCache, r.Video?.VideoId, r.Video?.Source)).ShouldBe(new (TrackResolutionState, bool, string?, ResolutionSource?)[]
        {
            (TrackResolutionState.Resolved, true, "BaxnrepwXck", ResolutionSource.Automatic),
            (TrackResolutionState.Resolved, true, "BaxnrepwXck", ResolutionSource.Automatic),
        });
        resolutions[0].Video.ShouldNotBeNull().Title.ShouldBe("Under Pressure");
        source.Searches.ShouldBeEmpty();
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
            resolution = (await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.NeedsReview);
        resolution.NeedsReview.ShouldBeTrue();
        resolution.Video.ShouldBeNull();
        resolution.Candidates.Select(c => c.Video.Id).ShouldBe(new[] { "cover000001", "other000001" });
        resolution.Candidates[0].Video.Artists.ShouldBe(new[] { "Tribute Band" });
        resolution.Candidates[0].HasAcceptableAudio.ShouldBeTrue();
        resolution.Reason.ShouldBe("No confident match on YouTube Music. The closest result was \"Song\" by Tribute Band (3:20).");

        using (var reloaded = CreateDownloader(source))
        {
            var remembered = (await reloaded.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem();

            (remembered.State, remembered.FromCache, remembered.Reason).ShouldBe((TrackResolutionState.NeedsReview, true, resolution.Reason));
            remembered.Candidates.Select(c => (c.Video.Id, c.Video.Url, c.Video.Album, c.Video.Duration, c.Score, c.Format)).ShouldBe(resolution.Candidates.Select(c => (c.Video.Id, c.Video.Url, c.Video.Album, c.Video.Duration, c.Score, c.Format)));
            remembered.Candidates[0].Video.Artists.ShouldBe(new[] { "Tribute Band" });
            source.Searches.Count.ShouldBe(2, customMessage: "Not looked up again");
        }

        using var expired = new YouTubeMusicDownloader(
            new YouTubeMusicDownloaderOptions { ToolCacheDirectory = directory, ResolutionCachePath = cachePath, ReviewCacheLifetime = TimeSpan.Zero },
            source);
        (await expired.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem().FromCache.ShouldBeFalse();
        source.Searches.Count.ShouldBe(4, customMessage: "Looked up again after the review result expired");
    }

    [Test]
    public async Task Retry_bypasses_the_remembered_result()
    {
        var source = new FakeSource();
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using var downloader = CreateDownloader(source);
        (await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem().State.ShouldBe(TrackResolutionState.NeedsReview);

        source.AddSearch("Song Artist", [YtDlpSamples.Video("song0000001", "Song", "Song", ["Artist"], "", 200, 1, "Artist")]);
        (await downloader.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem().FromCache.ShouldBeTrue();

        var retried = await downloader.RetryAsync(track, CancellationToken.None);

        (retried.State, retried.Video?.VideoId, retried.FromCache).ShouldBe((TrackResolutionState.Resolved, "song0000001", false));
        downloader.GetCachedResolution(track).ShouldNotBeNull().Video.ShouldNotBeNull().VideoId.ShouldBe("song0000001");
    }

    [Test]
    public async Task Match_without_acceptable_audio_is_unavailable()
    {
        var source = new FakeSource();
        source.AddSearch("Song Artist", [YtDlpSamples.Video("opusonly001", "Song", "Song", ["Artist"], "", 200, 1, "Artist", YtDlpSamples.OpusOnlyFormats)]);
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([Track("Song", "Artist", 200)], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.Unavailable);
        resolution.NeedsReview.ShouldBeTrue();
        resolution.Reason.ShouldNotBeNull().ShouldContain("no AAC", Case.Sensitive);
        resolution.Candidates.ShouldHaveSingleItem().FormatProblem.ShouldBe(resolution.Reason);
    }

    [Test]
    public async Task Search_failure_is_remembered_for_this_run_only()
    {
        var source = new FakeSource { SearchError = "ERROR: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>" };
        using var downloader = CreateDownloader(source);

        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.LookupFailed);
        resolution.Reason.ShouldNotBeNull().ShouldContain("getaddrinfo failed", Case.Sensitive);
        downloader.GetCachedResolution(UnderPressure).ShouldNotBeNull().State.ShouldBe(TrackResolutionState.LookupFailed);

        downloader.Dispose();
        using var reloaded = CreateDownloader(UnderPressureSource());
        reloaded.GetCachedResolution(UnderPressure).ShouldBeNull();
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
            (await downloader.ChooseVideoAsync(track, "https://music.youtube.com/playlist?list=OLAK5uy_abc", CancellationToken.None)).Error.ShouldNotBeNull().ShouldContain("playlist", Case.Sensitive);
            (await downloader.ChooseVideoAsync(track, "https://youtu.be/missing0001", CancellationToken.None)).Error.ShouldNotBeNull().ShouldStartWith("YouTube couldn't load that video.", Case.Sensitive);
            (await downloader.ChooseVideoAsync(track, "opusonly001", CancellationToken.None)).Error.ShouldNotBeNull().ShouldContain("no AAC", Case.Sensitive);

            var chosen = await downloader.ChooseVideoAsync(track, "https://www.youtube.com/watch?v=chosen00001&list=RDchosen00001", CancellationToken.None);

            chosen.Error.ShouldBeNull();
            chosen.Resolution.ShouldNotBeNull().State.ShouldBe(TrackResolutionState.Resolved);
            chosen.Resolution.Video.ShouldNotBeNull().VideoId.ShouldBe("chosen00001");
            chosen.Resolution.Video.Source.ShouldBe(ResolutionSource.Manual);
        }

        using var reloaded = CreateDownloader(new FakeSource());
        var remembered = (await reloaded.ResolveTracksAsync([track], null, CancellationToken.None)).ShouldHaveSingleItem();
        remembered.Video.ShouldNotBeNull().ShouldSatisfyAllConditions(
            v => v.VideoId.ShouldBe("chosen00001"),
            v => v.Source.ShouldBe(ResolutionSource.Manual));
    }

    [Test]
    public async Task Skips_are_remembered_until_forgotten()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);

        downloader.Skip(UnderPressure).State.ShouldBe(TrackResolutionState.Skipped);

        using (var reloaded = CreateDownloader(new FakeSource()))
        {
            var skipped = (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();
            (skipped.State, skipped.FromCache).ShouldBe((TrackResolutionState.Skipped, true));
        }

        downloader.Forget(UnderPressure).ShouldBeTrue();
        var resolved = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();
        (resolved.State, resolved.FromCache).ShouldBe((TrackResolutionState.Resolved, false));
    }

    [Test]
    public async Task Downloads_resolved_track_as_progressive_aac_without_extracting_again()
    {
        var source = UnderPressureSource();
        using var downloader = CreateDownloader(source);
        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();
        var request = Request(resolution, "track");
        var stages = new List<TrackDownloadStage>();

        var result = await downloader.DownloadTrackAsync(request, p => stages.Add(p.Stage), CancellationToken.None);

        result.FailureReason.ShouldBeNull();
        result.Format.ShouldNotBeNull().FormatId.ShouldBe("140");
        result.Audio.ShouldNotBeNull().IsAac.ShouldBeTrue();
        source.Extractions.ShouldHaveSingleItem();
        source.Downloads.ShouldBe(new[] { ("BaxnrepwXck", true, "140/bestaudio[acodec^=mp4a][abr>=96][protocol=https]") });
        M4aRemuxer.ReadAudioInfo(request.DestinationPath).IsAac.ShouldBeTrue();
        stages.Distinct().ShouldBe(new[] { TrackDownloadStage.Queued, TrackDownloadStage.Downloading, TrackDownloadStage.Remuxing, TrackDownloadStage.Succeeded });
        Directory.GetFiles(directory).ShouldBe(new[] { request.DestinationPath }, customMessage: "No intermediate files are left behind");
    }

    [Test]
    public async Task Cached_resolution_is_checked_before_downloading_and_forgotten_if_the_video_is_gone()
    {
        using (var downloader = CreateDownloader(UnderPressureSource()))
            await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None);

        var source = new FakeSource();
        using var reloaded = CreateDownloader(source);
        var cached = (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();

        var result = await reloaded.DownloadTrackAsync(Request(cached, "track"), null, CancellationToken.None);

        result.NeedsReview.ShouldBeTrue();
        result.FailureReason.ShouldNotBeNull().ShouldStartWith("The YouTube video is no longer available.", Case.Sensitive);
        source.Downloads.ShouldBeEmpty();

        using var afterward = CreateDownloader(UnderPressureSource());
        (await afterward.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem().FromCache.ShouldBeFalse();
    }

    [Test]
    [Arguments("ERROR: [youtube] chosen00001: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.")]
    [Arguments("ERROR: [youtube] chosen00001: Video unavailable. This content isn't available, try again later.")]
    [Arguments("ERROR: [youtube] chosen00001: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed> (caused by TransportError)")]
    [Arguments("ERROR: [youtube] chosen00001: Unable to download webpage: HTTP Error 429: Too Many Requests")]
    public async Task Manual_choice_survives_a_transient_failure_while_downloading(string transientError)
    {
        var source = new FakeSource();
        source.AddVideo(YtDlpSamples.Video("chosen00001", "Song", "Song", ["Artist"], "", 200, 1, "Artist"));
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using (var downloader = CreateDownloader(source))
            (await downloader.ChooseVideoAsync(track, "chosen00001", CancellationToken.None)).Succeeded.ShouldBeTrue();

        source.ExtractError = transientError;
        using (var reloaded = CreateDownloader(source))
        {
            var cached = reloaded.GetCachedResolution(track).ShouldNotBeNull();
            var result = await reloaded.DownloadTrackAsync(Request(cached, "track"), null, CancellationToken.None);

            result.Succeeded.ShouldBeFalse();
            result.NeedsReview.ShouldBeFalse();
            var reason = result.FailureReason.ShouldNotBeNull();
            (reason.StartsWith("Couldn't reach YouTube; try again later.", StringComparison.Ordinal) || reason == YouTubeMusicDownloader.YouTubeBusyMessage)
                .ShouldBeTrue(customMessage: reason);
        }

        source.ExtractError = null;
        using var afterward = CreateDownloader(source);
        var kept = afterward.GetCachedResolution(track).ShouldNotBeNull();
        kept.ShouldSatisfyAllConditions(
            k => k.State.ShouldBe(TrackResolutionState.Resolved),
            k => k.Video.ShouldNotBeNull().VideoId.ShouldBe("chosen00001"),
            k => k.Video.ShouldNotBeNull().Source.ShouldBe(ResolutionSource.Manual));
        (await afterward.DownloadTrackAsync(Request(kept, "track"), null, CancellationToken.None)).Succeeded.ShouldBeTrue(customMessage: "Downloads once YouTube is reachable again");
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

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.CoverArtUrl.ShouldBe("https://yt3.googleusercontent.com/art=w500-h500-l90-rj");
    }

    [Test]
    public async Task Manual_choice_is_forgotten_when_the_video_is_confirmed_gone()
    {
        var source = new FakeSource();
        source.AddVideo(YtDlpSamples.Video("chosen00001", "Song", "Song", ["Artist"], "", 200, 1, "Artist"));
        var track = Track("Song", "Artist", 200, uri: "spotify:track:song");
        using (var downloader = CreateDownloader(source))
            (await downloader.ChooseVideoAsync(track, "chosen00001", CancellationToken.None)).Succeeded.ShouldBeTrue();

        source.ExtractError = "ERROR: [youtube] chosen00001: Private video. Sign in if you've been granted access to this video.";
        using var reloaded = CreateDownloader(source);
        var result = await reloaded.DownloadTrackAsync(Request(reloaded.GetCachedResolution(track)!, "track"), null, CancellationToken.None);

        result.NeedsReview.ShouldBeTrue();
        result.FailureReason.ShouldNotBeNull().ShouldStartWith("The YouTube video is no longer available.", Case.Sensitive);
        reloaded.GetCachedResolution(track).ShouldBeNull();
    }

    [Test]
    public async Task Throttled_lookup_is_a_transient_failure_and_not_a_review_result()
    {
        var source = UnderPressureSource();
        source.ExtractError = "ERROR: [youtube] BaxnrepwXck: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.";

        using (var downloader = CreateDownloader(source))
        {
            var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();

            (resolution.State, resolution.IsThrottled, resolution.Reason).ShouldBe((TrackResolutionState.LookupFailed, true, YouTubeMusicDownloader.YouTubeBusyMessage));
            resolution.Candidates.ShouldBeEmpty();
            downloader.GetCachedResolution(UnderPressure).ShouldNotBeNull().IsThrottled.ShouldBeTrue(customMessage: "Remembered briefly so it isn't retried right away");
        }

        source.ExtractError = null;
        using var reloaded = CreateDownloader(source);
        reloaded.GetCachedResolution(UnderPressure).ShouldBeNull(customMessage: "Not saved: looked up again next run");
        (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem().State.ShouldBe(TrackResolutionState.Resolved);
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

        (result.IsThrottled, result.NeedsReview, result.FailureReason).ShouldBe((true, false, YouTubeMusicDownloader.YouTubeBusyMessage));
        reloaded.GetCachedResolution(UnderPressure).ShouldNotBeNull().Video.ShouldNotBeNull().VideoId.ShouldBe("BaxnrepwXck");
    }

    [Test]
    public async Task Throttled_manual_choice_says_youtube_is_busy()
    {
        var source = new FakeSource { ExtractError = "ERROR: [youtube] chosen00001: HTTP Error 429: Too Many Requests" };
        using var downloader = CreateDownloader(source);

        var result = await downloader.ChooseVideoAsync(Track("Song", "Artist", 200, uri: "spotify:track:song"), "chosen00001", CancellationToken.None);

        (result.Succeeded, result.IsThrottled, result.Error).ShouldBe((false, true, YouTubeMusicDownloader.YouTubeBusyMessage));
    }

    [Test]
    [Arguments("WARNING: [youtube] x: Unable to download webpage: HTTP Error 429: Too Many Requests (caused by <HTTPError 429: Too Many Requests>)", true)]
    [Arguments("ERROR: [youtube] x: Sign in to confirm you’re not a bot. This helps protect our community.", true)]
    [Arguments("ERROR: [youtube] x: Video unavailable. This content isn't available, try again later.", true)]
    [Arguments("ERROR: [youtube] x: Video unavailable", false)]
    [Arguments("ERROR: unable to download video data: HTTP Error 403: Forbidden", false)]
    [Arguments("ERROR: [youtube] x: Sign in to confirm your age.", false)]
    [Arguments("ERROR: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>", false)]
    public void Classifies_throttling(string message, bool isThrottled)
    {
        YtDlpException.IsThrottledMessage(message).ShouldBe(isThrottled);
    }

    [Test]
    [Arguments("ERROR: [youtube] x: Video unavailable", true)]
    [Arguments("ERROR: [youtube] x: Video unavailable. This video has been removed by the uploader", true)]
    [Arguments("ERROR: [youtube] x: Private video. Sign in if you've been granted access to this video", true)]
    [Arguments("ERROR: [youtube] x: The uploader has not made this video available in your country", true)]
    [Arguments("ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.", true)]
    [Arguments("ERROR: [youtube] x: Video unavailable. This content isn't available, try again later.", false)]
    [Arguments("ERROR: [youtube] x: Sign in to confirm you’re not a bot.", false)]
    [Arguments("ERROR: Unable to download webpage: HTTP Error 503: Service Unavailable", false)]
    [Arguments("yt-dlp exited with code 1.", false)]
    public void Classifies_gone_videos(string message, bool isGone)
    {
        YtDlpException.IsVideoGoneMessage(message).ShouldBe(isGone);
    }

    [Test]
    public async Task Cached_resolution_downloads_after_checking_the_video()
    {
        using (var downloader = CreateDownloader(UnderPressureSource()))
            await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None);

        var source = UnderPressureSource();
        using var reloaded = CreateDownloader(source);
        var cached = (await reloaded.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();
        var stages = new List<TrackDownloadStage>();

        var result = await reloaded.DownloadTrackAsync(Request(cached, "track"), p => stages.Add(p.Stage), CancellationToken.None);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        source.Extractions.ShouldBe(new[] { "BaxnrepwXck" });
        stages.ShouldContain(TrackDownloadStage.Preparing);
    }

    [Test]
    public async Task Rejects_and_deletes_a_download_that_is_not_aac()
    {
        var source = UnderPressureSource();
        source.DownloadedFile = Mp4TestFiles.CreateFragmented([[100, 100]], sampleEntryType: "Opus");
        using var downloader = CreateDownloader(source);
        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();

        var result = await downloader.DownloadTrackAsync(Request(resolution, "track"), null, CancellationToken.None);

        result.FailureReason.ShouldBe("YouTube sent Opus audio instead of AAC.");
        Directory.GetFiles(directory).ShouldBeEmpty();
    }

    [Test]
    public async Task Refuses_to_download_unresolved_tracks()
    {
        using var downloader = CreateDownloader(new FakeSource());

        var result = await downloader.DownloadTrackAsync(Request(downloader.Skip(UnderPressure), "track"), null, CancellationToken.None);

        result.FailureReason.ShouldBe("The track hasn't been resolved to a YouTube video.");
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

        results[0].Succeeded.ShouldBeTrue(customMessage: results[0].FailureReason);
        results[1].FailureReason.ShouldNotBeNull().ShouldStartWith("No confident match", Case.Sensitive);
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

        results.Select(r => r.Request).ShouldBe(requests);
        results.Select(r => r.Succeeded).ShouldBe(new[] { true, true, false, true, true, true });
        results[2].FailureReason.ShouldBe("ERROR: [youtube] video000002: Video unavailable");
        source.MaxConcurrentDownloads.ShouldBe(2);
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
        lookup.IsCompleted.ShouldBeFalse();
        downloader.GetCachedResolution(track).ShouldBeNull(customMessage: "Nothing is remembered about the track while YouTube is busy.");

        clock.Advance(YouTubeTraffic.Cooldowns[0]);
        var resolution = (await lookup.WaitAsync(TimeSpan.FromSeconds(30))).ShouldHaveSingleItem();

        resolution.State.ShouldBe(TrackResolutionState.Resolved);
        checks.ShouldBe(1);
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

        (choice.Resolution, choice.Error, choice.IsThrottled).ShouldBe(((TrackResolution?)null, "YouTube is busy; continuing in 60 min", true));
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
        (string VideoId, bool FromInfoJson, string FormatSelector)[] started;
        lock (source) started = [.. source.Downloads];
        started.ShouldBeEmpty(customMessage: "Downloads started while both slots were taken by lookups.");

        source.OpenGate();
        var results = await downloads;

        results.Select(r => r.Succeeded).ShouldAllBe(succeeded => succeeded);
        source.MaxConcurrentSearchesAndDownloads.ShouldBe(2);
    }

    [Test]
    public async Task Cancellation_throws_and_cleans_up()
    {
        var source = UnderPressureSource();
        source.DownloadDelay = Timeout.InfiniteTimeSpan;
        using var downloader = CreateDownloader(source);
        var resolution = (await downloader.ResolveTracksAsync([UnderPressure], null, CancellationToken.None)).ShouldHaveSingleItem();
        using var cancellation = new CancellationTokenSource();

        var task = downloader.DownloadTrackAsync(Request(resolution, "track"), p =>
        {
            if (p.Stage == TrackDownloadStage.Downloading) cancellation.CancelAfter(50);
        }, cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () => await task);
        Directory.GetFiles(directory).ShouldBeEmpty();
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
