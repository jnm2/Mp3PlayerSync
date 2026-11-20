using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mp3PlayerSync.EndToEnd.Launcher;

/// <summary>
/// One of a few machine-wide slots for an end-to-end run, shared with tests\Invoke-EndToEnd.ps1: runs go in parallel,
/// but only so many at once, or they starve each other of CPU and time out.
/// </summary>
/// <remarks>
/// Each slot is a machine-wide mutex: a run that dies holding one leaves it abandoned, which is fine to take, so no slot
/// is ever lost. Each run notes itself in a queue folder so the oldest waiters go first and a waiting run can say what
/// it's waiting for, instead of looking hung. A mutex belongs to the thread that took it, so the slot is released on
/// the thread that took it.
/// </remarks>
internal sealed class SlotQueue : IDisposable
{
    public static readonly string QueueFolder = Path.Combine(Path.GetTempPath(), "Mp3PlayerSyncEndToEndQueue");
    private static readonly int SlotCount = Math.Max(2, Environment.ProcessorCount / 4);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string entryPath;
    private readonly Mutex slot;

    private SlotQueue(string entryPath, Mutex slot)
    {
        this.entryPath = entryPath;
        this.slot = slot;
    }

    public static SlotQueue Enter(string from, string only)
    {
        Directory.CreateDirectory(QueueFolder);
        var entryPath = Path.Combine(QueueFolder, $"{Environment.ProcessId}.json");
        var entry = new QueueEntry(Environment.ProcessId, DateTime.Now, from, only, Running: false);
        Write(entryPath, entry);

        var waitStarted = Stopwatch.StartNew();
        var lastReport = (TimeSpan?)null;
        try
        {
            while (true)
            {
                var entries = ReadEntries();
                var ahead = entries.Count(e => !e.Running && e.Pid != entry.Pid && e.Since < entry.Since);
                // Only the longest-waiting runs try, so a later run can't take a slot first.
                if (ahead < SlotCount && TryTakeSlot() is { } slot)
                {
                    if (lastReport is not null) Console.WriteLine($"{DateTime.Now:HH:mm:ss} Starting after waiting {waitStarted.Elapsed:m\\:ss}");
                    entry = entry with { Running = true, Since = DateTime.Now };
                    Write(entryPath, entry);
                    return new SlotQueue(entryPath, slot);
                }

                if (lastReport is null || waitStarted.Elapsed - lastReport >= TimeSpan.FromSeconds(30))
                {
                    lastReport = waitStarted.Elapsed;
                    var running = entries.Where(e => e.Running).Select(e => $"{Path.GetFileName(e.From)}{(e.Only is { Length: > 0 } o ? $" ({o})" : "")} since {e.Since:HH:mm:ss}").ToList();
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} Waiting {waitStarted.Elapsed:m\\:ss} for one of {SlotCount} slots; running: {(running.Count > 0 ? string.Join(", ", running) : "runs from older scripts")}; {ahead} queued ahead of this one");
                }
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            File.Delete(entryPath);
            throw;
        }
    }

    public void Dispose()
    {
        File.Delete(entryPath);
        slot.ReleaseMutex();
        slot.Dispose();
    }

    private static Mutex? TryTakeSlot()
    {
        for (var i = 0; i < SlotCount; i++)
        {
            var slot = new Mutex(initiallyOwned: false, $@"Global\Mp3PlayerSyncEndToEndSlot-{i}");
            bool taken;
            try { taken = slot.WaitOne(0); }
            catch (AbandonedMutexException) { taken = true; }
            if (taken) return slot;
            slot.Dispose();
        }
        return null;
    }

    private static List<QueueEntry> ReadEntries()
    {
        var entries = new List<QueueEntry>();
        foreach (var file in Directory.EnumerateFiles(QueueFolder, "*.json"))
        {
            QueueEntry? other;
            try { other = JsonSerializer.Deserialize<QueueEntry>(File.ReadAllText(file), Json); }
            catch (Exception exception) when (exception is IOException or JsonException) { continue; }
            // Gone, or the ID now belongs to another process: a run that ended without cleaning up.
            if (other is null || !IsStillRunning(other))
            {
                try { File.Delete(file); }
                catch (IOException) { }
                continue;
            }
            entries.Add(other);
        }
        return entries;
    }

    private static bool IsStillRunning(QueueEntry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            return process.StartTime <= entry.Since;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Write(string path, QueueEntry entry) => File.WriteAllText(path, JsonSerializer.Serialize(entry, Json));

    // As Invoke-EndToEnd.ps1 writes them: {"pid":…,"since":"…","from":"…","only":"…","running":…}
    private sealed record QueueEntry(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("since")] DateTime Since,
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("only")] string Only,
        [property: JsonPropertyName("running")] bool Running);
}
