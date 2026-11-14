// Fake Spotify Web API for the mock Exportify page. Replaces window.fetch before exportify.js loads.
// Test switch: ?bump=<playlist id> gives that playlist a new snapshot_id, as if it had been edited.
(() => {
    const me = { id: 'mockuser', external_urls: { spotify: 'https://open.spotify.com/user/mockuser' } };
    const someoneElse = { id: 'someoneelse', external_urls: { spotify: 'https://open.spotify.com/user/someoneelse' } };
    const bumped = new URLSearchParams(location.search).get('bump');

    // Test switch: ?demo serves the believable made-up library in demo-library.js, for screenshots
    // (tests/Update-ReadmeScreenshots.ps1). It's kept for the session, since signing in returns without the query string.
    if (new URLSearchParams(location.search).has('demo')) localStorage.setItem('mock_demo', '1');
    const demo = localStorage.getItem('mock_demo') === '1';
    const demoLibrary = window.demoLibrary;
    const demoCover = name => location.origin + '/demo-cover/' + encodeURIComponent(name) + '.png';
    const demoSeeds = {};
    const demoSeed = prefix => demoSeeds[prefix] ??= [...prefix].reduce((sum, c) => sum * 31 + c.charCodeAt(0) >>> 0, 7);

    const playlistSpecs = demo ? demoLibrary.playlists.map(p => ({ ...p, owner: p.saved ? someoneElse : me })) : [
        { id: 'roadtrip', name: 'Road Trip, Vol. 2', total: 250 },
        { id: 'cafe', name: 'Café ☕ "Quotes"', total: 3 },
        { id: 'broken', name: 'Broken', total: 5, fail: true },
    ];
    if (!demo) for (let n = 4; n <= 60; n++) playlistSpecs.push({ id: 'p' + n, name: 'Playlist ' + n, total: n % 7 + 1, owner: n % 3 === 0 ? someoneElse : me });

    // Covers as Spotify makes them: an album cover for playlists spanning fewer than four albums, a mosaic for more,
    // or an uploaded image. Playlist 5 looks like an album by its cover but has tracks from four albums.
    const covers = demo ? Object.fromEntries(playlistSpecs.map(p => [p.id, demoCover(p.id)])) : {
        p5: 'https://i.scdn.co/image/ab67616d0000b273000000000000000000000005',
        p14: 'https://i.scdn.co/image/ab67616d0000b273000000000000000000000014',
        p8: 'https://mosaic.scdn.co/640/ab67616d0000b273aaaaab67616d0000b273bbbbab67616d0000b273ccccab67616d0000b273dddd',
        p10: 'https://image-cdn-ak.spotifycdn.com/image/ab67706c0000da84000000000000000000000010',
    };

    const playlists = playlistSpecs.map(p => ({
        id: p.id,
        name: p.name,
        images: covers[p.id] ? [{ url: covers[p.id], height: 640, width: 640 }] : [],
        snapshot_id: p.id === bumped ? 'snapshot-2' : 'snapshot-1',
        external_urls: { spotify: 'https://open.spotify.com/playlist/' + p.id },
        owner: p.owner ?? me,
        tracks: { total: p.total, href: 'https://api.spotify.com/v1/playlists/' + p.id + '/tracks' },
    }));

    const demoTrack = (prefix, i) => {
        const seed = demoSeed(prefix);
        const title = (seed + i * 5) % demoLibrary.titles.length, artist = (seed + i * 7) % demoLibrary.artists.length;
        const [artistName, albumName, release] = demoLibrary.artists[artist];
        return {
            id: prefix + 't' + i,
            uri: 'spotify:track:' + prefix + 't' + i,
            name: demoLibrary.titles[title],
            artists: [{ id: 'da' + artist, name: artistName }],
            album: { id: 'dal' + artist, name: albumName, release_date: release },
            disc_number: 1,
            track_number: title % 12 + 1,
            duration_ms: 150000 + (title * 7919 + artist * 104729) % 120000,
            popularity: 50,
            explicit: false,
            // The same song has the same ISRC in every playlist, so the app finds it elsewhere on the player.
            external_ids: { isrc: 'DEMO' + title + 'X' + artist },
        };
    };

    const track = (prefix, i) => demo ? demoTrack(prefix, i) : ({
        id: prefix + 't' + i,
        uri: 'spotify:track:' + prefix + 't' + i,
        name: prefix + ' song ' + (i + 1),
        artists: i % 5 === 0
            ? [{ id: 'a1', name: 'Earth, Wind & Fire' }, { id: 'a2', name: 'Chaka Khan' }]
            : [{ id: 'a' + (3 + i % 10), name: 'Artist ' + (i % 10) }],
        album: { id: 'al' + (i % 4), name: 'Album ' + (i % 4), release_date: '2001-01-01' },
        disc_number: 1,
        track_number: i + 1,
        duration_ms: 180000 + i * 1000,
        popularity: 50,
        explicit: i % 3 === 0,
        external_ids: { isrc: 'ISRC' + i },
    });

    const items = (prefix, offset, limit, total, addedAt) => {
        const result = [];
        for (let i = offset; i < Math.min(total, offset + limit); i++) {
            result.push({ added_at: addedAt(i), added_by: { id: 'mockuser' }, track: track(prefix, i) });
        }
        return result;
    };

    // Liked songs come newest first, like /me/tracks.
    const likedTotal = demo ? demoLibrary.likedTotal : 7;
    const likedAddedAt = i => new Date(Date.UTC(2024, 11, 24 - i)).toISOString();
    // In the demo, playlists were built up over a year, and some have songs added since the last sync.
    const playlistAddedAt = (spec, i) => !demo ? '2024-03-01T12:00:00Z'
        : i >= spec.total - (spec.newSinceLastSync ?? 0) ? new Date(Date.UTC(2025, 4, 2 + i - spec.total)).toISOString()
        : new Date(Date.UTC(2024, 2, 1 + i * 9)).toISOString();

    const albumSpecs = demo ? demoLibrary.albums : [
        { id: 'albumone', name: 'Mock Album One', artist: 'The Mockers', release: '2019-04-05', added: '2025-06-01T00:00:00Z', total: 3 },
        { id: 'bigalbum', name: 'Big Album', artist: 'Prolific Band', release: '2010-10-10', added: '2025-01-15T00:00:00Z', total: 60 },
        { id: 'oldalbum', name: 'Old Album', artist: 'Earth, Wind & Fire', release: '1975-01-01', added: '2023-05-05T00:00:00Z', total: 2 },
    ];
    const albumTrackPage = (spec, offset, limit) => {
        const pageItems = [];
        for (let i = offset; i < Math.min(spec.total, offset + limit); i++) {
            const t = track(spec.id, i);
            pageItems.push({ ...t, artists: [{ id: 'aa-' + spec.id, name: spec.artist }] });
        }
        const next = offset + limit < spec.total
            ? 'https://api.spotify.com/v1/albums/' + spec.id + '/tracks?offset=' + (offset + limit) + '&limit=' + limit
            : null;
        return { total: spec.total, offset, limit, items: pageItems, next };
    };
    const album = spec => ({
        id: spec.id,
        name: spec.name,
        album_type: 'album',
        artists: [{ id: 'aa-' + spec.id, name: spec.artist }],
        release_date: spec.release,
        total_tracks: spec.total,
        label: 'Mock Label',
        external_urls: { spotify: 'https://open.spotify.com/album/' + spec.id },
        tracks: albumTrackPage(spec, 0, 50),
    });

    const expireOnExport = new URLSearchParams(location.search).has('expireOnExport');

    function route(url, method, init) {
        if (url.hostname === 'accounts.spotify.com' && url.pathname === '/api/token' && method === 'POST') {
            return [200, { access_token: 'mock-access-token-' + Date.now(), token_type: 'Bearer', expires_in: 3600 }];
        }
        if (url.hostname !== 'api.spotify.com') return null;

        // Test switch: ?expireOnExport makes Spotify stop accepting the token at the first export, once, even though
        // Exportify still thinks it's good.
        if (expireOnExport && !localStorage.getItem('mock_expired_on_export') && (/^\/v1\/playlists\/[^/]+\/tracks$/.test(url.pathname) || url.pathname === '/v1/tracks')) {
            localStorage.setItem('mock_expired_on_export', '1');
            localStorage.setItem('mock_expired_token', localStorage.getItem('access_token'));
        }

        // Like Spotify, stop accepting a token once it has run out (see ?expireToken and ?expireWhenAsked in index.html).
        const authorization = new Headers(init?.headers).get('Authorization');
        if (authorization === 'Bearer ' + localStorage.getItem('mock_expired_token')) {
            return [401, { error: { status: 401, message: 'The access token expired' } }];
        }

        const q = url.searchParams;
        const offset = Number(q.get('offset') ?? 0), limit = Number(q.get('limit') ?? 20);
        const path = url.pathname;
        let m;

        if (path === '/v1/me') return [200, me];
        if (path === '/v1/me/tracks') return [200, { total: likedTotal, items: items('liked', offset, limit, likedTotal, likedAddedAt) }];
        if (path === '/v1/me/albums') {
            return [200, { total: albumSpecs.length, items: albumSpecs.slice(offset, offset + limit).map(s => ({ added_at: s.added, album: album(s) })) }];
        }
        if ((m = path.match(/^\/v1\/albums\/([^/]+)\/tracks$/))) {
            const spec = albumSpecs.find(s => s.id === m[1]);
            return spec ? [200, albumTrackPage(spec, offset, limit)] : [404, { error: { status: 404, message: 'No such album' } }];
        }
        if (path === '/v1/me/playlists') return [200, { total: playlists.length, items: playlists.slice(offset, offset + limit) }];
        if ((m = path.match(/^\/v1\/playlists\/([^/]+)\/tracks$/))) {
            const spec = playlistSpecs.find(p => p.id === m[1]);
            if (!spec || spec.fail) return [500, { error: { status: 500, message: 'Mock server error' } }];
            return [200, { total: spec.total, items: items(spec.id, offset, limit, spec.total, i => playlistAddedAt(spec, i)) }];
        }
        if (path === '/v1/tracks') {
            // Exportify's Saved Albums export looks up each saved album's tracks here.
            return [200, { tracks: q.get('ids').split(',').map(id => {
                const spec = albumSpecs.find(s => id.startsWith(s.id + 't'));
                const i = Number(id.slice(spec.id.length + 1));
                return { ...track(spec.id, i), artists: [{ id: 'aa-' + spec.id, name: spec.artist }], album: { id: spec.id, name: spec.name, release_date: spec.release } };
            }) }];
        }
        if (path === '/v1/artists') return [200, { artists: q.get('ids').split(',').map(id => ({ id, genres: id === 'a1' ? ['disco', 'funk'] : ['pop'] })) }];
        if (path === '/v1/albums') return [200, { albums: q.get('ids').split(',').map(id => ({ id, label: 'Label ' + id })) }];
        // Spotify has deprecated audio features for new apps; Exportify reports the 403 but still exports.
        if (path === '/v1/audio-features') {
            // The demo shows no warning, so its screenshots don't.
            return demo
                ? [200, { audio_features: q.get('ids').split(',').map(id => ({ id, danceability: 0.6, energy: 0.7, key: 5, loudness: -6, mode: 1, speechiness: 0.05, acousticness: 0.2, instrumentalness: 0, liveness: 0.1, valence: 0.6, tempo: 118, time_signature: 4 })) }]
                : [403, { error: { status: 403, message: 'Forbidden' } }];
        }

        return [404, { error: { status: 404, message: 'Not mocked: ' + path } }];
    }

    window.fetch = async (input, init) => {
        const url = new URL(typeof input === 'string' ? input : input.url, location.href);
        navigator.sendBeacon('/api-log', url.pathname + url.search); // For the tests: see Start-MockExportify.ps1
        const response = route(url, (init?.method ?? 'GET').toUpperCase(), init);
        if (!response) throw new TypeError('The mock only serves Spotify requests: ' + url);

        await new Promise(r => setTimeout(r, 20)); // Simulated latency
        const [status, body] = response;
        return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    };
})();
