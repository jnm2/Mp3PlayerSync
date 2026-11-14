# Settled design decisions

Every decision here was made by the user. Code and tests follow this list, not the other way around.

- Read this before any UI or behavior change.
- Don't contradict an entry without the user's explicit OK. If the user changes one, update its line in the same change.
- When the user settles something new, add it here in the same change, with a new ID and its check.
- IDs are stable: never renumber or reuse one. Retire an entry by striking it through with the date and reason.
- Checks: `Class.Method` is a unit test in `tests\Mp3PlayerSync.Tests`; *e2e* quotes a check in
  `tests\Test-MockEndToEnd.ps1` (run with `tests\Invoke-EndToEnd.ps1`); *manual* means look before merging.
  New e2e checks start with their ID, e.g. `[TEXT-5]`. If a check fails, fix the code; don't weaken the check.

## Product and data

| ID | Rule | Check |
|----|------|-------|
| DATA-1 | Spotify data comes only through exportify.net's own UI and exports. No direct Spotify Web API calls; never reuse Exportify's token (reading whether it exists or has expired, to know the sign-in state, is fine). | `DesignDecisionSourceTests.DATA_1_The_app_never_calls_Spotify_itself`; manual for the token |
| DATA-2 | Never export every playlist to compute anything. Saved albums come from Exportify's single Saved Albums export. | `LibraryTests.Groups_saved_albums_export_by_album_in_export_order`; e2e "Saved albums: album-like playlists, then saved albums newest first"; manual for "never export every playlist" |
| AUDIO-1 | Audio is YouTube's native AAC in .m4a. No Opus, no transcoding, no ffmpeg. (Rewriting the DASH .m4a container in-process, as `M4aRemuxer` does, is not transcoding.) | `YtDlpParsingTests.Picks_aac_140_even_when_opus_has_a_higher_bitrate`, `.Fails_when_only_opus_is_available`, `.Ignores_streamed_and_video_formats`; `DesignDecisionSourceTests.AUDIO_1_Downloads_never_convert_or_use_ffmpeg` |
| DEVICE-1 | "Keep all knowledge about the contents of a device on the device, and not on the PC. The PC can cache the recent selections and drive ID and so on to remember recency, and it can cache internet info that's not dependent on the device." Allowed on the PC: the last player and its drive ID, the last picked folder, recent playlists and the selected tab, what Exportify's exports showed, YouTube match and lookup caches, downloaded audio, cover art, and the YouTube pacer state. Not allowed on the PC: what's on a card, which folder on it is the sync root, playlist-to-folder mappings, or a scanned index of a card. Anything like that lives on the device (as the playlist marker files do) or is worked out from the device each time. So an index per volume (#31) would have to be stored on the card, or not exist. | manual |

## Writing to the player

| ID | Rule | Check |
|----|------|-------|
| WRITE-1 | Stage files locally, then write each one to the player once, sequentially. | `PlayerSyncerTests.Only_resolved_songs_are_downloaded_and_placed`; manual for "once, sequentially" |
| WRITE-2 | Write to a `.partial` name, then rename it into place. | `CopySafetyTests.Failed_rename_deletes_the_partial_copy`, `.Leftover_partial_copies_anywhere_on_the_player_are_removed_first` |
| WRITE-3 | One writer per drive. | manual (`PlacementExecutor` writer gates) |
| WRITE-4 | Renumber by rename only; never copy a file to give it a new number. | `PlaylistExecutorTests.Renumbering_renames_the_files_without_copying_them` |
| WRITE-5 | Never write anything during scans, planning or no-op syncs. | `PlaylistFoldersTests.Assigning_and_planning_write_nothing`, `CopySafetyTests.Syncing_files_that_are_already_right_writes_nothing`, `PlaylistExecutorTests.Rerunning_a_finished_plan_changes_nothing` |
| WRITE-6 | The scanner is read-only and doesn't update last-access times. | `PlayerLibraryTests.Reading_tags_leaves_the_file_untouched` |
| WRITE-7 | The playlist marker file is written only when it changes. | `PlaylistFoldersTests.Marker_is_hidden_not_audio_and_only_written_when_it_changes` |
| CARD-1 | A locked card shows "This card is locked" and disables Sync. | `PlayersTests.A_locked_card_says_so`; manual for disabling Sync |
| CARD-2 | When there isn't enough space, a "won't fit" estimate disables Sync. | `SpaceEstimateTests.Counts_the_songs_at_the_end_that_wont_fit`, `.Files_being_removed_make_room`; manual for disabling Sync |

## Tags

| ID | Rule | Check |
|----|------|-------|
| TAG-1 | An album's album artist is the artist on every one of its tracks, otherwise "Various Artists". | `TrackMetadataTests.Compilation_gets_various_artists_on_every_track`, `.Soundtrack_by_a_composer_with_guest_tracks_gets_the_composer` |
| TAG-2 | Copies made for playlists get no album-artist tag. | manual |
| TAG-3 | Cover art is the YouTube Music match's square album art, embedded as a baseline (not progressive) JPEG of at most 500×500 for older players. A match with only 16:9 video thumbnails (a music video) gets one center-cropped to a square. Art is fetched and converted while staging, with Windows' own imaging; no image library. | `YouTubeCoverArtTests.A_song_gets_its_square_album_art_at_500_pixels`, `.A_music_video_gets_the_smallest_16_9_jpeg_that_is_tall_enough_to_crop`; `PlayerCoverArtTests.A_synced_song_gets_a_500_pixel_baseline_jpeg_cover`, `.Converts_a_16_9_frame_to_its_center_square_as_a_500_pixel_baseline_jpeg`; `PlayerSyncerTests.Songs_get_the_cover_art_of_their_youtube_match` |

## UI text

| ID | Rule | Check |
|----|------|-------|
| TEXT-1 | Never mention "browser" or exportify.net, error messages included: the page's own error text is reworded in the app's words (`FailureText`) and goes only to the debug log. | `DesignDecisionSourceTests.TEXT_1_and_TEXT_3_The_UI_never_mentions_the_browser_or_Exportify_or_misnames_the_app` (XAML), `.TEXT_1_Messages_the_app_shows_never_mention_the_browser_or_Exportify`; `FailureTextTests`; e2e "Sign-in text mentions neither browser nor Exportify", "[TEXT-1] The … page mentions neither the browser nor Exportify", "[TEXT-1] The warning is in the app's words", "[TEXT-1] The failure is in the app's words" |
| TEXT-2 | No explanatory or "how we did this" text. | manual |
| TEXT-3 | The app is "MP3 Player Sync", never "Mp3PlayerSync". | `DesignDecisionSourceTests.TEXT_3_The_window_is_called_MP3_Player_Sync`, `.TEXT_1_and_TEXT_3_The_UI_never_mentions_the_browser_or_Exportify_or_misnames_the_app`; e2e "[TEXT-3] The window is called 'MP3 Player Sync'" |
| TEXT-4 | "Sign out of Spotify". | e2e "[TEXT-4] Sign-out button" |
| TEXT-5 | Banners: "Select an MP3 player to sync", "Select a playlist to sync", "Choose songs to sync". | e2e "MP3 player page comes first", "Library banner: …", "[TEXT-5] Contents banner" |
| TEXT-6 | Track status labels: "Finding source…", "Source found". | e2e "Songs are looked for as soon as the playlist opens" ("Source found"); manual for "Finding source…" |
| TEXT-15 | While songs are looked for in the background, the contents toolbar shows one line, "Finding sources… 120 of 249" (found so far, of the songs being looked for), which goes away when finding finishes. Each row keeps its own "Finding source…" (TEXT-6). | e2e "[TEXT-15] One line counts the songs being looked for", "[TEXT-15] The finding progress line goes away when finding finishes" |
| STATUS-1 | Each track's status has an icon and a semantic color from Fluent's theme brushes (resolved per element, so light, dark and high contrast follow): On player ✓ (E73E) success; Needs review ⚠ (E7BA) caution; Couldn't search, Unavailable and Failed (EA39) critical; Skipped 🛇 (E733, a circle with a slash) neutral grey, because skipping is the user's own choice, not a problem. Other statuses have no icon, but keep its slot so all the text lines up. The text stays and carries the meaning; the icon is hidden from Narrator. | e2e "[STATUS-1] … has a success icon", "[STATUS-1] Source found has no icon, …", "[STATUS-1] Skipped has a neutral icon" (pixel colors), "[STATUS-1] Narrator never reads an icon glyph" |
| TEXT-7 | "Sync to player", with the summary "Adding N songs, removing N songs": parts that are zero are left out, and 1 is singular. | `SyncSummaryTests.Describe`; e2e "Sync button reads 'Sync to player'", "Summary: …" |
| TEXT-8 | Headings: "Spotify playlist:" or "Spotify album:", then the cover, then the name. "Player:", then the icon, then the name (the icon comes after the prefix). | e2e title text checked when opening each page, "The contents page names the player"; manual for cover and icon order |
| TEXT-9 | Folder players show as "Folder (path)". | `PlayersTests.Folder_is_named_by_its_path_once_chosen`; e2e "The contents page names the player" |
| TEXT-10 | A drive root that is a detected player is that player. Other drive roots show the subtitle "Recent folder". | `PlayersTests.Remembered_root_of_a_player_drive_is_that_player`, `.Remembered_root_of_another_drive_is_a_recent_folder` |
| TEXT-11 | Recent folder tiles show the folder's name, with the full path as subtitle. | `PlayersTests.Folder_is_named_by_its_path_once_chosen`, `.Remembered_folder_is_offered_while_it_exists` |
| TEXT-12 | Long paths are trimmed in the middle, with the full path in a tooltip. | `PlayersTests.ShortenPath_keeps_the_drive_and_the_last_folders`, `.ShortDisplayName_shortens_only_folders`; manual for the tooltip |
| TEXT-13 | Never show the same text twice. | `PlayersTests.No_tile_shows_the_same_text_twice`; manual elsewhere |
| TEXT-14 | A row for a file that's only on the player says "Not in playlist" on playlist pages and "Not in album" on album pages; never "not on Spotify". | e2e "A file no longer in the playlist is listed as not in the playlist"; manual for albums |

## Layout

| ID | Rule | Check |
|----|------|-------|
| ALIGN-1 | Back arrows are centered on the heading's cap height (optical midline), on both the library and contents pages, within 1px. | manual (ink-measurement check to come) |
| ALIGN-2 | All the text in a heading shares one baseline, and descenders are never clipped. | manual (ink-measurement check to come) |
| ALIGN-3 | The heading row and banner have exactly the same height and position on every page (player, library and contents): the back arrow, heading baseline and banner baseline sit at the same y, so switching pages doesn't shift the layout. | manual (check coming with the change) |
| SPACE-1 | All spacing comes from the 4px ramp (4, 8, 12, 16, 24, 32). No two visible elements touch or overlap, measured between visual bounds (button backgrounds, text line boxes including descenders). Related elements, such as a label and its action or buttons in one row, are 8px apart; separate groups are at least 16px apart. | manual (UIA check of headers and toolbars coming) |
| CONTENTS-1 | Contents page, top to bottom: row 1 has the playlist heading on the left and the Player line right-justified, with Open folder below it; then the banner; then a toolbar with the track count on the left and Refresh, the sync summary and Sync to player on the right. | e2e "[CONTENTS-1] Contents page layout" |
| CONTENTS-2 | No hero art, and the name isn't repeated, under the contents banner. | e2e "[CONTENTS-2] No hero art or repeated name under the banner" |
| CONTENTS-3 | No Change player button on the contents page. | e2e "The player is changed from the player page, not the playlist" |
| TRACKS-1 | The track list's headers and cells line up to the pixel at window widths from 800 to 1900px. | e2e "At … px the columns line up with the header" |
| TRACKS-2 | The Album column is hidden on album pages. | e2e "Album pages leave out the Album column" |
| TRACKS-3 | # and Time are right-aligned. | e2e "At … px the columns line up with the header" (compares their right edges) |
| TRACKS-4 | Truncated text ends with an ellipsis. | e2e "At … px nothing in the header is cut off"; manual for cells |
| TRACKS-5 | Track list columns fit their content. Status text is never truncated. Title, Artists and Album share the remaining space, and are truncated only when it's genuinely short, with an ellipsis and a tooltip. | manual |
| LOAD-1 | One uniform loading indicator (`LoadingIndicator`). | manual |
| LOAD-2 | At startup, neither the sign-in screen nor the playlist screen flashes before the other. | e2e "No library page before signing in", "Sign-in screen is never shown when already signed in", "Nothing else shows behind the player page" |
| GRID-1 | Library tiles: cover art and 18px titles. No public/private/collaborative badges: Exportify's table and exports don't say, and DATA-1 rules out asking Spotify. | manual |
| GRID-2 | Owner names are hidden when they're "spotify", 8 or more digits, or exactly 25 or 28 lowercase letters and digits, and on "Playlists by you". | `LibraryTests.Is_shown`, `.Generated_owner_ids_are_hidden`, `.Owner_is_shown_except_on_created_playlists_tab` |
| GRID-3 | Liked Songs is pinned first. | `LibraryTests.Liked_songs_is_first_and_never_an_album_wherever_Exportify_lists_it`; e2e "Liked Songs is first" |
| GRID-4 | Spotify's DJ playlist is hidden. | `LibraryTests.Spotify_DJ_is_hidden_but_not_other_playlists_called_DJ` |
| GRID-5 | No cached-only fields on tiles, such as "last added" or duration. | `LibraryTests.Playlists_show_only_what_Exportify_lists_even_after_an_export` |
| GRID-6 | Tabs, in order: Playlists by you \| Playlists by others \| Saved albums \| All. | e2e "[GRID-6] Tabs" |
| WIN-1 | Mica backdrop. | `DesignDecisionSourceTests.WIN_1_The_window_has_a_Mica_backdrop` |
| WIN-2 | The window's minimum width is the narrowest at which the library page's single row (all the tabs, Refresh and Sign out of Spotify) fits unclipped, or 800px if that's wider. Its minimum height is 600px. The tabs stay on the same row as the buttons. | manual |

## Behavior

| ID | Rule | Check |
|----|------|-------|
| PLAYER-1 | The player page shows on every launch. | e2e "MP3 player page comes first" (every run) |
| PLAYER-2 | Players are remembered by device identity, not drive letter. | `PlayersTests.Remembers_a_drive_by_volume_whatever_its_letter`, `.Falls_back_to_volume_serial_and_label` |
| PLAYER-3 | Enter continues with the remembered player. | manual (keys would need focus; see TEST-1) |
| PLAYER-4 | Drive usage bars turn red at 90% used or more. | `PlayersTests.Drive_is_nearly_full_from_90_percent_used` |
| PLAYER-5 | No Refresh button on the player page. | e2e "[PLAYER-5] The player page has no Refresh button" |
| PLAYER-6 | "Other folder" can select the folder it opens in. | `PlayersTests.Folder_dialog_opens_in_the_player_folder_then_the_last_picked_one`; e2e "[PLAYER-6] The folder dialog opens in the player's folder", "[PLAYER-6] Nothing is selected in the folder dialog", "[PLAYER-6] Select Folder is enabled with nothing selected", "[PLAYER-6] Select Folder picked the folder shown" |
| PLAYER-7 | On a player drive, where to sync (and scan, find playlist folders and Open folder) is decided from what's on the card each time it's chosen, never remembered. The app's content is its marked playlist folders and its downloads (an .m4a with an ISRC tag, in the folder itself or in `Artist\Album\`). If the app's content is in a top-level MUSIC folder (any casing, kept as it is), use that folder. Otherwise, if it's at the root, use the root, so nothing is moved or copied again. If it's in both, use the one with more, ties going to MUSIC, and log it. With none of the app's content, use the MUSIC folder if there is one (many players only read it), else the root. Folder players sync where they are. Choosing writes nothing to the player. | `MusicFolderTests.A_card_with_nothing_from_the_app_uses_its_music_folder_with_its_own_casing`, `.Playlist_folders_at_the_root_keep_the_root_even_with_an_empty_music_folder`, `.The_apps_downloads_at_the_root_keep_the_root`, `.The_apps_content_in_the_music_folder_keeps_the_music_folder`, `.With_the_apps_content_in_both_places_the_one_with_more_wins`, `.The_users_own_files_at_the_root_dont_count_as_the_apps`, `.Choosing_writes_nothing`; e2e Run5 "[PLAYER-7] …" |
| SIGNIN-1 | Exportify's "Get started" is clicked automatically. | e2e run 1 signs in with no clicks; "Exportify's own page was never shown" |
| SIGNIN-2 | The sign-in view never shows exportify.net content. | e2e "Exportify's own page was never shown" |
| SIGNIN-3 | Tokens are renewed silently. | e2e "The album stays on screen while the token is renewed", "A token rejected mid-export is renewed without showing the sign-in screen", "Expired token is renewed without showing the sign-in screen" |
| SIGNIN-4 | Links from the sign-in page open outside the app. | e2e "Clicking '…' opens it outside the app and stays on the sign-in page" |
| DEV-1 | WebView2 DevTools are off in Release builds. | `DesignDecisionSourceTests.DEV_1_DevTools_are_only_ever_enabled_in_Debug_builds` |
| SYNC-1 | Matches are found in the background, with a cache. | e2e "Songs are looked for as soon as the playlist opens"; manual for the cache |
| SYNC-2 | Review happens before downloads. | `PlayerSyncerTests.Only_resolved_songs_are_downloaded_and_placed`; e2e "The track list makes way for the review" |
| SYNC-3 | The taskbar shows sync progress. | manual |
| SYNC-4 | Cancel leaves no partial files on the player. | `CopySafetyTests.Cancel_mid_copy_rolls_it_back_and_reports_exactly_what_was_done`; e2e "Cancelling during the downloads leaves the player's files as they were" |
| SYNC-5 | Once a sync finishes, rows always reflect the player as it is now: deleting a synced file updates its row without a Refresh. | e2e "A synced song deleted from the player goes back to being added" |
| SYNC-6 | Refresh on the contents page keeps the user's checkbox choices: Sync boxes are matched by track URI, Remove boxes on player-only rows by path. Rows the user never touched follow the defaults. | e2e "A song the user unticked stays unticked after refreshing", "A file the user chose to remove stays chosen after refreshing", "Songs the user left alone keep their defaults" |
| SYNC-7 | A sync shows a progress bar with "Syncing N of M songs", then a result InfoBar: Success "Synced N songs to <player>", or Informational "Sync cancelled. …". "Cancel sync" uses the default button style, not the accent. | e2e "Progress: …", "Cancelling says nothing on the player changed", "The sync says what it did"; manual for the button style |
| SYNC-8 | Finished downloads are kept on the PC (the app's data folder, `Download cache`, keyed by YouTube video ID and format) until they're on the player, so a sync that's cancelled, closed or crashes resumes without downloading them again. Each is kept as soon as it finishes. A song's copy is removed once it's on the player; the rest after 30 days, or oldest first beyond 4 GiB. | `PlayerSyncerTests.A_sync_cancelled_mid_download_resumes_without_downloading_again`, `.Cancelling_before_anything_is_copied_leaves_the_player_untouched_and_keeps_the_downloads`; `DownloadCacheTests` |
| SYNC-9 | The PC doesn't sleep while a sync runs (the display may still turn off). | manual (`powercfg /requests` lists the app under SYSTEM during a sync; `SleepBlocker`) |
| SYNC-10 | Closing the window during a sync asks "Stop syncing and close?" ("Stop and close" / "Keep syncing", Keep syncing the default). Stopping cancels the sync and waits for it to stop (so SYNC-4 holds) before closing. | manual (e2e check to come) |
| SYNC-11 | Songs are looked for in the background only for the open playlist. Leaving it drops its queued lookups; ones already under way finish, and everything found stays remembered. Playlists that aren't open are never looked for. During a sync only the songs being synced are looked for; other background finding waits until the sync ends. "Finding sources… N of M" (TEXT-15) counts only the open playlist's queued songs. | `OpenPlaylistFindingTests` (e.g. `.Leaving_the_playlist_drops_its_queued_lookups`, `.Nothing_is_looked_for_in_the_background_during_a_sync`); `YouTubeMusicDownloaderTests.Stopping_background_finding_drops_queued_lookups_and_keeps_what_was_found`; manual for the wiring in `MainPage` |
| LOAD-3 | A playlist that fails to load shows "Couldn't load this playlist." with Try again, and hides the sync summary and Sync to player. | e2e "A playlist that did not load offers Try again, and nothing to sync" |
| TEST-1 | Tests never steal focus: the UI runs only off-screen and never activated, through the test host (`tests\Mp3PlayerSync.E2EHost`), on a hidden desktop (`tests\Invoke-EndToEnd.ps1`, whose `-Desktop Current` is only for CI or a person asking for it, never for agents). Agents never launch the app on the user's desktop, except as UPKEEP-1 says. | e2e "The app never took the foreground" |
| UPKEEP-1 | After every successful warm build of `private`, the upkeep session launches the app on the user's desktop so the latest preview is there to try. No other session does. | manual |
| TEST-2 | No test-only code ships in the app: tests reach it through seams (such as a test host), not hooks in production code. Visual checks too. The app takes what it depends on outside itself as `AppServices` (data folder, Exportify and sign-in sites, track finder, download concurrency, showing the window, opening links) in one composition root, `Program.Run`; `tests\Mp3PlayerSync.E2EHost` runs it with the fakes. | `DesignDecisionSourceTests.TEST_2_The_app_has_nothing_that_is_only_for_tests` (no fakes, test hooks or `MP3PLAYERSYNC_` variables in `src`) |
| APP-1 | Only one app instance runs per data directory. A second launch hands over to the running copy and exits, after at most 10 s even if that copy never answers. | e2e "A second launch on the same data folder exits by itself", "A launch on another data folder runs alongside"; `LaunchHandOverTests.A_running_copy_that_never_answers_is_given_up_on_after_the_timeout` |
| PERF-1 | Lookups and downloads each run N at a time, worked out once at startup (never adjusted while running) from the logical processors and free physical memory: N = max(1, min(cap, Ncpu, Nmem)), where Ncpu = max(2, floor(processors × U / f)) with U = 0.5 (half the PC is left for the user) and f = 0.6 (measured CPU time per wall time), and Nmem = floor(free memory × 0.25 / memory per task) with 400 MiB per lookup and 60 MiB per download. cap = 4, in the one constant `DownloadConcurrency.Cap`: with YT-1's pacing, more at once can't go faster. Lookups and downloads share one budget: together at most min(cap, Ncpu) run at once, so the cap and the half of the PC are totals, not per pool. | `DownloadConcurrencyTests` (e.g. `.Lookups_use_about_half_the_processors_up_to_the_cap`, `.An_8_GB_PC_with_3_GB_free_runs_one_lookup_but_more_downloads`, `.Lookups_and_downloads_together_share_one_processor_budget_and_cap`); `YouTubeMusicDownloaderTests.Lookups_and_downloads_share_one_limit`; manual for the wiring (`AppServices.DownloadConcurrency`) |
| YT-1 | Never put the user's IP address at risk with YouTube. Every request to YouTube (lookups, downloads, cover art) goes through one `YouTubeTraffic`, which keeps to 40 requests a minute, 250 in any 10 minutes and 1,000 in any hour, counted as yt-dlp page and API requests (estimated per call; a probe was blocked at about 3,600 an hour). On any 429, bot check or "try again later" (including yt-dlp warnings), or 3 unfixed 403s within 5 minutes, all YouTube traffic stops for a cool-down of 60 min, then 120 and 240 when it happens again within 24 hours of the last one (a real block lasted 36 to 51 minutes). Matches and review choices are kept, and songs aren't marked as failed: they wait. While it lasts, finding and sync progress read "YouTube is busy; continuing in N min". Afterwards one extraction of a well-known video (not a search) checks YouTube before anything else is sent, and the limits are halved for an hour. The state is saved in the data folder (`YouTubeTraffic.json`), in wall-clock UTC, so it survives restarts and reboots; a clock that goes back never ends a cool-down early. It's per data folder: copies on different data folders (APP-1) each keep their own. | `YouTubeTrafficTests`; `YouTubeMusicDownloaderTests.A_throttled_lookup_waits_out_the_cool_down_instead_of_failing`; manual for the message |

## Known gaps on `private`

Where the code doesn't follow a decision yet. Remove an entry in the change that fixes it.

- DEVICE-1: `AppSettings.MusicFolders` remembers each drive's sync root (its MUSIC folder or the root) on the PC; the Player library scan session is removing it.
