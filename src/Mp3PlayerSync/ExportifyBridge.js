// Injected by Mp3PlayerSync into every document via CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync,
// so it runs before any of the page's own scripts. Apart from hiding WebView2's message channel, which it does
// everywhere, it only acts on the Exportify origin.
//
// Exportify (exportify.net) exports a playlist by building a CSV and calling FileSaver.js's saveAs(blob, fileName),
// which creates a blob: URL for it and clicks an <a download> pointing at it. Those clicks are intercepted and the
// CSV is handed to the host instead. Should a download start anyway, the host cancels it and asks this script to
// read the blob through "captureUrl".
//
// Pages should see an ordinary Edge: no extra globals, and patched functions are indistinguishable from native ones.
(() => {
    'use strict';

    // Keep the host's message channel to ourselves; a normal browser has no window.chrome.webview.
    const webview = window.chrome?.webview;
    if (webview) {
        try { delete window.chrome.webview; } catch { }
    }

    const allowedOrigin = __ALLOWED_ORIGIN__;
    const tokenLifetimeMs = 3600000; // Exportify treats access tokens as valid for an hour
    const renewMarginMs = 300000; // Renew this long before the hour runs out

    if (window.top !== window || location.origin !== allowedOrigin || !webview) return;

    const post = message => webview.postMessage(message);
    const log = message => post({ type: 'log', message: String(message) });

    // ---- Capturing exported CSVs -------------------------------------------------------------------------

    let pending = null; // The export the host asked for: { key, errorBefore, sawExporting, captured, observer, failTimer }
    const blobsByUrl = new Map();

    const errorText = () => (document.getElementById('error')?.innerText ?? '').trim();
    const errorTextSince = before => {
        const now = errorText();
        return (now.startsWith(before) ? now.slice(before.length) : now).trim();
    };

    function capture(blob, name) {
        name = String(name || (blob && blob.name) || '');

        if (!(blob instanceof Blob) || !(/\.csv$/i.test(name) || /^text\/csv/i.test(blob.type))) {
            log('Ignored a download that is not a CSV: ' + name);
            return false;
        }

        const request = pending;
        let warnings = '';
        if (request) {
            pending = null;
            request.captured = true;
            request.observer?.disconnect();
            clearTimeout(request.failTimer);
            warnings = errorTextSince(request.errorBefore);
        }

        blob.text().then(
            csv => post({ type: 'exportCompleted', key: request ? request.key : null, fileName: name, csv, warnings }),
            error => post({ type: 'exportFailed', key: request ? request.key : null, code: 'readFailed', error: 'Could not read the exported CSV: ' + error }));

        return true;
    }

    // Patched functions replace natives where they live, with the same name and length, no prototype property,
    // and a Function.prototype.toString that reports the native function's source.
    const disguises = new WeakMap(); // replacement -> the native function it stands in for
    const patch = (owner, name, methods) => {
        const native = owner[name];
        const replacement = methods[name];
        disguises.set(replacement, native);
        Object.defineProperty(owner, name, { ...Object.getOwnPropertyDescriptor(owner, name), value: replacement });
        return native;
    };

    const nativeToString = patch(Function.prototype, 'toString', {
        toString() { return Reflect.apply(nativeToString, disguises.get(this) ?? this, arguments); },
    });

    const nativeCreateObjectURL = patch(URL, 'createObjectURL', {
        createObjectURL(obj) {
            const url = Reflect.apply(nativeCreateObjectURL, this, arguments);
            if (obj instanceof Blob) {
                blobsByUrl.set(url, obj);
                setTimeout(() => blobsByUrl.delete(url), 120000);
            }
            return url;
        },
    });

    const interceptAnchor = anchor => {
        if (!(anchor instanceof HTMLAnchorElement) || !anchor.hasAttribute('download')) return false;
        const blob = blobsByUrl.get(anchor.href);
        return blob !== undefined && capture(blob, anchor.download || anchor.href);
    };

    const nativeClick = patch(HTMLElement.prototype, 'click', {
        click() {
            if (interceptAnchor(this)) return;
            return Reflect.apply(nativeClick, this, arguments);
        },
    });

    // FileSaver.js clicks with dispatchEvent(new MouseEvent('click')).
    const nativeDispatchEvent = patch(EventTarget.prototype, 'dispatchEvent', {
        dispatchEvent(event) {
            if (event?.type === 'click' && interceptAnchor(this)) return true;
            return Reflect.apply(nativeDispatchEvent, this, arguments);
        },
    });

    // Last resort, requested by the host from CoreWebView2.DownloadStarting after it cancels the download.
    async function captureUrl(url, name) {
        try {
            const blob = blobsByUrl.get(url) ?? await (await fetch(url)).blob();
            capture(blob, name);
        } catch (error) {
            const request = pending;
            pending = null;
            post({ type: 'exportFailed', key: request ? request.key : null, code: 'readFailed', error: 'Could not read the download ' + url + ': ' + error });
        }
    }

    // ---- Page state and playlists ------------------------------------------------------------------------

    const playlistRows = () => Array.from(document.querySelectorAll('#playlists table tbody tr'));

    const rowKey = (row, index) => row.cells[1]?.querySelector('a')?.href || ('row:' + index);

    function scrapePlaylists() {
        return playlistRows().map((row, index) => {
            const link = row.cells[1]?.querySelector('a');
            const trackCount = Number.parseInt((row.cells[3]?.textContent ?? '').trim(), 10);
            return {
                key: rowKey(row, index),
                name: (link?.textContent ?? row.cells[1]?.textContent ?? '').trim(),
                owner: (row.cells[2]?.textContent ?? '').trim(),
                trackCount: Number.isNaN(trackCount) ? null : trackCount,
                url: link?.href ?? null,
                imageUrl: row.cells[0]?.querySelector('img')?.src ?? null, // Tells album-like playlists apart
            };
        });
    }

    function computeState() {
        let token = null, timestamp = 0;
        try {
            token = localStorage.getItem('access_token');
            timestamp = Number(localStorage.getItem('access_token_timestamp'));
        } catch { }

        // A failed token exchange makes Exportify store the string "undefined".
        const hasToken = !!token && token !== 'undefined';
        const age = Date.now() - timestamp;
        const signedIn = hasToken && age < tokenLifetimeMs;

        return {
            type: 'state',
            signedIn,
            tokenExpired: hasToken && !signedIn,
            renewSoon: signedIn && age >= tokenLifetimeMs - renewMarginMs,
            signingIn: arrivedWithCode && !signedIn && !errorText(),
            playlistsReady: playlistRows().length > 0,
            url: location.href,
            error: errorText(),
        };
    }

    // Spotify redirects back with ?code=, which Exportify removes from the URL and exchanges for a token.
    const arrivedWithCode = new URLSearchParams(location.search).has('code');

    let lastStateJson = null;
    let lastPlaylistsJson = null;

    function update() {
        const state = computeState();
        const stateJson = JSON.stringify(state);
        if (stateJson !== lastStateJson) {
            lastStateJson = stateJson;
            post(state);
        }

        if (state.playlistsReady) {
            const playlists = scrapePlaylists();
            const playlistsJson = JSON.stringify(playlists);
            if (playlistsJson !== lastPlaylistsJson) {
                lastPlaylistsJson = playlistsJson;
                post({ type: 'playlists', playlists });
            }
        }
    }

    new MutationObserver(update).observe(document, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['style'] });
    document.addEventListener('DOMContentLoaded', update);
    window.addEventListener('load', update);
    setInterval(update, 30000); // Notices when the token's hour runs out
    update();

    // ---- Driving Exportify -------------------------------------------------------------------------------

    function exportPlaylist(key) {
        // The host words failures itself from their codes; the details are only for its log.
        const fail = (code, error) => post({ type: 'exportFailed', key, code, error });

        if (pending) return fail('busy', 'Another export is still in progress.');

        const rows = playlistRows();
        const row = rows.find((r, i) => rowKey(r, i) === key);
        if (!row) return fail('notListed', 'The playlist is no longer listed.');

        const button = row.querySelector('button[id^="export"]') ?? row.cells[row.cells.length - 1]?.querySelector('button');
        if (!button) return fail('noButton', 'The playlist has no Export button.');

        const request = { key, errorBefore: errorText(), sawExporting: false, captured: false, observer: null, failTimer: 0 };
        pending = request;

        // Exportify swaps the button text to "Exporting" and back to "Export" in a finally block, after saveAs.
        request.observer = new MutationObserver(() => {
            if (/Exporting/i.test(button.textContent)) {
                request.sawExporting = true;
            } else if (request.sawExporting && !request.captured) {
                request.observer.disconnect();
                // Give the anchor and native download fallbacks time to deliver the CSV.
                request.failTimer = setTimeout(() => {
                    if (pending !== request) return;
                    pending = null;
                    fail('noCsv', errorTextSince(request.errorBefore));
                }, 5000);
            }
        });
        request.observer.observe(button, { childList: true, subtree: true, characterData: true });

        button.click();
    }

    // With a live Spotify session cookie, Spotify redirects straight back to Exportify with a new code. Exportify
    // hides the button while it shows the playlists, but clicking it still starts its sign-in, which is how an
    // expired (or nearly expired) token is renewed. Forced when Spotify no longer accepts a token that looks good.
    function signIn(force) {
        const start = () => setTimeout(() => {
            const state = computeState();
            const button = document.getElementById('loginButton');
            if ((force || !state.signedIn || state.renewSoon) && button) button.click();
        }, 0);
        if (document.readyState === 'complete') start(); else window.addEventListener('load', start, { once: true });
    }

    webview.addEventListener('message', event => {
        const command = event.data || {};
        switch (command.cmd) {
            case 'getState':
                lastStateJson = lastPlaylistsJson = null;
                update();
                break;
            case 'export':
                exportPlaylist(command.key);
                break;
            case 'signIn':
                signIn(command.force === true);
                break;
            case 'captureUrl':
                captureUrl(command.url, command.name);
                break;

        }
    });
})();
