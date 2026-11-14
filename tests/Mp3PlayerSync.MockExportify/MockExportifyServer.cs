using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mp3PlayerSync.MockExportify;

/// <summary>
/// Stands in for exportify.net, and for Spotify's sign-in page on another origin (127.0.0.1 rather than localhost), for
/// the end-to-end tests. It serves only what the app and the tests rely on; README.md says what each part is for.
/// </summary>
public sealed partial class MockExportifyServer : IAsyncDisposable
{
    private readonly MockLibrary library;
    private readonly Action<string> log;
    private readonly Lock gate = new();
    private readonly List<string> requests = [];
    private bool signInHeld, expireAsked, expiredOnExport;
    private int tokensIssued, firstValidToken;
    private int failed;
    private WebApplication? app;

    private MockExportifyServer(MockLibrary library, Action<string> log)
    {
        this.library = library;
        this.log = log;
    }

    /// <summary>The mock Exportify's home page, such as <c>http://localhost:5178/</c>.</summary>
    public Uri Url { get; private set; } = null!;

    public int Port => Url.Port;

    /// <summary>
    /// Whether the sign-in page's ?auto sign-in waits until it's let go on, so a test can see the sign-in screen first,
    /// as a POST of 'hold' or 'release' to /sign-in-hold sets it.
    /// </summary>
    public bool SignInHeld
    {
        get { lock (gate) return signInHeld; }
        set { lock (gate) signInHeld = value; }
    }

    /// <summary>
    /// Starts serving the library the end-to-end tests check on a free port, for tests in the same process.
    /// <paramref name="log"/> hears of each request that failed.
    /// </summary>
    public static Task<MockExportifyServer> StartAsync(Action<string> log) => StartAsync(MockLibrary.Test, 0, log);

    /// <summary>
    /// Starts serving <paramref name="library"/> on <paramref name="port"/>, or on a free port when it's 0.
    /// <paramref name="log"/> hears of each request that failed.
    /// </summary>
    internal static async Task<MockExportifyServer> StartAsync(MockLibrary library, int port, Action<string> log)
    {
        var server = new MockExportifyServer(library, log);
        // Another process can take the free port found before Kestrel binds it; then another is tried.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await server.ListenAsync(port is 0 ? FreePort() : port);
                return server;
            }
            catch (IOException exception) when (port is 0 && attempt < 5 && exception.InnerException is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                log($"Mock: port {server.Url.Port} was taken; trying another");
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private async Task ListenAsync(int port)
    {
        Url = new Uri($"http://localhost:{port}/");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // localhost is both loopbacks, and the browser may try either; the sign-in page is on 127.0.0.1.
            kestrel.Listen(IPAddress.Loopback, port);
            if (Socket.OSSupportsIPv6) kestrel.Listen(IPAddress.IPv6Loopback, port);
        });
        var candidate = builder.Build();
        candidate.Run(RespondAsync);
        try
        {
            await candidate.StartAsync();
            app = candidate;
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        await app.StopAsync(TimeSpan.Zero);
        await app.DisposeAsync();
        app = null;
    }

    private async Task RespondAsync(HttpContext context)
    {
        try
        {
            await RespondOrThrowAsync(context);
        }
        catch (Exception exception)
        {
            // A client that goes away mid-request, as the app's page does when it closes or navigates, only fails that
            // request: the mock goes on serving the others, and says so, which can explain a failure far from it.
            var count = Interlocked.Increment(ref failed);
            log($"Mock: {context.Request.Method} {context.Request.Path}{context.Request.QueryString} failed ({count} so far): {exception.Message}");
            context.Abort();
        }
    }

    private async Task RespondOrThrowAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Path.Value ?? "/";
        var isPost = HttpMethods.IsPost(request.Method);
        response.Headers.CacheControl = "no-store";

