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
/// Stands in for exportify.net, and for Spotify's sign-in pages on another origin (127.0.0.1 rather than localhost), for
/// the end-to-end tests: serves the pages in tests\MockExportify, and the signals the tests and those pages exchange.
/// </summary>
public sealed partial class MockExportifyServer : IAsyncDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css",
    };

    private readonly string root;
    private readonly Action<string> log;
    private readonly Lock gate = new();
    private readonly List<string> apiLog = [];
    private bool signInHeld, expireAsked;
    private int failed;
    private WebApplication? app;

    private MockExportifyServer(string root, Action<string> log)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        this.log = log;
    }

    /// <summary>The mock Exportify's home page, such as <c>http://localhost:5178/</c>.</summary>
    public Uri Url { get; private set; } = null!;

    public int Port => Url.Port;

    /// <summary>
    /// Starts serving <paramref name="root"/> on <paramref name="port"/>, or on a free port when it's 0. <paramref name="log"/>
    /// hears of each request the mock couldn't answer.
    /// </summary>
    public static async Task<MockExportifyServer> StartAsync(string root, int port, Action<string> log)
    {
        var server = new MockExportifyServer(root, log);
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
            // localhost is both loopbacks, and the browser may try either; the sign-in pages are on 127.0.0.1.
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
        var path = Uri.UnescapeDataString(request.Path.Value ?? "").TrimStart('/');
        var isPost = HttpMethods.IsPost(request.Method);

        // For fingerprint.html: what the browser sends, and somewhere to put its findings.
        if (Is(path, "echo-headers"))
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
            response.Headers.AccessControlAllowOrigin = "*";
            await WriteAsync(response, "application/json", JsonSerializer.Serialize(headers));
            return;
        }

        // The fake Spotify API answers inside the page, so the page reports each call here; the tests read the list
        // to tell when the app asked Spotify for something, such as a refresh, however quickly it was answered.
        if (Is(path, "api-log"))
        {
            if (isPost)
            {
                var call = await ReadBodyAsync(context);
                lock (gate) apiLog.Add(call);
            }
            else
            {
                string json;
                lock (gate) json = JsonSerializer.Serialize(apiLog);
                await WriteAsync(response, "application/json", json);
            }
            return;
        }

        if (Is(path, "report") && isPost)
        {
            var body = await ReadBodyAsync(context);
            lock (gate) File.AppendAllText(Path.Combine(Path.GetTempPath(), $"mock-exportify-report-{Port}.jsonl"), body + Environment.NewLine);
            response.Headers.AccessControlAllowOrigin = "*";
            return;
        }

        // The tests can hold the fake login page's ?auto sign-in (POST 'hold') until they've seen the sign-in screen,
        // then let it go on (POST 'release'); the page asks here (GET) while it waits.
        if (Is(path, "sign-in-hold"))
        {
            if (isPost)
            {
                var held = Is(await ReadBodyAsync(context), "hold");
                lock (gate) signInHeld = held;
            }
            else
            {
                bool held;
                lock (gate) held = signInHeld;
                response.Headers.CacheControl = "no-store";
                await WriteAsync(response, "text/plain", held ? "held" : "released");
            }
            return;
        }

        // The tests can make the page's token run out once they're watching (POST); the page, opened with
        // ?expireWhenAsked, asks here (GET) and is told once.
        if (Is(path, "expire"))
        {
            if (isPost)
            {
                lock (gate) expireAsked = true;
            }
            else
            {
                bool expire;
                lock (gate) (expire, expireAsked) = (expireAsked, false);
                response.Headers.CacheControl = "no-store";
                await WriteAsync(response, "text/plain", expire ? "expire" : "wait");
            }
            return;
        }

        if (DemoCoverPath().Match(path) is { Success: true } cover)
        {
            response.ContentType = "image/png";
            await response.Body.WriteAsync(DemoCovers.Draw(cover.Groups[1].Value), context.RequestAborted);
            return;
        }

        if (path is "") path = "index.html";
        var file = Path.GetFullPath(Path.Combine(root, path));
        if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
        {
            response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            response.Headers.CacheControl = "no-store";
            await response.SendFileAsync(file, context.RequestAborted);
        }
        else
        {
            response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"^demo-cover/(.+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex DemoCoverPath();
}
