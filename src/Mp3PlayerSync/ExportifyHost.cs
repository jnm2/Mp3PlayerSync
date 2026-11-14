using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync;

/// <summary>
/// Hosts Exportify in a WebView2 and talks to it through the injected ExportifyBridge.js.
/// </summary>
internal sealed class ExportifyHost
{
    // Keeps Exportify's setTimeout-paced API calls running at full speed while the WebView2 is hidden.
    private const string BrowserArguments =
        "--disable-background-timer-throttling --disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows --disable-features=IntensiveWakeUpThrottling";

    private const string BlankPage = "https://mp3playersync.invalid/";

    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long an export waits for a token that's about to run out to be renewed first.</summary>
    private static readonly TimeSpan RenewalWait = TimeSpan.FromSeconds(20);

    // The pages of the identity providers that Spotify's sign-in offers, followed only during a sign-in.
    private static readonly HashSet<string> IdentityProviderHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.google.com", "appleid.apple.com", "idmsa.apple.com",
        "www.facebook.com", "m.facebook.com", "web.facebook.com", "facebook.com",
    };

    // Sites whose frames the sign-in pages embed, such as reCAPTCHA and the identity providers' own frames.
    private static readonly string[] SignInFrameDomains =
    [
        "spotify.com", "scdn.co", "spotifycdn.com", "google.com", "gstatic.com", "recaptcha.net", "apple.com", "facebook.com",
    ];

    private readonly WebView2 webView;
    private readonly AppServices services;

    // Spotify's sign-in, consent and verification pages, which are the only pages ever shown.
    private readonly HashSet<string> spotifySignInOrigins;

    private TaskCompletionSource<BridgeMessage>? pendingExport;
    private bool exportRequested;
    private bool exportInFlight;
    private bool exportNavigatedAway;
    private readonly HostNavigations hostNavigations = new();
    private bool awaitingNewToken;
    private ulong currentNavigationId;
    private readonly HashSet<ulong> blockedNavigations = [];

    public ExportifyHost(WebView2 webView, AppServices services)
    {
        this.webView = webView;
        this.services = services;
        HomeUri = services.ExportifyUri;
        Origin = HomeUri.GetLeftPart(UriPartial.Authority);
        spotifySignInOrigins = new(services.SpotifySignInOrigins.Select(origin => new Uri(origin).GetLeftPart(UriPartial.Authority)), StringComparer.OrdinalIgnoreCase);
    }

    public Uri HomeUri { get; }

    public string Origin { get; }

    public BridgeMessage? State { get; private set; }

    public bool IsOnExportify => webView.CoreWebView2?.Source is { } source && IsExportifyUrl(source);

    /// <summary>Whether the current page finished loading (as opposed to still navigating or redirecting).</summary>
    public bool IsPageLoaded { get; private set; }

    /// <summary>When the current page finished loading, if it has.</summary>
    public DateTimeOffset? PageLoadedAt { get; private set; }

    /// <summary>
    /// Whether a sign-in, consent or verification page of Spotify's (or an identity provider's, during a sign-in)
    /// has finished loading: Spotify needs the user. These are the only pages that may ever be shown.
    /// </summary>
    public bool IsShowingSignInPage => IsPageLoaded
        && webView.CoreWebView2?.Source is { } source
        && Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (IsSpotifySignInUrl(uri) || (InSignInFlow && IsIdentityProviderUrl(uri)));

    /// <summary>
    /// Whether the page left Exportify for Spotify's sign-in and hasn't come back yet. With a live Spotify session
    /// that's only a few redirects.
    /// </summary>
    public bool InSignInFlow { get; private set; }

    /// <summary>Whether the last navigation failed, e.g. because there is no internet connection.</summary>
    public bool NavigationFailed { get; private set; }

    /// <summary>Whether Exportify is exporting a playlist right now (not just waiting to be able to).</summary>
    public bool IsExporting => pendingExport is not null && exportInFlight;

    /// <summary>Raised when the page's sign-in or loading state changes, or the WebView2 navigates.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler<BridgeMessage>? PlaylistsChanged;

    private string UserDataFolder => Path.Combine(services.DataDirectory, "WebView2");

    public async Task InitializeAsync()
    {
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserArguments };
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", UserDataFolder, options);
        await webView.EnsureCoreWebView2Async(environment);

        var core = webView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        // No inspecting the page, and no browser features such as reloading, printing or finding on the page.
        // Editing keys (cut, copy, paste, select all, undo) still work in the sign-in pages' text fields.
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#endif

        await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadBridgeScript(Origin));
        await PresentAsEdgeAsync(core, environment);

        core.WebMessageReceived += OnWebMessageReceived;
        core.DownloadStarting += OnDownloadStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.SourceChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += (_, args) =>
        {
            // A blocked link click leaves the current page as it was, and a navigation that another one replaced
            // (say, two sign-ins started at once) says nothing about the page.
            if (blockedNavigations.Remove(args.NavigationId) || args.NavigationId != currentNavigationId) return;

            IsPageLoaded = args.IsSuccess;
            PageLoadedAt = args.IsSuccess ? DateTimeOffset.Now : null;
            NavigationFailed = !args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        Navigate(HomeUri.AbsoluteUri);
    }

    /// <summary>
    /// Makes the User-Agent Client Hints (navigator.userAgentData and Sec-CH-UA headers) match Microsoft Edge's,
    /// which WebView2 otherwise extends with a "Microsoft Edge WebView2" brand. The user agent string already matches.
    /// </summary>
    private static async Task PresentAsEdgeAsync(CoreWebView2 core, CoreWebView2Environment environment)
    {
        // The hints are only readable in a secure context, which the initial about:blank isn't. Load an empty
        // page from an https address that never reaches the network: the app answers the request itself.
        var blankLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void ServeBlank(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var content = new MemoryStream("<!doctype html><title></title>"u8.ToArray()).AsRandomAccessStream();
            args.Response = environment.CreateWebResourceResponse(content, 200, "OK", "Content-Type: text/html; charset=utf-8");
        }

        void OnBlankLoaded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => blankLoaded.TrySetResult();

        core.AddWebResourceRequestedFilter(BlankPage + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeBlank;
        core.NavigationCompleted += OnBlankLoaded;
        try
        {
            core.Navigate(BlankPage);
            await blankLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await OverrideAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not match Edge's client hints: {ex.Message}");
        }
        finally
        {
            core.NavigationCompleted -= OnBlankLoaded;
            core.WebResourceRequested -= ServeBlank;
            core.RemoveWebResourceRequestedFilter(BlankPage + "*", CoreWebView2WebResourceContext.All);
        }

        async Task OverrideAsync()
        {
        const string Expression = """
            (async () => {
                const data = navigator.userAgentData;
                const high = await data.getHighEntropyValues(['architecture', 'bitness', 'model', 'platformVersion', 'fullVersionList', 'wow64']);
                return { userAgent: navigator.userAgent, navigatorPlatform: navigator.platform, brands: data.brands, mobile: data.mobile, platform: data.platform, ...high };
            })()
            """;

            var evaluation = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                new JsonObject { ["expression"] = Expression, ["awaitPromise"] = true, ["returnByValue"] = true }.ToJsonString()));
            if (evaluation?["exceptionDetails"] is { } failure)
                throw new InvalidOperationException("Could not read the client hints: " + failure.ToJsonString());
            var value = evaluation?["result"]?["value"] ?? throw new InvalidOperationException("No client hints");

            var metadata = new JsonObject
            {
                ["brands"] = ToJson(ClientHints.AsEdge(Brands(value["brands"]))),
                ["fullVersionList"] = ToJson(ClientHints.AsEdge(Brands(value["fullVersionList"]))),
            };
            foreach (var name in (string[])["platform", "platformVersion", "architecture", "model", "mobile", "bitness", "wow64"])
                metadata[name] = value[name]?.DeepClone();

            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", new JsonObject
            {
                ["userAgent"] = value["userAgent"]?.DeepClone(),
                ["platform"] = value["navigatorPlatform"]?.DeepClone(),
                ["userAgentMetadata"] = metadata,
            }.ToJsonString());
        }

        static List<BrandVersion> Brands(JsonNode? list) =>
            list?.AsArray().Select(b => new BrandVersion((string?)b?["brand"] ?? "", (string?)b?["version"] ?? "")).ToList() ?? [];

        static JsonArray ToJson(IEnumerable<BrandVersion> brands) =>
            new(brands.Select(b => (JsonNode)new JsonObject { ["brand"] = b.Brand, ["version"] = b.Version }).ToArray());
    }

    /// <summary>
    /// Clicks Exportify's Get Started button, which goes to Spotify's sign-in page, or with a live Spotify session
    /// straight back with a new token. Exportify hides the button while it shows the playlists, but clicking it still
    /// works, which is how a token that has run out (or soon will) is renewed.
    /// </summary>
    public void StartSignIn() => Post(new BridgeCommand(BridgeCommand.SignIn));

    /// <summary>
    /// Loads the playlists again. A token that has run out (or soon will) is renewed instead, which loads them too.
    /// </summary>
    public void Reload()
    {
        if (IsOnExportify && IsPageLoaded && State is { SignedIn: false, TokenExpired: true } or { RenewSoon: true })
            StartSignIn();
        else
            Navigate(HomeUri.AbsoluteUri);
    }

    /// <summary>
    /// Clicks the playlist's Export button on Exportify and returns the CSV it generates, without saving it.
    /// Waits for the playlists first, such as while the token is renewed, and tries again once if the page left
    /// during the export (Exportify goes home when Spotify stops accepting its token).
    /// </summary>
    public async Task<BridgeMessage> ExportAsync(string playlistKey, CancellationToken cancellationToken = default)
    {
        if (exportRequested)
            throw new InvalidOperationException(FailureText.StillLoading);
        exportRequested = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExportTimeout);

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await WaitUntilReadyAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(FailureText.SignInTooSlow);
                }

                var completion = new TaskCompletionSource<BridgeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = timeout.Token.Register(() => completion.TrySetException(
                    cancellationToken.IsCancellationRequested
                        ? new OperationCanceledException(cancellationToken)
                        : new TimeoutException(FailureText.TooSlow)));

                pendingExport = completion;
                exportNavigatedAway = false;
                exportInFlight = true;
                BridgeMessage result;
                try
                {
                    Post(new BridgeCommand(BridgeCommand.Export, Key: playlistKey));
                    result = await completion.Task;
                }
                finally
                {
                    pendingExport = null;
                    exportInFlight = false;
                }

                if (result.Type != BridgeMessage.ExportFailedType)
                {
                    if (string.IsNullOrWhiteSpace(result.Warnings)) return result;

                    // The page's own words never reach the UI (TEXT-1).
                    System.Diagnostics.Debug.WriteLine($"Export warnings: {result.Warnings}");
                    return new BridgeMessage
                    {
                        Type = result.Type, Key = result.Key, FileName = result.FileName, Csv = result.Csv,
                        Warnings = FailureText.ForPageError(result.Warnings),
                    };
                }

                // A failure because the token stopped working shows as the page going home, sometimes just after.
                if (attempt == 1 && !exportNavigatedAway)
                    await Task.Delay(500, timeout.Token);
                if (attempt == 1 && (exportNavigatedAway || !IsOnExportify || State is not { SignedIn: true }))
                    continue;

                System.Diagnostics.Debug.WriteLine($"Export failed ({result.Code}): {result.Error}");
                throw new ExportifyException(FailureText.ForExport(result.Code, result.Error));
            }
        }
        finally
        {
            exportRequested = false;
            StateChanged?.Invoke(this, EventArgs.Empty); // A renewal may have been waiting for the export
        }
    }

    /// <summary>
    /// Waits until Exportify is showing the playlists with a valid token, e.g. while the token is renewed in the
    /// background. A token that's about to run out is renewed first, unless that doesn't happen soon.
    /// </summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        while (!(IsOnExportify && IsPageLoaded && !awaitingNewToken && State is { SignedIn: true, PlaylistsReady: true } state
            && (!state.RenewSoon || DateTimeOffset.Now - started > RenewalWait)))
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();
            StateChanged += OnChanged;
            try
            {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                StateChanged -= OnChanged;
            }
        }
    }

    public async Task SignOutAsync()
    {
        var core = webView.CoreWebView2;
        if (IsOnExportify)
            await core.ExecuteScriptAsync("localStorage.clear()");
        core.CookieManager.DeleteAllCookies();
        Navigate(HomeUri.AbsoluteUri);
    }

    private void Navigate(string url)
    {
        hostNavigations.Starting(url);
        webView.CoreWebView2?.Navigate(url);
    }

    private bool IsExportifyUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsExportifyUrl(uri);
    }

    private bool IsExportifyUrl(Uri uri) => string.Equals(uri.GetLeftPart(UriPartial.Authority), Origin, StringComparison.OrdinalIgnoreCase);

    private bool IsSpotifySignInUrl(Uri uri) => IsWebUrl(uri) && spotifySignInOrigins.Contains(uri.GetLeftPart(UriPartial.Authority));

    private static bool IsIdentityProviderUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && IdentityProviderHosts.Contains(uri.Host);

    private static bool IsWebUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    private void Post(BridgeCommand command) => webView.CoreWebView2?.PostWebMessageAsJson(command.ToJson());

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsExportifyUrl(args.Source))
            return;

        BridgeMessage message;
        try
        {
            message = BridgeMessage.Parse(args.WebMessageAsJson);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine($"Ignoring malformed bridge message: {ex.Message}");
            return;
        }

        switch (message.Type)
        {
            case BridgeMessage.StateType:
                State = message;
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;

            case BridgeMessage.PlaylistsType:
                PlaylistsChanged?.Invoke(this, message);
                break;

            case BridgeMessage.ExportCompletedType:
            case BridgeMessage.ExportFailedType:
                pendingExport?.TrySetResult(message);
                break;

            case BridgeMessage.LogType:
                System.Diagnostics.Debug.WriteLine($"[ExportifyBridge] {message.Message}");
                break;
        }
    }

    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        // Nothing Exportify produces should reach the disk.
        args.Cancel = true;
        args.Handled = true;

        var uri = args.DownloadOperation.Uri;
        if (uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            Post(new BridgeCommand(BridgeCommand.CaptureUrl, Url: uri, Name: Path.GetFileName(args.ResultFilePath)));
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Spotify's sign-in and the identity providers' work by redirecting the page itself, not with popups.
        args.Handled = true;
        if (args.IsUserInitiated)
            OpenExternally(args.Uri);
    }

    // ---- Keeping the page to Exportify and the sign-in -------------------------------------------------------
    //
    // The page only ever goes to Exportify, Spotify's sign-in pages, and during a sign-in, the identity providers'
    // pages and wherever their redirects and scripts take it. Links the user clicks to anywhere else (such as
    // Spotify's sign-up, privacy and help pages) open in the default app instead. Redirects and navigations that
    // aren't the user's, which is how OAuth and its form posts move between sites, are never mistaken for those.

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var byHost = hostNavigations.IsOwn(args.Uri);

        Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri);
        if (!byHost && !IsAllowedNavigation(uri, args.IsUserInitiated, args.IsRedirected))
        {
            args.Cancel = true;
            blockedNavigations.Add(args.NavigationId);
            if (args.IsUserInitiated) OpenExternally(args.Uri);
            return;
        }

        // Exportify only sends itself home when Spotify stops accepting its token (a 401), even though by its age the
        // token still looks good. Going home would load the playlists with the same token, fail again and go home
        // again, so sign in for a new token instead.
        if (!byHost && uri is not null && IsExportifyUrl(uri) && IsOnExportify && !args.IsRedirected)
        {
            args.Cancel = true;
            blockedNavigations.Add(args.NavigationId);
            FailExportInFlight();
            // Several requests can fail at once; one sign-in answers them all.
            if (!awaitingNewToken) Post(new BridgeCommand(BridgeCommand.SignIn, Force: true));
            awaitingNewToken = true;
            return;
        }

        if (uri is not null && IsExportifyUrl(uri))
        {
            if (InSignInFlow) awaitingNewToken = false; // Back from Spotify, with a new code to exchange
            InSignInFlow = false;
        }
        else if (uri is not null && IsSpotifySignInUrl(uri))
        {
            InSignInFlow = true;
        }

        FailExportInFlight();
        currentNavigationId = args.NavigationId;
        IsPageLoaded = false;
        PageLoadedAt = null;
        NavigationFailed = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Whatever takes the page away from Exportify's playlists ends an export; it's tried again once they're back.
    private void FailExportInFlight()
    {
        if (!exportInFlight) return;
        exportNavigatedAway = true;
        pendingExport?.TrySetResult(new BridgeMessage { Type = BridgeMessage.ExportFailedType, Code = FailureText.Codes.SessionEnded, Error = "The page left during the export." });
    }

    private bool IsAllowedNavigation(Uri? uri, bool userInitiated, bool redirected)
    {
        if (uri is null) return false;
        if (!IsWebUrl(uri)) return uri.Scheme == "about";
        if (IsExportifyUrl(uri) || IsSpotifySignInUrl(uri) || uri.AbsoluteUri.StartsWith(BlankPage, StringComparison.OrdinalIgnoreCase)) return true;
        return InSignInFlow && (IsIdentityProviderUrl(uri) || redirected || !userInitiated);
    }

    private void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.IsUserInitiated || args.IsRedirected) return;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !IsWebUrl(uri)) return;
        if (IsExportifyUrl(uri) || IsSpotifySignInUrl(uri) || IsIdentityProviderUrl(uri)
            || SignInFrameDomains.Any(domain => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            return;

        args.Cancel = true;
        OpenExternally(args.Uri);
    }


    private void OpenExternally(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsWebUrl(uri)) services.OpenExternally(uri);
    }

    private static string LoadBridgeScript(string origin)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mp3PlayerSync.ExportifyBridge.js")
            ?? throw new InvalidOperationException("Part of the app is missing. Reinstall it."); // The embedded ExportifyBridge.js
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__ALLOWED_ORIGIN__", JsonSerializer.Serialize(origin, BridgeScriptJsonContext.Default.String));
    }
}

internal sealed class ExportifyException(string message) : Exception(message);

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class BridgeScriptJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
