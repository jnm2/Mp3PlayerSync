using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mp3PlayerSync.Core;
using Mp3PlayerSync.Core.Sync;
using Mp3PlayerSync.Core.Download;
using Mp3PlayerSync.Core.Placement;
using Mp3PlayerSync.Core.Scanning;
using Mp3PlayerSync.Core.Tagging;
using Mp3PlayerSync.Sync;

namespace Mp3PlayerSync;

/// <summary>
/// Shows Spotify's sign-in (through Exportify) until the user is signed in, then native library and playlist pages.
/// Everything is read from what Exportify shows and exports; the app makes no Spotify requests of its own.
/// </summary>
public sealed partial class MainPage : Page
{
    private static readonly TimeSpan SignInRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SignInGracePeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SignInRevealDelay = TimeSpan.FromMilliseconds(600);

    private readonly AppServices services;
    private readonly ExportifyHost host;
    private readonly DispatcherQueueTimer viewTimer;
    private DispatcherQueueTimer? columnsTimer;
    private TextBlock? measureText;
    private readonly Dictionary<(string Text, bool Strong), double> textWidths = [];
    private readonly SemaphoreSlim exportLock = new(1, 1);
    private readonly string settingsPath;
    private AppSettings settings = new();

    private bool initialized;
    private bool playerChosen;
    private DriveChangeWatcher? driveWatcher;
    private CancellationTokenSource? playerRecheck;
    private readonly ObservableCollection<PlayerOption> playerOptions = [];
    private readonly SemaphoreSlim playerLoadLock = new(1, 1);
    private bool suppressFilterEvents;
    private bool restoredLastSelection;
    private bool restoringAlbum;
    private bool signingOut;
    private DateTimeOffset? lastSignInClick;
    private DateTimeOffset? lastRenewal;
    private DateTimeOffset? unsettledSince; // Since when the page has been somewhere that's neither shown nor clear

    private int libraryVersion;
    private IReadOnlyList<PlaylistSummary>? playlistRows;
    private List<SavedAlbum>? savedAlbums;
    private Task<List<SavedAlbum>>? savedAlbumsLoad;
    private List<LibraryItem>? libraryItems;

    private LibraryItem? currentItem;
    private int contentsVersion;

    // Syncing to the player
    private PlayerLibrary? playerLibrary;
    private YouTubeMusicDownloader? downloader;
    private PlayerSyncer? syncer;
    private ITrackFinder? finder;
    private string? playerRoot;
    private string playerName = "";
    private string? chosenPlayerKey;
    private List<TrackRow>? spotifyRows; // The playlist's tracks, in order
    private Dictionary<string, TrackRow> playerOnlyRows = new(StringComparer.OrdinalIgnoreCase); // By path
    private List<TrackRow>? trackRows; // What's shown: the tracks, with player-only files among them
    private CancellationTokenSource? syncCancellation;
    private TaskCompletionSource? syncFinished;

