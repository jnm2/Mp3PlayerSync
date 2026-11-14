# Mock Exportify

This is the stand-in for exportify.net, and for Spotify's sign-in page, that the end-to-end tests
(`tests\Test-MockEndToEnd.ps1`) run the app against. It is written from scratch for those tests. It doesn't copy
or imitate exportify.net's code, markup or styles, and it loads nothing from the internet. It only provides what the
app's bridge (`src\Mp3PlayerSync\ExportifyBridge.js`), the app's navigation rules (`ExportifyHost.cs`) and the tests
rely on, which is listed below so that the next person knows why each piece exists. Anything not on this list can go.

The server (`MockExportifyServer.cs`) builds every page and CSV from a library defined in code (`MockLibrary.cs`).
It prints its address on its first line, which is how the tests find it, and `--port 0` makes it pick a free port.

## The Exportify page

The home page (`/`, whatever its query) is what the app loads as Exportify.

- Its title is "Exportify (mock)". The tests look for a page with that title to check that the app never shows
  Exportify's own page to the user.
- An element with the ID `error` holds the page's error and warning text. The bridge reads it to tell why an export
  failed and to pass on warnings, and the app rewords that text in its own words.
- A button with the ID `loginButton` starts the sign-in. The app clicks it to sign in, and again to renew a token
  that has run out. It goes to the sign-in page on `127.0.0.1`, a different origin from the page's `localhost`, just
  as accounts.spotify.com is a different site from exportify.net. That's the origin the app is told is Spotify's
  sign-in, so the app shows that page and nothing else.
- When the page arrives with `?code=`, as it does from the sign-in, it removes the code from the address and trades
  it for a token at `/token`. The bridge tells a sign-in in progress by the code in the address.
- The token and when it was issued are kept in local storage as `access_token` and `access_token_timestamp`, the
  names the bridge reads to tell whether the user is signed in and whether the token is more than an hour old.
- The playlist table is under `#playlists` as `table tbody tr`, and only appears while the token is less than an hour
  old. The app takes rows as a sign that the user is signed in and the library has loaded. Each row has, in order, a
  cell with the cover image (if any), a cell with a link to the list on Spotify (the link is the list's key and its
  text is the name), the owner's user ID, the track count, and a cell with an export button whose ID starts with
  `export`. Liked Songs and Saved Albums are rows too, with the links the app recognizes them by
  (`https://open.spotify.com/collection/tracks` and `.../albums`).
- An export button changes its text to "Exporting" and back afterwards, which the bridge watches. It fetches the
  list's CSV from `/export/<list>` with the token, adds any warning to the error element, and then saves it the way
  exportify.net does: it makes a `blob:` URL for the CSV and dispatches a click on an `<a download>` that points at
  it, which the bridge intercepts. If the export fails, the page adds the HTTP status to the error element and saves
  nothing. If the token is refused (HTTP 401), the page goes home, as exportify.net does, which the app takes as the
  signal to renew the token.

## The sign-in page

`/sign-in` stands in for Spotify's sign-in, on `127.0.0.1`.

- Its title is "Spotify login (mock)", and it has a "Sign in" button. Signing in sets a session cookie and goes back
  to Exportify with a code. With that cookie already set, `/sign-in` sends the browser straight back with a code,
  as Spotify does when the user is still signed in there. That's how the app renews a token without showing anything.
- "Sign up for Spotify" (an ordinary link) and "Help" (a link that opens a new window) check that links leading away
  from the sign-in open outside the app.
- With `?auto`, it signs in by itself after a moment, standing in for the user, unless a test holds it (see below).

## What the tests control

- `?autoSignIn` on the home page makes its sign-in button go to `/sign-in?auto`.
- `?expireToken` on the home page makes the stored token more than an hour old when the page loads.
- `?expireWhenAsked` on the home page makes the token run out while the page is open, once a test asks for it with
  `POST /expire`. The page asks `GET /expire` four times a second, and from when it's told, the tokens issued so far
  are refused.
- `?expireOnExport` on the home page makes the first export refuse the tokens issued so far, even though by its age
  the page still thinks its token is good, as Spotify can.
- `POST /sign-in-hold` with `hold` keeps the automatic sign-in waiting until the test has seen the sign-in screen,
  and `release` lets it go on.
- `GET /requests` lists what the app has asked the mock for, such as `token`, `sign-in page` or `export roadtrip`,
  so the tests can tell when the app renewed its token, loaded the sign-in page or exported a list again.

## The libraries

The test library (`MockLibrary.Test`) has exactly what the end-to-end tests check: "Road Trip, Vol. 2" with 250
tracks, `Café ☕ "Quotes"` (an awkward name, whose songs the test host's fake YouTube Music treats specially),
"Broken" (its export fails with HTTP 500), Playlists 4 to 60 (every third one someone else's), seven Liked Songs and
three saved albums. Playlists 5 and 14 have album covers, so they look like albums until they're exported, Playlist 8
has a mosaic and Playlist 10 an uploaded cover. Every export comes with a warning (HTTP 403), which the app should
pass on in its own words.

With `--demo`, the mock lists the made-up library in `DemoLibrary.json` instead, with drawn covers
(`/demo-cover/<id>.png`, from `DemoCovers.cs`), for the README's screenshots (`tests\Update-ReadmeScreenshots.ps1`).