        if (path is "/")
        {
            await WriteAsync(response, "text/html; charset=utf-8", ExportifyPage());
        }
        else if (path is "/sign-in")
        {
            // Like accounts.spotify.com, go straight back with a code when there's a Spotify session already.
            if (request.Cookies.ContainsKey(SessionCookie))
            {
                response.Redirect($"http://localhost:{Port}/?code=mock-authorization-code");
                return;
            }
            Record("sign-in page");
            await WriteAsync(response, "text/html; charset=utf-8", SignInPage);
        }
        else if (path is "/token" && isPost)
        {
            Record("token");
            int token;
            lock (gate) token = tokensIssued++;
            await WriteAsync(response, "text/plain", "token-" + token);
        }
        else if (ExportPath().Match(path) is { Success: true } export)
        {
            await ExportAsync(context, export.Groups[1].Value);
        }
        else if (path is "/requests")
        {
            string json;
            lock (gate) json = JsonSerializer.Serialize(requests);
            await WriteAsync(response, "application/json", json);
        }
        else if (path is "/sign-in-hold")
        {
            // The tests can hold the sign-in page's ?auto sign-in (POST hold) until they've seen the sign-in screen,
            // then let it go on (POST release). The page asks here (GET) while it waits.
            if (isPost)
            {
                var held = await ReadBodyAsync(context) is "hold";
                lock (gate) signInHeld = held;
            }
            else
            {
                bool held;
                lock (gate) held = signInHeld;
                await WriteAsync(response, "text/plain", held ? "held" : "released");
            }
        }
        else if (path is "/expire")
        {
            // The tests can make the token run out once they're watching (POST). The page, opened with
            // ?expireWhenAsked, asks here (GET) and is told once, and from then on the tokens issued so far are refused.
            if (isPost)
            {
                lock (gate) expireAsked = true;
            }
            else
            {
                bool expire;
                lock (gate)
                {
                    (expire, expireAsked) = (expireAsked, false);
                    if (expire) firstValidToken = tokensIssued;
                }
                await WriteAsync(response, "text/plain", expire ? "expire" : "wait");
            }
        }
        else if (DemoCoverPath().Match(path) is { Success: true } cover)
        {
            response.ContentType = "image/png";
            await response.Body.WriteAsync(DemoCovers.Draw(cover.Groups[1].Value), context.RequestAborted);
        }
        else
        {
            response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private async Task ExportAsync(HttpContext context, string listId)
    {
        var response = context.Response;
        Record("export " + listId);

        bool accepted;
        lock (gate)
        {
            // ?expireOnExport makes Spotify stop accepting the tokens issued so far at the first export, once, even
            // though by its age the page still thinks its token is good.
            if (context.Request.Query.ContainsKey("expireOnExport") && !expiredOnExport)
            {
                expiredOnExport = true;
                firstValidToken = tokensIssued;
            }
            var authorization = context.Request.Headers.Authorization.ToString();
            accepted = authorization.StartsWith("Bearer token-", StringComparison.Ordinal)
                && int.TryParse(authorization["Bearer token-".Length..], out var token)
                && token >= firstValidToken && token < tokensIssued;
        }

        if (!accepted)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else if (library.Playlists.Any(p => p.Id == listId && p.Fails))
        {
            response.StatusCode = StatusCodes.Status500InternalServerError;
        }
        else if (library.ExportCsv(listId) is not { } csv)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
        }
        else
        {
            if (library.ExportWarning is { } warning) response.Headers["X-Mock-Warning"] = warning;
            await WriteAsync(response, "text/csv; charset=utf-8", csv);
        }
    }

    private void Record(string request)
    {
        lock (gate) requests.Add(request);
    }

    private const string SessionCookie = "mock_spotify_session";