    public MainPage(AppServices services)
    {
        InitializeComponent();

        this.services = services;
        settingsPath = Path.Combine(services.DataDirectory, "settings.json");
        host = new ExportifyHost(ExportifyWebView, services);

        host.StateChanged += (_, _) => UpdateView();
        host.PlaylistsChanged += (_, message) => OnPlaylistsLoaded(message.Playlists ?? []);

        // Re-evaluates the sign-in grace periods while waiting on them.
        viewTimer = DispatcherQueue.CreateTimer();
        viewTimer.Interval = TimeSpan.FromMilliseconds(500);
        viewTimer.Tick += (_, _) => UpdateView();

        PlayerGridView.ItemsSource = playerOptions;
        Unloaded += (_, _) => { driveWatcher?.Dispose(); playerLibrary?.Dispose(); downloader?.Dispose(); youTubeTraffic?.Dispose(); };
        LayoutUpdated += (_, _) => PlaceOpenFolder();
        ContentsPlayer.Loaded += (_, _) => PlaceOpenFolder();
        OpenPlayerFolderButton.Loaded += (_, _) => PlaceOpenFolder();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;

        settings = AppSettings.Load(settingsPath);
        suppressFilterEvents = true;
        FilterBar.SelectedItem = settings.Filter switch
        {
            LibraryFilter.PlaylistsByOthers => ByOthersFilterItem,
            LibraryFilter.SavedAlbums => SavedAlbumsFilterItem,
            LibraryFilter.All => AllFilterItem,
            _ => ByYouFilterItem,
        };
        suppressFilterEvents = false;

        // The sign-in page is revealed only once loaded; let it sit on the window's Mica until it paints.
        ExportifyWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;

        XamlRoot.Changed += (_, _) => UpdateTitleCover(); // e.g. moved to a screen with another scale

        if (App.MainWindow is { } window)
        {
            driveWatcher = new DriveChangeWatcher(WinRT.Interop.WindowNative.GetWindowHandle(window));
            driveWatcher.DrivesChanged += (_, _) => RecheckPlayers();

            // Also covers changes that send no device notification, such as unlocking an encrypted drive.
            window.Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated && CurrentView == View.Player)
                    _ = LoadPlayersAsync(focusSelected: false);
            };
        }
        _ = LoadPlayersAsync(focusSelected: true);
        _ = UpdateDownloadToolAsync();

        try
        {
            await host.InitializeAsync();
        }
        catch (Exception ex)
        {
            playerChosen = true;
            ShowNative();
            ShowMessage(InfoBarSeverity.Error, "Could not start",
                $"{ex.Message} Make sure the Microsoft Edge WebView2 Runtime is installed.");
            HideLibraryBusy();
        }
    }

    // ---- MP3 player page -------------------------------------------------------------------------------------

    /// <summary>
    /// A drive can take a while to become readable after Windows announces it, so look again over the next seconds.
    /// </summary>
    private async void RecheckPlayers()
    {
        playerRecheck?.Cancel();
        var cancellation = playerRecheck = new CancellationTokenSource();

        foreach (var delay in (int[])[700, 2300, 7000]) // About 0.7, 3 and 10 seconds after the latest change
        {
            try
            {
                await Task.Delay(delay, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (CurrentView == View.Player)
                await LoadPlayersAsync(focusSelected: false);
        }
    }

    /// <param name="focusSelected">Whether to put keyboard focus on the selected player, so Enter continues.</param>
    private async Task LoadPlayersAsync(bool focusSelected)
    {
        await playerLoadLock.WaitAsync();
        try
        {
            var drives = await Task.Run(services.GetDrives);
            var (options, remembered) = Players.BuildOptions(drives, settings.Player, Directory.Exists);

            var selectedKey = PlayerGridView.SelectedItem is PlayerOption selected ? KeyOf(selected) : null;
            var focusedKey = FocusManager.GetFocusedElement(XamlRoot) is GridViewItem { Content: PlayerOption focused } ? KeyOf(focused) : null;

            // Update in place, so tiles that haven't changed don't flicker.
            for (var i = playerOptions.Count - 1; i >= 0; i--)
            {
                if (!options.Any(o => KeyOf(o) == KeyOf(playerOptions[i])))
                    playerOptions.RemoveAt(i);
            }
            for (var i = 0; i < options.Count; i++)
            {
                var existing = IndexOf(KeyOf(options[i]));
                if (existing < 0) playerOptions.Insert(i, options[i]);
                else if (existing != i) playerOptions.Move(existing, i);
                if (playerOptions[i] != options[i]) playerOptions[i] = options[i]; // e.g. free space changed
            }

            // Keep what the user picked, or the player in use; otherwise offer the player chosen last time, if it's here.
            var toSelect = (selectedKey is null ? null : Find(selectedKey) is { Kind: not PlayerOptionKind.OtherFolder } s ? s : null)
                ?? (chosenPlayerKey is null ? null : Find(chosenPlayerKey))
                ?? (remembered is null ? null : Find(KeyOf(remembered)));
            PlayerGridView.SelectedItem = toSelect;

            // A folder chosen last time that turned out to be a player drive's root is remembered as that drive.
            if (remembered?.Drive is not null && settings.Player?.FolderPath is not null)
            {
                settings.Player = Players.ChoiceFor(remembered);
                SaveSettings();
            }

            // Keep the chosen player's details current, e.g. its free space after a sync.
            if (chosenPlayerKey is not null && Find(chosenPlayerKey) is { } chosen) ShowChosenPlayer(chosen);

            var toFocus = focusedKey is null ? (focusSelected ? toSelect : null) : Find(focusedKey);
            if (toFocus is not null && CurrentView == View.Player)
            {
                PlayerGridView.UpdateLayout();
                (PlayerGridView.ContainerFromItem(toFocus) as GridViewItem)?.Focus(FocusState.Programmatic);
            }
        }
        finally
        {
            playerLoadLock.Release();
        }

        int IndexOf(string key) => playerOptions.ToList().FindIndex(o => KeyOf(o) == key);
        PlayerOption? Find(string key) => playerOptions.FirstOrDefault(o => KeyOf(o) == key);
    }

    private async void OnPlayerItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlayerOption option) return;

        if (option.Kind == PlayerOptionKind.OtherFolder)
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                // Back to the player chosen before, so Enter still continues with it.
                PlayerGridView.SelectedItem = chosenPlayerKey is null ? null : playerOptions.FirstOrDefault(o => KeyOf(o) == chosenPlayerKey);
                return;
            }
            option = Players.ForFolder(folder, playerOptions);
        }

        settings.Player = Players.ChoiceFor(option);
        SaveSettings();
        var previousKey = chosenPlayerKey;
        ShowChosenPlayer(option);

        // Look for what's already on the player in the background, ready for when a playlist is opened.
        var root = option.Drive is { } drive ? await SyncFolderAsync(drive) : option.FolderPath!;
        playerName = option.DisplayName;

        if (chosenPlayerKey != previousKey || !string.Equals(root, playerRoot, StringComparison.OrdinalIgnoreCase))
        {
            playerRoot = root;
            _ = ScanPlayerAsync(root);
        }

        playerChosen = true;
        SetView(View.Loading);
        UpdateView();
    }

    /// <summary>
    /// The folder on a player drive that everything is synced into, scanned and searched for playlist folders, decided
    /// from what's on the card (PLAYER-7).
    /// </summary>
    private async Task<string> SyncFolderAsync(DriveCandidate drive)
    {
        var driveFolder = services.DriveFolder(drive);
        var choice = await Task.Run(() => MusicFolder.Choose(driveFolder));
        if (choice.FoundInBoth)
        {
            System.Diagnostics.Debug.WriteLine(
                $"The app's songs are both in {choice.Folder} and at the root of {drive.RootPath} ({choice.InMusic} in the MUSIC folder, {choice.AtRoot} at the root); syncing to {(choice.Folder.Length == 0 ? "the root" : choice.Folder)}.");
        }

        return MusicFolder.PathFor(driveFolder, choice);
    }

    /// <summary>Shows the chosen player: "Player:", its icon, then its name, e.g. SANDISK (E:) or Folder (D:\Music).</summary>
    private void ShowChosenPlayer(PlayerOption option)
    {
        chosenPlayerKey = KeyOf(option);
        SelectedPlayer.Player = option;
        ContentsPlayer.Player = option;
        LayoutPlaylistHeadingRow();
    }

    private static string KeyOf(PlayerOption option) => $"{option.Kind}|{option.Drive?.VolumeId ?? option.Drive?.RootPath}|{option.FolderPath}";

    private async Task<string?> PickFolderAsync()
    {
        // The folder player in use, or else the one remembered from last time.
        var chosen = chosenPlayerKey is null ? null : playerOptions.FirstOrDefault(o => KeyOf(o) == chosenPlayerKey);
        var playerFolder = chosen is null ? settings.Player?.FolderPath : chosen.FolderPath;
        var start = Players.DialogStartFolder(playerFolder, settings.LastPickedFolder, Directory.Exists);
        // Unlike Windows.Storage.Pickers, this one's Select Folder picks the folder it's showing.
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow!.AppWindow.Id);
        // It throws on relative paths, which a hand-edited settings file could hold.
        if (start is not null && Path.IsPathFullyQualified(start)) picker.SuggestedFolder = start;
        var folder = (await picker.PickSingleFolderAsync())?.Path;

        if (folder is not null)
        {
            settings.LastPickedFolder = folder;
            SaveSettings();
        }
        return folder;
    }

    private void OnChangePlayerClick(object sender, RoutedEventArgs e)
    {
        playerChosen = false;
        SetView(View.Player);
        _ = LoadPlayersAsync(focusSelected: true);
        _ = UpdateDownloadToolAsync();
    }

    // ---- Which view is showing -------------------------------------------------------------------------------

    private void UpdateView()
    {
        var now = DateTimeOffset.Now;
        var state = host.State;
        var onExportify = host.IsOnExportify;
        var awaitingSignInPage = lastSignInClick is { } click && now - click < SignInGracePeriod;
        var settled = true;
        var wasUnsettledSince = unsettledSince;
        unsettledSince = null;

        if (host.NavigationFailed)
        {
            signingOut = false;
            ShowNative();
            HideLibraryBusy();
            ShowMessage(InfoBarSeverity.Error, "Could not connect to Spotify", "Check your internet connection, then click Refresh.");
        }
        else if (signingOut && !(onExportify && state is { SignedIn: false }))
        {
            ShowLoading("Signing out…");
            settled = false;
        }
        else if (host.IsShowingSignInPage && now - host.PageLoadedAt >= SignInRevealDelay)
        {
            // Spotify's own sign-in or consent page, revealed once it has loaded and stayed (rather than sending the page
            // straight on, as it does with a live session): Spotify needs the user.
            signingOut = false;
            ShowSignIn(webPage: true);
        }
        else if (onExportify && state is { SignedIn: true })
        {
            lastSignInClick = null;

            // Renew the hour-long token shortly before it runs out, through Spotify's redirects, while the user carries
            // on. Not in the middle of an export, which the redirects would interrupt.
            if (state.RenewSoon && host.IsPageLoaded)
            {
                if (host.IsExporting) settled = false;
                else if (lastRenewal is null || now - lastRenewal > SignInRetryInterval)
                {
                    lastRenewal = now;
                    host.StartSignIn();
                }
            }

            if (libraryItems is not null && !restoringAlbum)
            {
                ShowNative();
                if (!(settings.Filter == LibraryFilter.SavedAlbums && savedAlbumsLoad is not null))
                    HideLibraryBusy();
            }
            else if (!state.PlaylistsReady && !string.IsNullOrWhiteSpace(state.Error))
            {
                ShowNative();
                HideLibraryBusy();
                System.Diagnostics.Debug.WriteLine($"Playlists failed to load: {state.Error}");
                ShowMessage(InfoBarSeverity.Error, "Could not load your playlists", FailureText.ForPageError(state.Error) ?? FailureText.TryAgain);
            }
            else if (CurrentView == View.Native)
            {
                ShowLibraryBusy("Loading your playlists…");
            }
            else
            {
                ShowLoading(restoringAlbum ? "Loading your saved albums…" : "Loading your playlists…");
            }
        }
        else if (onExportify && state is { SigningIn: true })
        {
            if (!KeepViewInBackground()) ShowLoading("Signing in…");
            settled = false;
        }
        else if (onExportify && state is { SignedIn: false })
        {
            signingOut = false;

            // Skip Exportify's landing page: click Get Started, which goes to Spotify's sign-in page, or straight
            // back with a new token when Spotify still has a session (Exportify's tokens only last an hour).
            if (host.IsPageLoaded && !host.IsExporting && (lastSignInClick is null || now - lastSignInClick > SignInRetryInterval))
            {
                lastSignInClick = now;
                awaitingSignInPage = true;
                host.StartSignIn();
            }

            if (awaitingSignInPage || !host.IsPageLoaded || host.IsExporting)
            {
                if (!KeepViewInBackground()) ShowLoading("Connecting to Spotify…");
                settled = false;
            }
            else
            {
                // Spotify sent the user back without signing in, or never showed its page; let them start again.
                ShowSignIn(webPage: false);
            }
        }
        else if (!onExportify && host.IsPageLoaded && host.InSignInFlow && wasUnsettledSince is { } since && now - since > SignInGracePeriod)
        {
            // The sign-in stopped somewhere that isn't Spotify's sign-in, and that is never shown; let them start again.
            unsettledSince = since;
            ShowSignIn(webPage: false);
        }
        else
        {
            // Redirects, loading pages, and anything else that's not yet clear.
            if (!KeepViewInBackground()) ShowLoading(onExportify || state is null ? "Connecting…" : "Connecting to Spotify…");
            unsettledSince = wasUnsettledSince ?? now;
            settled = false;
        }

        if (!settled && !viewTimer.IsRunning) viewTimer.Start();
        else if (settled && viewTimer.IsRunning) viewTimer.Stop();
    }

    private enum View { Player, Loading, SignIn, Native }

    private View CurrentView =>
        PlayerView.Visibility == Visibility.Visible ? View.Player
        : NativePanel.Visibility == Visibility.Visible ? View.Native
        : SignInView.Visibility == Visibility.Visible ? View.SignIn
        : View.Loading;

    // Exactly one view is visible at a time. Until an MP3 player is chosen, that's the player page; the rest of
    // the app (signing in, loading playlists) carries on behind it, ready for when the player is chosen.
    private void SetView(View view)
    {
        if (!playerChosen) view = View.Player;

        PlayerView.Visibility = view == View.Player ? Visibility.Visible : Visibility.Collapsed;
        LoadingView.Visibility = view == View.Loading ? Visibility.Visible : Visibility.Collapsed;
        SignInView.Visibility = view == View.SignIn ? Visibility.Visible : Visibility.Collapsed;
        NativePanel.Visibility = view == View.Native ? Visibility.Visible : Visibility.Collapsed;
        MessageBar.IsOpen = false;

        // Keyboard focus would otherwise stay in the hidden page (or fall to the title bar).
        _ = FocusViewAsync(view);
    }

    private async Task FocusViewAsync(View view)
    {
        // Item containers appear a layout pass or two after the view is shown.
        for (var attempt = 0; attempt < 20 && CurrentView == view; attempt++)
        {
            await Task.Delay(50);

            Control? target = view switch
            {
                View.Player => PlayerGridView,
                View.Native when PlaylistView.Visibility == Visibility.Visible => BackButton,
                View.Native => LibraryGridView,
                _ => null,
            };
            if (target is null) return;

            if (target is ListViewBase list)
            {
                target = list.SelectedItem is { } selected ? list.ContainerFromItem(selected) as Control : list.ContainerFromIndex(0) as Control;
                if (target is null) continue;
            }

            if (target.Focus(FocusState.Programmatic)) return;
        }
    }

    private void ShowLoading(string text)
    {
        LoadingView.Text = text;
        if (CurrentView != View.Loading) SetView(View.Loading);
    }

    private void ShowNative()
    {
        if (CurrentView != View.Native) SetView(View.Native);
    }

    /// <param name="webPage">
    /// Whether to show the web page, which must be Spotify's own. Otherwise a button starts signing in again.
    /// </param>
    private void ShowSignIn(bool webPage)
    {
        ExportifyWebView.Visibility = webPage ? Visibility.Visible : Visibility.Collapsed;
        SignInPrompt.Visibility = webPage ? Visibility.Collapsed : Visibility.Visible;
        if (CurrentView != View.SignIn) SetView(View.SignIn);
    }

    /// <summary>Keeps the library or playlist on screen through background sign-in redirects, such as renewing the token.</summary>
    private bool KeepViewInBackground() => CurrentView == View.Native && !signingOut;

    private void OnSignInPromptClick(object sender, RoutedEventArgs e)
    {
        unsettledSince = null;
        if (host.IsOnExportify && host.IsPageLoaded)
        {
            lastSignInClick = DateTimeOffset.Now;
            host.StartSignIn();
        }
        else
        {
            // Start over from Exportify, which then goes on to Spotify's sign-in.
            lastSignInClick = null;
            host.Reload();
        }
        UpdateView();
    }

    // ---- Library page ----------------------------------------------------------------------------------------

    private void OnPlaylistsLoaded(IReadOnlyList<PlaylistSummary> rows)
    {
        playlistRows = rows;
        RebuildLibrary();

        if (!restoredLastSelection)
        {
            restoredLastSelection = true;
            RestoreLastSelection();
        }

        if (NeedsSavedAlbums)
            _ = LoadSavedAlbumsForListAsync();

        UpdateView();
    }

    private void RebuildLibrary()
    {
        if (playlistRows is null) return;

        libraryItems = LibraryBuilder.Build(playlistRows, savedAlbums, settings.Exports);
        ShowLibraryItems();

        if (currentItem is not null)
        {
            currentItem = libraryItems.FirstOrDefault(i => i.Key == currentItem.Key) ?? currentItem;
            ShowItemHeading(currentItem); // An export can show a playlist is really an album
        }
    }

    private async void RestoreLastSelection()
    {
        if (settings.LastSelectedKey is not { } key || libraryItems is null) return;

        if (libraryItems.FirstOrDefault(i => i.Key == key) is { } item)
        {
            OpenItem(item);
            return;
        }

        if (!key.StartsWith(SavedAlbum.KeyPrefix, StringComparison.Ordinal) || !HasSavedAlbumsRow) return;

        // Albums come from Exportify's Saved Albums export; keep the loading view up until it's read.
        restoringAlbum = true;
        UpdateView();
        try
        {
            await EnsureSavedAlbumsAsync(force: false);
            if (libraryItems?.FirstOrDefault(i => i.Key == key) is { } album && currentItem is null)
                OpenItem(album);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowMessage(InfoBarSeverity.Error, "Could not load your saved albums", ex.Message);
        }
        finally
        {
            restoringAlbum = false;
            UpdateView();
        }
    }

    private bool HasSavedAlbumsRow => playlistRows?.Any(r => r.Key == LibraryBuilder.SavedAlbumsUrl) == true;

    private bool NeedsSavedAlbums => settings.Filter is LibraryFilter.SavedAlbums or LibraryFilter.All
        && savedAlbums is null && savedAlbumsLoad is null && HasSavedAlbumsRow;

    private async Task LoadSavedAlbumsForListAsync()
    {
        var showBusy = settings.Filter == LibraryFilter.SavedAlbums;
        if (showBusy) ShowLibraryBusy("Loading your saved albums…");

        try
        {
            await EnsureSavedAlbumsAsync(force: false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowMessage(InfoBarSeverity.Error, "Could not load your saved albums", ex.Message);
        }
        finally
        {
            if (libraryItems is not null) HideLibraryBusy();
        }
    }

    /// <summary>
    /// Reads saved albums from Exportify's Saved Albums export (all saved albums' tracks in one CSV).
    /// </summary>
    private async Task<List<SavedAlbum>> EnsureSavedAlbumsAsync(bool force)
    {
        if (!force)
        {
            if (savedAlbums is not null) return savedAlbums;
            if (savedAlbumsLoad is not null) return await savedAlbumsLoad;
        }

        var version = libraryVersion;
        var load = ExportSavedAlbumsAsync();
        savedAlbumsLoad = load;
        try
        {
            var albums = await load;
            if (version == libraryVersion)
            {
                savedAlbums = albums;
                RebuildLibrary();
            }
            return albums;
        }
        finally
        {
            if (savedAlbumsLoad == load) savedAlbumsLoad = null;
        }

        async Task<List<SavedAlbum>> ExportSavedAlbumsAsync()
        {
            var (export, _) = await ExportAsync(LibraryBuilder.SavedAlbumsUrl);
            return SavedAlbum.FromExport(export);
        }
    }

    private void ShowLibraryItems()
    {
        if (libraryItems is null)
        {
            LibraryGridView.ItemsSource = null;
            LibrarySummaryText.Text = "";
            return;
        }

        var items = LibraryBuilder.Present(libraryItems, settings.Filter, DateTimeOffset.Now);
        LibraryGridView.ItemsSource = items;
        UpdateTileSize();
        LibraryGridView.SelectedItem = items.FirstOrDefault(i => i.Key == settings.LastSelectedKey);

        LibrarySummaryText.Text = settings.Filter switch
        {
            LibraryFilter.SavedAlbums => Count(items.Count, "album", "albums"),
            LibraryFilter.All => Count(items.Count, "playlist or album", "playlists and albums"),
            _ => Count(items.Count, "playlist", "playlists"),
        };

        static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
    }

    private void OnFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (suppressFilterEvents) return;

        settings.Filter = sender.SelectedItem == ByOthersFilterItem ? LibraryFilter.PlaylistsByOthers
            : sender.SelectedItem == SavedAlbumsFilterItem ? LibraryFilter.SavedAlbums
            : sender.SelectedItem == AllFilterItem ? LibraryFilter.All
            : LibraryFilter.PlaylistsByYou;
        SaveSettings();
        ShowLibraryItems();
        MessageBar.IsOpen = false;

        if (NeedsSavedAlbums)
            _ = LoadSavedAlbumsForListAsync();
        else if (settings.Filter == LibraryFilter.SavedAlbums && savedAlbums is null && savedAlbumsLoad is not null)
            ShowLibraryBusy("Loading your saved albums…");
        else if (libraryItems is not null)
            HideLibraryBusy();
    }

    // Fits as many columns of at least MinTileWidth as the width allows, stretching tiles to fill each row.
    private void OnLibraryGridSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileSize();

    private bool toolbarMeasured;

    /// <summary>
    /// Once the playlist page is first laid out, keeps the window wide enough for its tabs, Refresh and Sign out to
    /// fit on one line, unclipped.
    /// </summary>
    private void OnLibraryToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (toolbarMeasured || e.NewSize.Width <= 0 || XamlRoot is not { } root || App.MainWindow is not MainWindow window) return;
        toolbarMeasured = true;

        var parts = new FrameworkElement[] { FilterBar, RefreshLibraryButton, SignOutButton };
        var natural = 0d;
        foreach (var part in parts)
        {
            part.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            natural += part.DesiredSize.Width;
            part.InvalidateMeasure(); // Lay it out again in the room it has
        }
        natural += LibraryToolbar.ColumnSpacing * (parts.Length - 1);

        // Everything around the row (margins, and so on) stays the same as the window narrows.
        var around = root.Size.Width - e.NewSize.Width;
        window.RequireContentWidth(natural + around);
    }

    private void UpdateTileSize()
    {
        if (LibraryGridView.ItemsPanelRoot is not ItemsWrapGrid panel || LibraryGridView.ActualWidth <= 0)
        {
            // The panel appears once the grid is first shown with items.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (LibraryGridView.ItemsPanelRoot is ItemsWrapGrid && LibraryGridView.ActualWidth > 0) UpdateTileSize();
            });
            return;
        }

        const double MinTileWidth = 180, TileMargin = 8, TilePadding = 16, CoverToText = 10;
        var available = LibraryGridView.ActualWidth - LibraryGridView.Padding.Left - LibraryGridView.Padding.Right - 4;
        var columns = Math.Max(1, (int)(available / MinTileWidth));
        var width = Math.Floor(available / columns);
        var cover = width - TileMargin - TilePadding;

        panel.ItemWidth = width;
        panel.ItemHeight = TileMargin + TilePadding + cover + CoverToText + (double)Resources["TileTextHeight"];
    }

    private void OnLibraryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryItem item)
            OpenItem(item);
    }

    private void OnRefreshLibraryClick(object sender, RoutedEventArgs e)
    {
        ResetLibrary();
        MessageBar.IsOpen = false;
        ShowLibraryBusy("Loading your playlists…");
        host.Reload();
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        signingOut = true;
        ShowLoading("Signing out…");
        settings.LastSelectedKey = null;
        SaveSettings();
        ResetLibrary();
        CloseItem();
        await host.SignOutAsync();
    }

    private void ResetLibrary()
    {
        libraryVersion++;
        playlistRows = null;
        savedAlbums = null;
        savedAlbumsLoad = null;
        libraryItems = null;
        ShowLibraryItems();
    }

    // ---- Playlist page ---------------------------------------------------------------------------------------

    private void OpenItem(LibraryItem item)
    {
        currentItem = item;
        settings.LastSelectedKey = item.Key;
        SaveSettings();

        LibraryView.Visibility = Visibility.Collapsed;
        PlaylistView.Visibility = Visibility.Visible;
        ShowItemHeading(item);
        MessageBar.IsOpen = false;
        ClearTracks();

        _ = LoadContentsAsync(item, refresh: false);
    }

    private void ShowItemHeading(LibraryItem item)
    {
        var kind = PlacementPlanner.UsesAlbumLayout(item.Kind) ? "Spotify album:" : "Spotify playlist:";
        PlaylistKindText.Text = kind;
        PlaylistTitle.Text = item.Name;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaylistTitle, $"{kind} {item.Name}");
        TitleCover.Background = LibraryVisuals.TileBackground(item.Kind);
        TitleCoverGlyph.Glyph = item.Glyph;
        TitleCoverGlyph.Foreground = LibraryVisuals.GlyphForeground(item.Kind);
        titleCoverUrl = item.CoverUrl;
        titleCoverScale = 0;
        UpdateTitleCover();
        LayoutPlaylistHeadingRow();
    }

    private string? titleCoverUrl;
    private double titleCoverScale;

    // Decodes the heading's small cover for the screen it's on, again when that changes.
    private void UpdateTitleCover()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        if (scale == titleCoverScale) return;
        titleCoverScale = scale;
        TitleCoverImage.Source = LibraryVisuals.SmallCover(titleCoverUrl, TitleCover.Width, scale);
    }

    // The player sits at the right of the heading's line when both fit, and under the banner's title when not.
    private void LayoutPlaylistHeadingRow()
    {
        var available = PlaylistView.ActualWidth - PlaylistView.Padding.Left - PlaylistView.Padding.Right;
        if (available <= 0) return;
        var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        BackButton.Measure(unbounded);
        PlaylistHeading.Measure(unbounded);
        ContentsPlayer.ClearValue(MaxWidthProperty);
        ContentsPlayer.Measure(unbounded);
        var besideHeading = available - BackButton.DesiredSize.Width - 2 * PlaylistHeadingRow.ColumnSpacing - PlaylistHeading.DesiredSize.Width;
        var underBanner = ContentsPlayer.DesiredSize.Width > besideHeading;

        var parent = (Panel)ContentsPlayer.Parent;
        var target = underBanner ? (Panel)ContentsBanner : PlaylistHeadingRow;
        if (parent != target)
        {
            parent.Children.Remove(ContentsPlayer);
            target.Children.Add(ContentsPlayer);
        }
        ContentsPlayer.HorizontalAlignment = underBanner ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ContentsPlayer.Margin = underBanner ? new Thickness(0) : new Thickness(24, 0, 0, 0);
        ContentsPlayer.MaxWidth = underBanner ? available - ContentsBanner.Padding.Left : double.PositiveInfinity;

        // Open folder stays just under the player, wherever the player is.
        var folderParent = (Panel)OpenPlayerFolderButton.Parent;
        var folderTarget = underBanner ? (Panel)ContentsBanner : ContentsBannerTitleRow;
        if (folderParent != folderTarget)
        {
            folderParent.Children.Remove(OpenPlayerFolderButton);
            folderTarget.Children.Add(OpenPlayerFolderButton);
        }
        OpenPlayerFolderButton.HorizontalAlignment = underBanner ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        openFolderUnderBanner = underBanner;
        PlaceOpenFolder();
    }

    private bool openFolderUnderBanner;

    // Related elements are 8px apart (SPACE-1), measured from the player's text line, descenders included, to the
    // button's background. Measured from where things were actually arranged, so no number here is a guess.
    private const double RelatedSpacing = 8;

    private void PlaceOpenFolder()
    {
        // Until both are in the page's tree (just after moving), there's nothing to measure; their Loaded events call back.
        if (PlaylistView.Visibility != Visibility.Visible || ContentsPlayer.Content is not FrameworkElement text
            || text.XamlRoot is null || OpenPlayerFolderButton.XamlRoot is null || text.ActualHeight <= 0) return;
        var container = (FrameworkElement)OpenPlayerFolderButton.Parent;
        // The text's own box: its full line, descenders included.
        var textBottom = text.TransformToVisual(container).TransformPoint(new(0, text.ActualHeight)).Y;
        // The button's top, as laid out without its top margin.
        var slotTop = OpenPlayerFolderButton.TransformToVisual(container).TransformPoint(new(0, 0)).Y - OpenPlayerFolderButton.Margin.Top;
        // Rounded up, so the gap is never under 8px whichever way the text's fractional bottom is drawn.
        var top = Math.Ceiling(Math.Ceiling(textBottom) + RelatedSpacing - slotTop);
        var margin = openFolderUnderBanner ? new Thickness(0, top, 0, 0) : new Thickness(0, top, -32, 0);
        if (OpenPlayerFolderButton.Margin != margin) OpenPlayerFolderButton.Margin = margin;
    }

    private void CloseItem()
    {
        currentItem = null;
        contentsVersion++;
        HidePlaylistBusy();
        ClearTracks();
        PlaylistView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        CloseItem();
        MessageBar.IsOpen = false;
        LibraryGridView.SelectedItem = (LibraryGridView.ItemsSource as List<LibraryItem>)?.FirstOrDefault(i => i.Key == settings.LastSelectedKey);
    }

    private void OnRefreshPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (currentItem is { } item)
        {
            MessageBar.IsOpen = false;
            _ = LoadContentsAsync(item, refresh: true);
        }
    }

    private async Task LoadContentsAsync(LibraryItem item, bool refresh)
    {
        var version = ++contentsVersion;
        bool IsCurrent() => version == contentsVersion;
        Finding.Stop(); // The loaded rows are looked for afresh once they're shown

        RefreshPlaylistButton.IsEnabled = false;
        LoadFailedPanel.Visibility = Visibility.Collapsed;
        ShowPlaylistBusy($"Loading “{item.Name}”…");

        try
        {
            ExportedPlaylist contents;
            string? warnings = null;

            if (item.IsSavedAlbum)
            {
                var albums = await EnsureSavedAlbumsAsync(force: refresh);
                contents = albums.FirstOrDefault(a => a.Key == item.Key)?.ToExportedPlaylist()
                    ?? throw new ExportifyException("This album is no longer in your saved albums.");
            }
            else
            {
                (contents, warnings) = await ExportAsync(item.Key);
                RememberExport(item, contents);
            }

            if (!IsCurrent()) return;

            // A refresh keeps what the user ticked and unticked, for the songs still there (the nth copy of a song
            // for its nth copy), and which files they chose to remove.
            var earlier = refresh && spotifyRows is { } previous ? Occurrences(previous) : null;
            spotifyRows = contents.Tracks.Select(t => new TrackRow(t)).ToList();
            if (earlier is not null)
            {
                foreach (var (key, row) in Occurrences(spotifyRows))
                {
                    if (earlier.GetValueOrDefault(key) is { } before) row.KeepChoiceFrom(before);
                }
            }
            foreach (var row in spotifyRows)
            {
                // What was found before, this session or (except failed searches) an earlier one, shows at once.
                var known = found.GetValueOrDefault(FindKey(row.Track!)) ?? Finder.GetCachedResolution(row.Track!);
                if (known is not null)
                {
                    found[FindKey(row.Track!)] = known;
                    ShowFound(row, known);
                }
            }
            if (!refresh) playerOnlyRows.Clear();
            TracksListView.ItemsSource = trackRows = spotifyRows;
        WatchRows(trackRows);
            ShowWhatsOnPlayer();
            TrackHeader.Visibility = Visibility.Visible;
            var count = contents.Tracks.Count;
            var byline = item.IsSavedAlbum ? LibraryBuilder.Present(new[] { item }, LibraryFilter.All, DateTimeOffset.Now)[0].Byline : "";
            SummaryText.Text = $"{(byline.Length > 0 ? byline + " — " : "")}{count} {(count == 1 ? "track" : "tracks")}, {ExportedTrack.FormatDuration(contents.TotalDuration)}";

            if (!string.IsNullOrWhiteSpace(warnings))
                ShowMessage(InfoBarSeverity.Warning, "Some details could not be loaded", warnings);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!IsCurrent()) return;
            ClearTracks();
            LoadFailedPanel.Visibility = Visibility.Visible;
            _ = FocusAsync(TryAgainButton);
            ShowMessage(InfoBarSeverity.Error, $"Could not load “{item.Name}”", ex.Message);
        }
        finally
        {
            if (IsCurrent())
            {
                HidePlaylistBusy();
                RefreshPlaylistButton.IsEnabled = true;
            }
        }
    }

    // Lets the library show the playlist's length and last added date, and corrects whether it looks like an album.
    private void RememberExport(LibraryItem item, ExportedPlaylist contents)
    {
        var summary = ExportSummary.From(contents);
        if (settings.Exports.TryGetValue(item.Key, out var known) && known == summary) return;

        settings.Exports[item.Key] = summary;
        SaveSettings();
        RebuildLibrary();
    }

    /// <summary>
    /// Clicks the row's Export button on Exportify and parses the CSV it produces. Exportify exports one at a time.
    /// </summary>
    private async Task<(ExportedPlaylist Export, string? Warnings)> ExportAsync(string key)
    {
        await exportLock.WaitAsync();
        try
        {
            var result = await host.ExportAsync(key);
            var csv = result.Csv ?? "";
            try
            {
                return (await Task.Run(() => ExportifyCsv.Parse(csv)), result.Warnings);
            }
            catch (FormatException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not read the export: {ex.Message}");
                throw new ExportifyException(FailureText.TryAgain);
            }
        }
        finally
        {
            exportLock.Release();
        }
    }

    // ---- Syncing ---------------------------------------------------------------------------------------------

    /// <summary>Indexes what's on the player, keeping up with changes, for as long as it's the chosen player.</summary>
    private async Task ScanPlayerAsync(string root)
    {
        playerLibrary?.Dispose();
        var library = playerLibrary = new PlayerLibrary(root);
        library.IndexChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowWhatsOnPlayer);
        library.Start();
        ShowWhatsOnPlayer();

        try
        {
            await library.ScanCompletion;
        }
        catch (OperationCanceledException)
        {
            return; // Another player was chosen
        }
        catch (Exception ex) when (playerLibrary == library)
        {
            ShowMessage(InfoBarSeverity.Error, $"Could not read {playerName}", ex.Message);
        }

        if (playerLibrary == library) ShowWhatsOnPlayer();
    }

    /// <summary>
    /// Marks tracks already on the player. Once the player has been checked, tracks not on it are selected by
    /// default, unless the user has already chosen.
    /// </summary>
    private void ShowWhatsOnPlayer()
    {
        var scanning = IsCheckingPlayer;
        CheckingPlayerPanel.Visibility = scanning && trackRows is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        UpdateFindingProgress();

        if (spotifyRows is { Count: > 0 } tracks && playerLibrary is { } library)
        {
            var snapshot = library.GetSnapshot();
            var exported = tracks.Select(r => r.Track!).ToList();

            // A playlist's tracks are on the player when they're in its own folder; an album's, when they're anywhere.
            var entries = currentItem is { } item && !PlacementPlanner.UsesAlbumLayout(item.Kind)
                ? (PlaylistFolderOnPlayer(item, library.RootPath) is { } folder
                    ? PlaylistPlanner.Plan(folder, exported, library.RootPath, snapshot.Files, maxRelativePathLength: PlacementPlanner.MaxRelativePathLengthFor(library.RootPath))
                    : PlaylistPlanner.Plan(item.Name, exported, library.RootPath, snapshot.Files, maxRelativePathLength: PlacementPlanner.MaxRelativePathLengthFor(library.RootPath)))
                    .Rows.Select(r => (OnPlayer: r.Track?.State is PlaylistTrackState.InPlace or PlaylistTrackState.Renumber, PlayerOnly: r.PlayerOnlyFile,
                        CopySize: r.Track is { State: PlaylistTrackState.CopyFromPlayer, CopySourceRelativePath: { } source } ? SizeOnPlayer(source) : null))
                    .ToList()
                : snapshot.Match(exported).Select(m => (OnPlayer: m.IsOnPlayer, PlayerOnly: (PlayerOnlyFile?)null, CopySize: (long?)null)).ToList();
            long? SizeOnPlayer(string relativePath) =>
                snapshot.Files.FirstOrDefault(f => string.Equals(f.Path, Path.Combine(library.RootPath, relativePath), StringComparison.OrdinalIgnoreCase))?.Size;

            var rows = new List<TrackRow>(entries.Count);
            var keptPlayerOnly = new Dictionary<string, TrackRow>(StringComparer.OrdinalIgnoreCase);
            var next = 0;
            foreach (var (onPlayer, playerOnlyFile, copySize) in entries)
            {
                if (playerOnlyFile is { } file)
                {
                    var playerOnly = playerOnlyRows.GetValueOrDefault(file.RelativePath) ?? new TrackRow(file.File, file.RelativePath, inAlbum: currentItem is { } open && PlacementPlanner.UsesAlbumLayout(open.Kind));
                    keptPlayerOnly[file.RelativePath] = playerOnly;
                    rows.Add(playerOnly);
                    continue;
                }

                var row = tracks[next++];
                row.CopySize = copySize;
                // Whether a song is on the player always comes from what's on the player now, except while it's
                // being synced; a song synced earlier and then deleted goes back to being added.
                if (!row.IsSyncing)
                {
                    // Not while syncing: the index can briefly miss a file that has just been copied.
                    if (row.SyncState == TrackSyncState.Done && !onPlayer && syncCancellation is null) row.SyncState = null;
                    row.IsOnPlayer = onPlayer;
                    if (!scanning) row.SetDefaultSelection(!row.IsOnPlayer);
                }
                rows.Add(row);
            }

            playerOnlyRows = keptPlayerOnly;
            if (trackRows is null || !rows.SequenceEqual(trackRows))
                TracksListView.ItemsSource = trackRows = rows;
        WatchRows(trackRows);
        }

        RemoveHeader.Visibility = playerOnlyRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSyncControls();
        if (!scanning)
        {
            StartFinding();
            _ = CheckPlayerVolumeAsync();
        }
    }

    // ---- Finding songs ahead of syncing ----------------------------------------------------------------------

    // What looking for each song found, this session, so going back to a playlist shows it at once.
    private readonly Dictionary<string, TrackResolution> found = new(StringComparer.Ordinal);

    // Only the open playlist's songs are looked for in the background, and none during a sync (SYNC-11).
    private OpenPlaylistFinding Finding => field ??= new(() => Finder);

    /// <summary>Each row keyed by its song and which copy of the song it is, for songs in a playlist more than once.</summary>
    private static Dictionary<string, TrackRow> Occurrences(IEnumerable<TrackRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new Dictionary<string, TrackRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = FindKey(row.Track!);
            var n = counts[key] = counts.GetValueOrDefault(key) + 1;
            result[$"{key}\n#{n}"] = row;
        }
        return result;
    }

    private static string FindKey(ExportedTrack track) => track.Uri.Length > 0 ? track.Uri : $"{track.Title}\n{track.ArtistsText}\n{track.Album}";

    /// <summary>
    /// Has the open playlist's songs that aren't on the player yet looked for in the background, so that syncing
    /// can start downloading straight away and uncertain matches can be settled first. This takes the place of
    /// whatever was being looked for before; anything already found is kept. Nothing is looked for during a sync.
    /// </summary>
    private void StartFinding()
    {
        if (IsCheckingPlayer || spotifyRows is not { } rows) return;

        var toFind = rows.Where(r => !r.IsOnPlayer && !r.IsSkipped && !found.ContainsKey(FindKey(r.Track!)))
            .Select(r => r.Track!)
            .DistinctBy(FindKey)
            .ToList();
        if (Finding.Find(toFind, FindKey) is not { } known) return;

        var queued = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < toFind.Count; i++)
        {
            if (known[i] is { } resolution) Remember(resolution);
            else queued.Add(FindKey(toFind[i]));
        }
        foreach (var row in rows)
        {
            if (queued.Contains(FindKey(row.Track!))) row.IsFinding = true;
        }
        findingTotal = rows.Count(r => r.IsFinding);
        UpdateFindingProgress();
        UpdateColumns();
    }

    // How many songs the current background search started with, for "Finding sources… 120 of 249".
    private int findingTotal;

    /// <summary>Shows how many of the songs being looked for have been found, while any still are.</summary>
    private void UpdateFindingProgress()
    {
        var left = spotifyRows?.Count(r => r.IsFinding) ?? 0;
        if (left == 0)
        {
            findingTotal = 0;
            FindingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        findingTotal = Math.Max(findingTotal, left);
        FindingProgressText.Text = youTubeTraffic?.BusyText ?? $"Finding sources… {findingTotal - left} of {findingTotal}";
        FindingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Stops looking for the open playlist's songs, e.g. when leaving it. What was found is kept.</summary>
    private void StopFinding()
    {
        Finding.Stop();
        foreach (var row in spotifyRows ?? []) row.IsFinding = false;
        UpdateFindingProgress();
    }

    private void OnLookupProgress(TrackResolutionProgress update)
    {
        if (update is { Stage: TrackResolutionStage.Completed, Resolution: { } resolution }) Remember(resolution);
    }
    /// <summary>Keeps what was found for a song, and shows it on its rows in the open playlist.</summary>
    private void Remember(TrackResolution resolution)
    {
        var key = FindKey(resolution.Track);
        found[key] = resolution;
        foreach (var row in spotifyRows ?? [])
        {
            if (FindKey(row.Track!) == key) ShowFound(row, resolution);
        }
        UpdateSyncControls();
    }

    private void Forget(ExportedTrack track)
    {
        var key = FindKey(track);
        found.Remove(key);
        foreach (var row in spotifyRows ?? [])
        {
            if (FindKey(row.Track!) == key) row.Found = null;
        }
    }

    private static void ShowFound(TrackRow row, TrackResolution resolution)
    {
        row.IsFinding = false;
        row.Found = resolution.State;
        if (resolution.State == TrackResolutionState.Skipped) row.IsSkipped = true;
    }

    /// <summary>What was found for a song before, if it can be used as it is, for this playlist's copy of the song.</summary>
    private TrackResolution? KnownFind(ExportedTrack track) =>
        found.GetValueOrDefault(FindKey(track)) is { State: not TrackResolutionState.LookupFailed } known
            ? known with { Track = track }
            : null;



    /// <summary>
    /// Opens File Explorer on the open playlist's folder or album's folder on the player, if it has one yet, or
    /// else on the player itself.
    /// </summary>
    private void OnOpenPlayerFolderClick(object sender, RoutedEventArgs e)
    {
        if (playerRoot is not { } root) return;
        var folder = currentItem is { } item && playerLibrary is { } library ? FolderOnPlayer(item, library) : null;
        var path = folder is not null && Directory.Exists(folder) ? folder : root;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { path } })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Could not open the folder", ex.Message);
        }
    }

    /// <returns>The playlist's folder, or the one folder that has all of the album's songs on the player, if any.</returns>
    private string? FolderOnPlayer(LibraryItem item, PlayerLibrary library)
    {
        if (!PlacementPlanner.UsesAlbumLayout(item.Kind))
            return PlaylistFolderOnPlayer(item, library.RootPath) is { } folder ? Path.Combine(library.RootPath, folder.CurrentRelativePath) : null;

        var tracks = (spotifyRows ?? []).Where(r => r.Track is not null).Select(r => r.Track!).ToList();
        var folders = library.GetSnapshot().Match(tracks)
            .SelectMany(m => m.Files)
            .Select(f => Path.GetDirectoryName(f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return folders is [{ } only] && !string.Equals(Path.TrimEndingDirectorySeparator(only), Path.TrimEndingDirectorySeparator(library.RootPath), StringComparison.OrdinalIgnoreCase)
            ? only
            : null;
    }

    /// <summary>The open playlist's folder on the player (which may not exist yet), or null if the player can't be read.</summary>
    private static PlaylistFolderAssignment? PlaylistFolderOnPlayer(LibraryItem item, string root)
    {
        try
        {
            return PlayerSyncer.FolderFor(root, new SyncRequest(item.Kind, item.Key, item.Name, item.Owner, [], [], []));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private YouTubeMusicDownloader Downloader => downloader ??= new YouTubeMusicDownloader(new()
    {
        ToolCacheDirectory = Path.Combine(services.DataDirectory, "Tools"),
        ResolutionCachePath = Path.Combine(services.DataDirectory, "YouTubeMatches.json"),
        MaxParallelLookups = services.DownloadConcurrency.MaxParallelLookups,
        MaxParallelDownloads = services.DownloadConcurrency.MaxParallelDownloads,
        MaxParallelLookupsAndDownloads = services.DownloadConcurrency.MaxParallelLookupsAndDownloads,
        Traffic = Traffic,
    });

    /// <summary>Paces everything sent to YouTube, and stops it while YouTube pushes back, even across restarts.</summary>
    private YouTubeTraffic Traffic
    {
        get
        {
            if (youTubeTraffic is null)
            {
                youTubeTraffic = new YouTubeTraffic(Path.Combine(services.DataDirectory, "YouTubeTraffic.json"), cancellationToken => Downloader.CheckYouTubeAsync(cancellationToken));
                youTubeTraffic.BusyChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowYouTubeBusy);
                DispatcherQueue.TryEnqueue(ShowYouTubeBusy);
            }
            return youTubeTraffic;
        }
    }

    private YouTubeTraffic? youTubeTraffic;
    private DispatcherQueueTimer? youTubeBusyTimer;

    /// <summary>Shows "YouTube is busy; continuing in N min" where finding and syncing progress is, while it is.</summary>
    private void ShowYouTubeBusy()
    {
        var busy = youTubeTraffic?.BusyText is not null;
        if (busy && youTubeBusyTimer is null)
        {
            youTubeBusyTimer = DispatcherQueue.CreateTimer();
            youTubeBusyTimer.Interval = TimeSpan.FromSeconds(15);
            youTubeBusyTimer.Tick += (_, _) => ShowYouTubeBusy();
        }
        if (busy) youTubeBusyTimer!.Start();
        else youTubeBusyTimer?.Stop();

        UpdateFindingProgress();
        if (syncingRows is not null) UpdateTaskbar();
    }

    private ITrackFinder Finder
    {
        get
        {
            if (finder is null)
            {
                finder = services.CreateTrackFinder(Downloader);
                finder.LookupProgress += (_, update) => DispatcherQueue.TryEnqueue(() => OnLookupProgress(update));
            }
            return finder;
        }
    }

    private PlayerSyncer Syncer => syncer ??= new PlayerSyncer(
        Finder,
        new TrackTagWriter(new HttpAlbumArtFetcher(new HttpClient(), new WindowsAlbumArtConverter(), Traffic)),
        Path.Combine(services.DataDirectory, "Downloads"))
    {
        IsWriteProtected = DriveProbe.IsWriteProtected,
        Cache = new DownloadCache(Path.Combine(services.DataDirectory, "Download cache")),
    };

    /// <summary>Gets the latest yt-dlp ready before the first sync. Offline, a copy fetched earlier is used.</summary>
    private async Task UpdateDownloadToolAsync()
    {
        try
        {
            await Downloader.UpdateToolAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update yt-dlp: {ex.Message}");
        }
    }

    // Until the first scan finishes (or fails), selections wait for it.
    private bool IsCheckingPlayer => playerLibrary is { ScanCompletion.IsCompleted: false };

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        // Clicking cycles unchecked -> checked -> indeterminate; treat indeterminate as "select none".
        var select = SelectAllCheckBox.IsChecked == true;
        foreach (var row in spotifyRows ?? [])
        {
            if (row.CanCheck) row.IsSelected = select;
        }
        UpdateSyncControls();
    }

    private void OnTrackCheckBoxClick(object sender, RoutedEventArgs e) => UpdateSyncControls();

    private void UpdateSyncControls()
    {
        var syncing = syncCancellation is not null;
        var rows = spotifyRows ?? [];
        var selected = rows.Count(r => r.IsSelected);
        var selectable = rows.Count(r => !r.IsSkipped);
        var addingRows = rows.Where(r => r.IsSelected && !r.IsOnPlayer && r.Found != TrackResolutionState.Unavailable).ToList();
        var adding = addingRows.Count;
        var toReview = rows.Where(r => r.IsSelected && !r.IsOnPlayer && r.NeedsReview).Select(r => FindKey(r.Track!)).Distinct().Count();
        ReviewMatchesButton.Content = $"Review {toReview} {(toReview == 1 ? "song" : "songs")}";
        ReviewMatchesButton.Visibility = toReview > 0 && !syncing && ReviewPanel.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        var removals = playerOnlyRows.Values.Count(r => r.IsMarkedForRemoval);
        SyncSummaryText.Text = syncing ? "" : SyncSummary.Describe(adding, removals);

        SelectAllCheckBox.IsChecked = selected == 0 ? false : selected == selectable ? true : null;
        SelectAllCheckBox.IsEnabled = !syncing && selectable > 0;

        SyncButtonGlyph.Glyph = syncing ? "\uE711" : "\uE895";
        SyncButtonText.Text = syncing ? "Cancel sync" : "Sync to player";
        SyncButton.Style = (Style)Application.Current.Resources[syncing ? "DefaultButtonStyle" : "AccentButtonStyle"];
        var problem = syncing ? null : PlayerProblem(addingRows);
        PlayerProblemText.Text = problem ?? "";
        PlayerProblemPanel.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        SyncButton.IsEnabled = syncing || (adding + removals > 0 && !IsCheckingPlayer && playerRoot is not null && problem is null);
        // Nothing to sync on a playlist that didn't load; the review step has its own buttons.
        SyncButton.Visibility = SyncSummaryText.Visibility = spotifyRows is null || ReviewPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        BackButton.IsEnabled = RefreshPlaylistButton.IsEnabled = !syncing;
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        var showAlbum = currentItem is not { } item || !PlacementPlanner.UsesAlbumLayout(item.Kind);
        TrackColumns.Shared.Update(showAlbum, trackRows?.Any(r => r.Status.Length > 0) == true, playerOnlyRows.Count > 0);
        AlbumHeader.Visibility = showAlbum ? Visibility.Visible : Visibility.Collapsed;
        StatusHeader.Visibility = TrackColumns.Shared.ShowsStatus ? Visibility.Visible : Visibility.Collapsed;
        FitColumns();
    }

    // Statuses change in bursts while songs are found or synced; the columns follow once things settle.
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackRow.IsFinding)) UpdateFindingProgress();
        if (e.PropertyName != nameof(TrackRow.Status)) return;
        columnsTimer ??= CreateColumnsTimer();
        columnsTimer.Stop();
        columnsTimer.Start();
    }

    private DispatcherQueueTimer CreateColumnsTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UpdateColumns();
        return timer;
    }

    private void WatchRows(IEnumerable<TrackRow> rows)
    {
        foreach (var row in rows)
        {
            row.PropertyChanged -= OnRowChanged;
            row.PropertyChanged += OnRowChanged;
        }
    }

    /// <summary>Measures what each column needs to show its header and all its cells in full, with the list's fonts.</summary>
    private void FitColumns()
    {
        if (trackRows is not { } rows) return;
        double Cells(Func<TrackRow, string> text, string header) => rows.Select(r => TextWidth(text(r))).Append(TextWidth(header, strong: true)).Max();

        // Every status has the icon's slot before it: 18px and the 4px gap.
        const double StatusIcon = 22;
        var undo = TextWidth("Skipped") + 4 + TextWidth("Undo") + 10;
        var status = TrackRow.FixedStatuses.Concat(rows.Select(r => r.Status)).Select(s => Math.Min(TextWidth(s), 280))
            .Append(undo).Max() + StatusIcon;
        status = Math.Max(status, TextWidth("Status", strong: true));

        // A little slack: italic player-only rows, and rounding in the list's own text layout.
        const double Slack = 4;
        TrackColumns.Shared.SetContentWidths(new(
            Cells(r => r.Number, "#") + Slack,
            Cells(r => r.Title, "Title") + Slack,
            Cells(r => r.ArtistsText, "Artists") + Slack,
            Cells(r => r.Album, "Album") + Slack,
            Cells(r => r.DurationText, "Time") + Slack,
            Cells(r => r.AddedAtText, "Added") + Slack,
            status + Slack));
    }

    private double TextWidth(string text, bool strong = false)
    {
        if (text.Length == 0) return 0;
        if (textWidths.TryGetValue((text, strong), out var width)) return width;
        measureText ??= new TextBlock { FontSize = 14, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"] };
        measureText.FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        measureText.Text = text;
        measureText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return textWidths[(text, strong)] = measureText.DesiredSize.Width;
    }

    // From the page rather than the header: a grid of pixel-wide columns never measures narrower than their sum.
    private void OnPlaylistViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TrackColumns.Shared.SetWidth(e.NewSize.Width - PlaylistView.Padding.Left - PlaylistView.Padding.Right - TrackHeader.Padding.Left - TrackHeader.Padding.Right);
        LayoutPlaylistHeadingRow();
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        if (syncCancellation is { } running)
        {
            running.Cancel();
            return;
        }

        if (trackRows is not { } rows || spotifyRows is not { } tracks || playerLibrary is not { } library || currentItem is not { } item) return;
        var selected = tracks.Where(r => r.IsSelected).ToList();
        var removals = playerOnlyRows.Values.Where(r => r.IsMarkedForRemoval).Select(r => r.PlayerOnlyRelativePath!).ToList();
        if (selected.Count + removals.Count == 0) return;

        // A sync looks for its own songs first, and nothing else is looked for until it ends (SYNC-11).
        Finding.PauseForSync();
        StopFinding();
        using var cancellation = syncCancellation = new CancellationTokenSource();
        var finished = syncFinished = new TaskCompletionSource();
        using var awake = SleepBlocker.Start();
        foreach (var row in rows) row.CanChange = false;
        foreach (var row in selected)
        {
            row.SyncState = TrackSyncState.Queued;
            row.Percent = null;
            row.SyncMessage = null;
        }
        MessageBar.IsOpen = false;
        UpdateSyncControls();

        // By reference: a playlist can have the same song twice.
        var rowsByTrack = new Dictionary<ExportedTrack, TrackRow>(ReferenceEqualityComparer.Instance);
        foreach (var row in selected) rowsByTrack[row.Track!] = row;
        // Progress counts the songs being added, as the summary does; ticked songs already on the player need nothing.
        syncingRows = selected.Where(r => !r.IsOnPlayer).ToList();
        UpdateTaskbar();
        var copiedRows = new HashSet<TrackRow>();
        // Progress<T> queues its reports, so some can arrive after the sync has finished; the result is what counts then.
        var active = true;
        var progress = new Progress<TrackSyncProgress>(update =>
        {
            if (!active) return;
            if (rowsByTrack.GetValueOrDefault(update.Track) is not TrackRow row) return;
            row.SyncState = update.State;
            row.Percent = update.Percent;
            row.SyncMessage = update.Message;
            if (update.State == TrackSyncState.Done)
            {
                row.MarkOnPlayer();
                copiedRows.Add(row);
            }
            UpdateColumns();
            UpdateTaskbar();
        });

        // What's on the player before, to tell afterwards whether anything changed.
        static HashSet<string> FilesOn(PlayerLibrary library) => library.GetSnapshot().Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filesBefore = FilesOn(library);
        var errors = new List<string>();
        var stopped = false;
        var cancelled = false;

        try
        {
            var allTracks = tracks.Select(r => r.Track!).ToList();
            var adding = selected;
            var cleanup = removals;
            while (true)
            {
                var request = new SyncRequest(item.Kind, item.Key, item.Name, item.Owner, allTracks, adding.Select(r => r.Track!).ToList(), cleanup);

                // Find every song first, and let the user settle the uncertain ones, before downloading anything.
                var resolutions = await ResolveAndReviewAsync(PlayerSyncer.TracksToDownload(library, request), rowsByTrack, cancellation.Token);
                if (resolutions is null)
                {
                    cancelled = true;
                    break;
                }

                var result = await Syncer.SyncAsync(library, request with { Resolutions = resolutions }, progress, cancellation.Token);
                errors.AddRange(result.Errors);
                // Each song's final state comes from the result, whatever progress reports are still on their way.
                foreach (var track in result.Added)
                {
                    if (rowsByTrack.GetValueOrDefault(track) is not TrackRow row) continue;
                    row.SyncState = TrackSyncState.Done;
                    row.Percent = null;
                    row.MarkOnPlayer();
                    copiedRows.Add(row);
                }
                foreach (var (track, message) in result.Failed)
                {
                    if (rowsByTrack.GetValueOrDefault(track) is not TrackRow row) continue;
                    row.SyncState = TrackSyncState.Failed;
                    row.SyncMessage = message;
                }
                foreach (var track in result.NeedsReview)
                {
                    if (rowsByTrack.GetValueOrDefault(track) is TrackRow row) row.SyncState = TrackSyncState.NeedsReview;
                }

                // A song whose video has gone since it was matched goes back for review.
                adding = result.NeedsReview.Select(t => rowsByTrack.GetValueOrDefault(t)).OfType<TrackRow>().ToList();
                if (adding.Count == 0) break;
                foreach (var row in adding) Forget(row.Track!);
                cleanup = [];
                foreach (var row in adding) row.SyncState = TrackSyncState.Queued;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            stopped = true;
            ShowMessage(InfoBarSeverity.Error, $"Could not sync to {ContentsPlayer.Player?.DisplayName ?? playerName}", ex.Message);
        }
        finally
        {
            active = false;
            HideReview();
            if (!stopped)
            {
                var removed = removals.Count(r => !File.Exists(Path.Combine(library.RootPath, r)));
                ShowSyncResult(selected, copiedRows.Count, removed, cancelled, changed: !FilesOn(library).SetEquals(filesBefore), errors);
            }
            syncCancellation = null;
            syncingRows = null;
            resolving = reviewing = false;
            UpdateTaskbar();
            foreach (var row in rows)
            {
                row.CanChange = true;
                // Tracks that never got going were cancelled
                if (row.SyncState is TrackSyncState.Queued or TrackSyncState.Searching or TrackSyncState.Downloading or TrackSyncState.Saving or TrackSyncState.NeedsReview)
                    row.SyncState = null;
            }
            UpdateSyncControls();
            Finding.ResumeAfterSync();
            StartFinding();
            _ = CheckPlayerVolumeAsync();
            _ = LoadPlayersAsync(focusSelected: false); // The free space has changed
            syncFinished = null;
            finished.TrySetResult();
        }
    }

    /// <summary>Whether a sync is running.</summary>
    public bool IsSyncing => syncCancellation is not null;

    /// <summary>
    /// When the window is closed during a sync, asks whether to stop it. If so, cancels the sync and waits until it
    /// has stopped, so nothing is left half-written on the player.
    /// </summary>
    /// <returns>Whether the window can close.</returns>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (syncCancellation is null) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Stop syncing and close?",
            PrimaryButtonText = "Stop and close",
            CloseButtonText = "Keep syncing",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var finished = syncFinished?.Task ?? Task.CompletedTask;
        syncCancellation?.Cancel();
        await finished;
        return true;
    }

    /// <summary>
    /// Finds each song on YouTube Music (instantly for songs found or skipped before), then has the user settle any
    /// uncertain matches.
    /// </summary>
    /// <returns>What each song resolved to, or null if the user cancelled the review.</returns>
    private async Task<Dictionary<ExportedTrack, TrackResolution>?> ResolveAndReviewAsync(
        IReadOnlyList<ExportedTrack> toDownload, Dictionary<ExportedTrack, TrackRow> rowsByTrack, CancellationToken cancellationToken)
    {
        var byTrack = new Dictionary<ExportedTrack, TrackResolution>(ReferenceEqualityComparer.Instance);
        if (toDownload.Count == 0) return byTrack;

        // Songs found before (in the background, or for another playlist) don't need looking for again.
        var toFind = new List<ExportedTrack>();
        foreach (var track in toDownload)
        {
            if (KnownFind(track) is { } known) byTrack[track] = known;
            else toFind.Add(track);
        }

        resolving = true;
        resolveTotal = toFind.Count;
        resolveDone = 0;
        UpdateTaskbar();
        var lookingUp = true;
        var resolveProgress = new Progress<TrackResolutionProgress>(update =>
        {
            if (!lookingUp) return; // Arrived after the lookups returned; their results are applied below
            if (update.Stage == TrackResolutionStage.Completed) resolveDone++;
            UpdateTaskbar();
            if (rowsByTrack.GetValueOrDefault(update.Track) is not TrackRow row) return;
            row.SyncState = update.Stage switch
            {
                TrackResolutionStage.Searching => TrackSyncState.Searching,
                TrackResolutionStage.Completed when update.Resolution is { State: not (TrackResolutionState.Resolved or TrackResolutionState.Skipped) } => TrackSyncState.NeedsReview,
                _ => TrackSyncState.Queued,
            };
            UpdateColumns();
        });

        if (toFind.Count > 0)
        {
            foreach (var resolution in await Finder.ResolveTracksAsync(toFind, resolveProgress, cancellationToken))
            {
                byTrack[resolution.Track] = resolution;
                Remember(resolution);
            }
        }
        lookingUp = false;

        var toReview = byTrack.Values.Where(r => r.State is not (TrackResolutionState.Resolved or TrackResolutionState.Skipped)).ToList();
        if (toReview.Count > 0)
        {
            reviewing = true;
            UpdateTaskbar();
            var reviewed = await ReviewAsync(toReview, cancellationToken);
            reviewing = false;
            if (reviewed is null) return null;
            foreach (var review in reviewed) byTrack[review.Track] = review.Resolution;
        }
        resolving = false;

        foreach (var resolution in byTrack.Values)
        {
            if (rowsByTrack.GetValueOrDefault(resolution.Track) is not TrackRow row) continue;
            if (resolution.State == TrackResolutionState.Skipped)
            {
                row.SyncState = null;
                row.IsSkipped = true;
            }
            else
            {
                row.SyncState = TrackSyncState.Queued;
            }
        }
        UpdateColumns();
        return byTrack;
    }

    // ---- Whether the player can take the sync ----------------------------------------------------------------

    private (string Root, bool Locked, long? FreeBytes)? playerVolume;
    private bool checkingVolume;

    /// <summary>
    /// Finds out, without writing anything, whether the player is locked and how much space it has, so that a sync
    /// that can't work isn't started.
    /// </summary>
    private async Task CheckPlayerVolumeAsync()
    {
        if (checkingVolume || playerRoot is not { } root) return;
        checkingVolume = true;
        try
        {
            var volume = await Task.Run(() =>
            {
                long? free = null;
                try
                {
                    free = new DriveInfo(root).AvailableFreeSpace;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
                return (root, DriveProbe.IsWriteProtected(root), free);
            });
            if (playerRoot == root) playerVolume = volume;
        }
        finally
        {
            checkingVolume = false;
        }
        UpdateSyncControls();
    }

    /// <returns>Why the selected songs can't be synced to the player, if they can't.</returns>
    private string? PlayerProblem(IReadOnlyList<TrackRow> adding)
    {
        if (playerVolume is not { } volume || volume.Root != playerRoot) return null;
        if (volume.Locked) return Players.LockedText;
        if (volume.FreeBytes is not { } free || adding.Count == 0) return null;

        var sizes = adding.Select(r => r.CopySize ?? SpaceEstimate.ForDownload(r.Track!.Duration)).ToList();
        var freed = playerOnlyRows.Values.Where(r => r.IsMarkedForRemoval).Sum(r => r.PlayerOnlyFile!.Size);
        return SpaceEstimate.Check(sizes, free, freed) is { } shortfall ? SpaceEstimate.Describe(shortfall) : null;
    }

    // ---- Taskbar progress ------------------------------------------------------------------------------------

    private TaskbarProgress? taskbarProgress;
    private List<TrackRow>? syncingRows;
    private bool resolving, reviewing;
    private int resolveTotal, resolveDone;

    private TaskbarProgress Taskbar => taskbarProgress ??= new TaskbarProgress(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

    /// <summary>
    /// Shows how far the sync has got on the taskbar button: finding songs is the first fifth, then downloading
    /// and copying. Paused while waiting for the user to review matches; red once a song has failed.
    /// </summary>
    private void UpdateTaskbar()
    {
        if (syncingRows is not { } rows)
        {
            Taskbar.Set(TaskbarProgressState.None);
            SyncProgressPanel.Visibility = Visibility.Collapsed;
            return;
        }

        const double ResolveShare = 0.2;
        var fraction = resolving
            ? ResolveShare * (resolveTotal == 0 ? 0 : (double)resolveDone / resolveTotal)
            : ResolveShare + (1 - ResolveShare) * (rows.Count == 0 ? 1 : rows.Average(RowFraction));
        ShowSyncProgress(rows, fraction);
        var state = reviewing ? TaskbarProgressState.Paused
            : rows.Any(r => r.SyncState == TrackSyncState.Failed) ? TaskbarProgressState.Error
            : resolving && resolveDone == 0 ? TaskbarProgressState.Indeterminate
            : TaskbarProgressState.Normal;
        Taskbar.Set(state, fraction);

        static double RowFraction(TrackRow row) => row.SyncState switch
        {
            TrackSyncState.Queued or TrackSyncState.Searching => 0,
            TrackSyncState.Downloading => 0.8 * (row.Percent ?? 0) / 100,
            TrackSyncState.Saving => 0.9,
            _ => 1, // Done, failed, or skipped
        };
    }

    private void ShowSyncProgress(List<TrackRow> rows, double fraction)
    {
        var done = rows.Count(r => r.SyncState is TrackSyncState.Done or TrackSyncState.Failed || r.IsSkipped);
        SyncProgressText.Text = youTubeTraffic?.BusyText ?? (resolving ? "Finding sources…" : $"Syncing {Math.Min(done + 1, rows.Count)} of {rows.Count} {(rows.Count == 1 ? "song" : "songs")}");
        SyncProgressBar.Value = fraction;
        SyncProgressBar.ShowPaused = reviewing;
        SyncProgressBar.ShowError = rows.Any(r => r.SyncState == TrackSyncState.Failed);
        SyncProgressPanel.Visibility = Visibility.Visible;
        SyncSummaryText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Says how the sync went, once it's over.</summary>
    /// <param name="changed">Whether any file on the player was added, moved, renamed or deleted.</param>
    /// <param name="errors">Problems that aren't about one song, e.g. a full player.</param>
    private void ShowSyncResult(IReadOnlyList<TrackRow> rows, int copied, int removed, bool cancelled, bool changed, IReadOnlyList<string> errors)
    {
        var failed = rows.Count(r => r.SyncState == TrackSyncState.Failed);
        var skipped = rows.Count(r => r.IsSkipped);
        var player = ContentsPlayer.Player?.DisplayName ?? playerName;
        string Songs(int n) => $"{n} {(n == 1 ? "song" : "songs")}";

        if (cancelled)
        {
            ShowMessage(InfoBarSeverity.Informational, "Sync cancelled.",
                !changed ? "Nothing on the player changed."
                : copied > 0 ? $"{Songs(copied)} had already been copied to {player}."
                : "Some changes had already been made on the player.");
            return;
        }

        var details = new List<string>();
        if (skipped > 0) details.Add($"{Songs(skipped)} skipped");
        if (failed > 0) details.Add($"{Songs(failed)} couldn't be synced");
        var title = copied + removed == 0 && failed > 0 ? $"Nothing was synced to {player}"
            : copied > 0 ? $"Synced {Songs(copied)} to {player}"
            : $"Updated {player}";
        if (removed > 0) details.Insert(0, $"Removed {Songs(removed)}");
        var message = string.Join(" ", new[] { details.Count > 0 ? string.Join(", ", details) + "." : "" }.Concat(errors).Where(s => s.Length > 0));
        ShowMessage(failed > 0 || errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, title, message);
    }

    // ---- Reviewing uncertain matches -------------------------------------------------------------------------

    private TaskCompletionSource<bool>? reviewDecision;
    private List<ReviewItem> reviewItems = [];
    private CancellationToken reviewCancellation;

    /// <returns>The settled songs, or null if the user cancelled.</returns>
    private async Task<List<ReviewItem>?> ReviewAsync(IReadOnlyList<TrackResolution> toReview, CancellationToken cancellationToken)
    {
        reviewItems = toReview.Select(r => new ReviewItem(r)).ToList();
        foreach (var review in reviewItems)
            review.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReviewItem.State)) UpdateReviewControls(); };

        reviewCancellation = cancellationToken;
        var decision = reviewDecision = new TaskCompletionSource<bool>();
        using var registration = cancellationToken.Register(() => decision.TrySetResult(false));

        ReviewListView.ItemsSource = reviewItems;
        ReviewPanel.Visibility = Visibility.Visible;
        ReviewMatchesButton.Visibility = Visibility.Collapsed;
        TrackHeader.Visibility = Visibility.Collapsed;
        TracksListView.Visibility = Visibility.Collapsed;
        SyncButton.Visibility = SyncSummaryText.Visibility = Visibility.Collapsed;
        UpdateReviewControls();
        _ = FocusAsync(ReviewListView);

        var continued = await decision.Task;
        HideReview();
        // Choices are remembered even when the user backs out.
        foreach (var review in reviewItems) Remember(review.Resolution);
        cancellationToken.ThrowIfCancellationRequested();
        return continued ? reviewItems : null;
    }

    private void HideReview()
    {
        reviewDecision?.TrySetResult(false);
        reviewDecision = null;
        if (ReviewPanel.Visibility == Visibility.Collapsed) return;

        ReviewPanel.Visibility = Visibility.Collapsed;
        ReviewListView.ItemsSource = null;
        TrackHeader.Visibility = TracksListView.Visibility = Visibility.Visible;
        SyncButton.Visibility = SyncSummaryText.Visibility = Visibility.Visible;
        _ = FocusAsync(SyncButton);
    }

    private void UpdateReviewControls()
    {
        var left = reviewItems.Count(r => !r.IsSettled);
        ReviewTitle.Text = left == 0
            ? (reviewOnly is not null ? "Every song has a match" : "Ready to sync")
            : $"Choose a match for {left} {(left == 1 ? "song" : "songs")}";
        ReviewContinueButton.IsEnabled = reviewItems.All(r => r.IsSettled);
    }

    private static async Task FocusAsync(Control control)
    {
        await Task.Yield();
        control.Focus(FocusState.Programmatic);
    }

    private CancellationTokenSource? reviewOnly;

    /// <summary>Settles uncertain matches before syncing, while the other songs are still being looked for.</summary>
    private async void OnReviewMatchesClick(object sender, RoutedEventArgs e)
    {
        if (spotifyRows is not { } rows || reviewOnly is not null) return;
        var toReview = rows.Where(r => r.IsSelected && !r.IsOnPlayer && r.NeedsReview)
            .DistinctBy(r => FindKey(r.Track!))
            .Select(r => found[FindKey(r.Track!)] with { Track = r.Track! })
            .ToList();
        if (toReview.Count == 0) return;

        using var cancellation = reviewOnly = new CancellationTokenSource();
        ReviewContinueButton.Content = "Done";
        try
        {
            await ReviewAsync(toReview, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            reviewOnly = null;
            ReviewContinueButton.Content = "Continue";
            UpdateSyncControls();
        }
    }

    private void OnReviewContinueClick(object sender, RoutedEventArgs e) => reviewDecision?.TrySetResult(true);

    private void OnReviewCancelClick(object sender, RoutedEventArgs e) => reviewDecision?.TrySetResult(false);

    private async void OnUseCandidateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReviewCandidate candidate)
            await ChooseAsync(candidate.Item, candidate.Candidate.Video.Url);
    }

    private async void OnUseLinkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReviewItem review) return;
        if (string.IsNullOrWhiteSpace(review.PastedLink))
        {
            review.LinkError = "Paste a link to the song first.";
            return;
        }
        await ChooseAsync(review, review.PastedLink.Trim());
    }

    private async Task ChooseAsync(ReviewItem review, string link)
    {
        review.LinkError = null;
        review.State = ReviewState.Working;
        try
        {
            var choice = await Finder.ChooseVideoAsync(review.Track, link, reviewCancellation);
            if (choice.Resolution is { } resolution)
            {
                review.Resolution = resolution;
                return;
            }
            review.LinkError = choice.Error;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or YtDlpException)
        {
            review.LinkError = ex.Message;
        }
        review.State = ReviewState.Open;
    }

    private async void OnRetryLookupClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReviewItem review) return;
        review.LinkError = null;
        review.State = ReviewState.Working;
        try
        {
            review.Resolution = await Finder.RetryAsync(review.Track, reviewCancellation);
        }
        catch (OperationCanceledException)
        {
            review.State = ReviewState.Open;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or YtDlpException)
        {
            review.State = ReviewState.Open;
            review.LinkError = ex.Message;
        }
    }

    private void OnSkipReviewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReviewItem review)
            review.Resolution = Finder.Skip(review.Track);
    }

    private void OnChangeReviewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReviewItem review) return;
        // Forget the choice or the skip, so it isn't remembered if the user then cancels.
        Finder.Forget(review.Track);
        review.Resolution = review.Original;
    }

    private void OnUndoSkipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow { Track: { } track } row) return;
        Finder.Forget(track);
        Forget(track);
        row.IsSkipped = false;
        row.IsSelected = !row.IsOnPlayer;
        UpdateSyncControls();
        StartFinding();
    }
    private void ClearTracks()
    {
        StopFinding(); // Only the open playlist's songs are looked for (SYNC-11)
        reviewOnly?.Cancel();
        trackRows = spotifyRows = null;
        playerOnlyRows.Clear();
        RemoveHeader.Visibility = Visibility.Collapsed;
        CheckingPlayerPanel.Visibility = Visibility.Collapsed;
        UpdateFindingProgress();
        TracksListView.ItemsSource = null;
        TrackHeader.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        LoadFailedPanel.Visibility = Visibility.Collapsed;
        UpdateSyncControls();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static bool IsExpected(Exception ex) =>
        ex is ExportifyException or TimeoutException or FormatException or InvalidOperationException;

    private void SaveSettings()
    {
        try
        {
            settings.Save(settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save settings: {ex.Message}");
        }
    }

    private void ShowLibraryBusy(string text)
    {
        LibraryBusy.Text = text;
        LibraryBusy.Visibility = Visibility.Visible;
        LibraryGridView.Opacity = 0; // The window's Mica shows through the indicator, so hide what's beneath
        RefreshLibraryButton.IsEnabled = false;
    }

    private void HideLibraryBusy()
    {
        LibraryBusy.Visibility = Visibility.Collapsed;
        LibraryGridView.Opacity = 1;
        RefreshLibraryButton.IsEnabled = true;
    }

    private void ShowPlaylistBusy(string text)
    {
        PlaylistBusy.Text = text;
        PlaylistBusy.Visibility = Visibility.Visible;
        TracksListView.Opacity = TrackHeader.Opacity = PlaylistInfoBar.Opacity = 0;
    }

    private void HidePlaylistBusy()
    {
        PlaylistBusy.Visibility = Visibility.Collapsed;
        TracksListView.Opacity = TrackHeader.Opacity = PlaylistInfoBar.Opacity = 1;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}
