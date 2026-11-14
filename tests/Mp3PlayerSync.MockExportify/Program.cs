using Mp3PlayerSync.MockExportify;

// Serves the mock Exportify until stopped (Ctrl+C): on http://localhost:5178/, or with --port, on that port, or on a
// free one with --port 0. With --demo it lists the made-up library in DemoLibrary.json, for the README's screenshots,
// instead of the one the end-to-end tests check. The first line it prints is the address, which the tests read.
var port = 5178;
var library = MockLibrary.Test;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var chosen) && chosen >= 0) port = chosen;
    else if (args[i] is "--demo") library = MockLibrary.LoadDemo(Path.Combine(AppContext.BaseDirectory, "DemoLibrary.json"));
    else
    {
        Console.Error.WriteLine("Usage: Mp3PlayerSync.MockExportify [--port <port, or 0 for a free one>] [--demo]");
        return 2;
    }
}

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.TrySetResult();
};

await using var server = await MockExportifyServer.StartAsync(library, port, Console.WriteLine);
Console.WriteLine($"Mock Exportify listening on {server.Url}");
await stopped.Task;
return 0;