    private string ExportifyPage()
    {
        var rows = new StringBuilder();
        void Row(string listId, string url, string name, string owner, int count, string? cover)
        {
            var image = cover is null ? "" : $"""<img src="{Encode(cover)}" alt="">""";
            rows.Append($"""<tr><td>{image}</td><td><a href="{Encode(url)}">{Encode(name)}</a></td><td>{Encode(owner)}</td><td>{count}</td>""")
                .Append($"""<td><button id="export-{Encode(listId)}" data-list="{Encode(listId)}">Export</button></td></tr>""").Append('\n');
        }

        Row("liked", "https://open.spotify.com/collection/tracks", "Liked Songs", MockLibrary.UserId, library.LikedTotal, null);
        Row("albums", "https://open.spotify.com/collection/albums", "Saved Albums", MockLibrary.UserId, library.Albums.Count, null);
        foreach (var playlist in library.Playlists)
            Row(playlist.Id, "https://open.spotify.com/playlist/" + playlist.Id, playlist.Name, playlist.Owner, playlist.Total, playlist.Cover);

        return ExportifyPageTemplate.Replace("<!-- rows -->", rows.ToString());
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    // The script names its parts after what ExportifyBridge.js reads: the error element, the sign-in button, and the
    // playlist table with its export buttons. It shows the table only while it has a token less than an hour old.
    private const string ExportifyPageTemplate = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Exportify (mock)</title>
        </head>
        <body>
        <p id="error"></p>
        <button id="loginButton">Sign in</button>
        <div id="playlists"></div>
        <template id="playlistTable"><table><tbody>
        <!-- rows -->
        </tbody></table></template>
        <script>
        const query = new URLSearchParams(location.search)
        const error = document.getElementById('error')
        const playlists = document.getElementById('playlists')

        document.getElementById('loginButton').addEventListener('click', () => {
            location = 'http://127.0.0.1:' + location.port + '/sign-in' + (query.has('autoSignIn') ? '?auto' : '')
        })

        playlists.addEventListener('click', event => {
            const button = event.target.closest('button')
            if (button) exportList(button)
        })

        async function exportList(button) {
            button.textContent = 'Exporting'
            try {
                const response = await fetch('/export/' + button.dataset.list + (query.has('expireOnExport') ? '?expireOnExport' : ''), {
                    headers: { Authorization: 'Bearer ' + localStorage.getItem('access_token') },
                })
                if (response.status === 401) {
                    location = '/'
                    return
                }
                if (!response.ok) {
                    error.textContent += ' The export failed (HTTP ' + response.status + ').'
                    return
                }
                const warning = response.headers.get('X-Mock-Warning')
                if (warning) error.textContent += ' ' + warning
                const link = document.createElement('a')
                link.href = URL.createObjectURL(new Blob([await response.text()], { type: 'text/csv' }))
                link.download = button.dataset.list + '.csv'
                link.dispatchEvent(new MouseEvent('click'))
            } finally {
                button.textContent = 'Export'
            }
        }

        function expireToken() {
            localStorage.setItem('access_token_timestamp', '0')
            document.body.append(document.createElement('i')) // The bridge notices changes to the page, not to storage
        }

        (async () => {
            const code = query.get('code')
            if (code) {
                history.replaceState(null, '', '/')
                const response = await fetch('/token', { method: 'POST', body: code })
                localStorage.setItem('access_token', await response.text())
                localStorage.setItem('access_token_timestamp', String(Date.now()))
            }
            if (query.has('expireToken')) expireToken()
            const age = Date.now() - Number(localStorage.getItem('access_token_timestamp'))
            if (localStorage.getItem('access_token') && age < 3600000)
                playlists.append(document.getElementById('playlistTable').content.cloneNode(true))
            if (query.has('expireWhenAsked')) setInterval(async () => {
                if (await fetch('/expire').then(r => r.text()).catch(() => '') === 'expire') expireToken()
            }, 250)
        })()
        </script>
        </body>
        </html>
        """;

    private const string SignInPage = $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Spotify login (mock)</title>
        </head>
        <body>
        <h1>Spotify login (mock)</h1>
        <button id="signIn">Sign in</button>
        <p><a href="https://www.spotify.com/signup">Sign up for Spotify</a> <a href="https://support.spotify.com/" target="_blank">Help</a></p>
        <script>
        function signIn() {
            document.cookie = '{{SessionCookie}}=1; path=/; max-age=31536000'
            location = 'http://localhost:' + location.port + '/?code=mock-authorization-code'
        }
        document.getElementById('signIn').addEventListener('click', signIn)
        if (new URLSearchParams(location.search).has('auto')) setTimeout(async function signInWhenReleased() {
            const hold = await fetch('/sign-in-hold').then(r => r.text()).catch(() => 'released')
            if (hold === 'held') setTimeout(signInWhenReleased, 200)
            else signIn()
        }, 2500)
        </script>
        </body>
        </html>
        """;

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(context.RequestAborted);
    }

    private static async Task WriteAsync(HttpResponse response, string contentType, string text)
    {
        response.ContentType = contentType;
        await response.WriteAsync(text, Encoding.UTF8, response.HttpContext.RequestAborted);
    }

    [GeneratedRegex("^/export/([^/]+)$")]
    private static partial Regex ExportPath();

    [GeneratedRegex(@"^/demo-cover/(.+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex DemoCoverPath();
}
