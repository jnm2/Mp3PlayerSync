using Mp3PlayerSync.MockExportify;

// Serves the mock Exportify until stopped (Ctrl+C): on http://localhost:5178/, or with --port, on that port, or on a
// free one with --port 0. The first line it prints is the address, which tests\Test-MockEndToEnd.ps1 reads.
var port = 5178;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var chosen) && chosen >= 0) port = chosen;
    else
    {
        Console.Error.WriteLine("Usage: Mp3PlayerSync.MockExportify [--port <port, or 0 for a free one>]");
        return 2;
    }
}

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.TrySetResult();
};

await using var server = await MockExportifyServer.StartAsync(Path.Combine(AppContext.BaseDirectory, "MockExportify"), port, Console.WriteLine);
Console.WriteLine($"Mock Exportify listening on {server.Url}");
await stopped.Task;
return 0;
