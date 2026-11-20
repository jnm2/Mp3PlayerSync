using System.Text.Json;
using System.Text.RegularExpressions;
using Mp3PlayerSync.EndToEnd.Harness;
using Shouldly;

namespace Mp3PlayerSync.EndToEnd;

/// <summary>
/// Syncing to player drives, which the app lists as removable but which are folders here. Where the songs go is decided
/// from what's on the card each time it's chosen, never remembered (PLAYER-7). Each test syncs Playlist 5 (six songs,
/// and a playlist, unlike one-song playlists) to a card of its own with an empty MUSIC folder and a file of the user's.
/// </summary>
[Category(TestCategories.Drives)]
public sealed partial class PlayerDriveTests
{
    private const string Playlist5 = "Spotify playlist: Playlist 5";

    [ClassDataSource<RunContext>(Shared = SharedType.None)]
    public required RunContext Run { get; init; }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task A_card_with_a_MUSIC_folder_and_nothing_from_the_app_is_synced_into_it(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Playlist 5", cancellationToken);
        var card = MakeCard("E2E Walkman", "Music");

        var synced = await SyncToAsync(checks, new(@"W:\", "E2E WALKMAN", "Usb", card), cancellationToken);
        checks.Check("[PLAYER-7] A card with a MUSIC folder and nothing from the app is synced into it",
            () => synced.ShouldAllBe(song => AppSteps.Like(song, @"Music\Playlist 5\*.m4a")));
        checks.Check("[PLAYER-7] Nothing is added at that card's root",
            () => Directory.EnumerateFileSystemEntries(card).Select(Path.GetFileName).ShouldBe(["Music", "Notes.txt"], ignoreOrder: true));
        CheckNothingRemembered(checks);
        Run.Finish(checks);
    }

    [Test]
    [Timeout(10 * 60_000)]
    public async Task A_card_the_app_synced_to_at_its_root_stays_at_its_root(CancellationToken cancellationToken)
    {
        var checks = new Checks();
        await Run.PrepareAsync("Playlist 5", cancellationToken);
        var card = MakeCard("E2E Old Card", "MUSIC");
        // A playlist folder the app synced to the card's root before
        Directory.CreateDirectory(Path.Combine(card, "Road Trip"));
        File.WriteAllText(Path.Combine(card, @"Road Trip\.mp3playersync-playlist.json"), "{}");

        var synced = await SyncToAsync(checks, new(@"X:\", "E2E OLD CARD", "SecureDigital", card), cancellationToken);
        checks.Check("[PLAYER-7] A card the app synced to at its root stays at its root, despite its empty MUSIC folder",
            () => synced.ShouldAllBe(song => AppSteps.Like(song, @"Playlist 5\*.m4a")));
        checks.Check("[PLAYER-7] ...and its MUSIC folder is left empty",
            () => Directory.EnumerateFileSystemEntries(Path.Combine(card, "MUSIC")).ShouldBeEmpty());
        CheckNothingRemembered(checks);
        Run.Finish(checks);
    }

    private sealed record Drive(string Root, string Label, string Bus, string Folder);

    /// <summary>A card with an empty MUSIC folder, in its own casing, and a file of the user's.</summary>
    private string MakeCard(string name, string musicFolder)
    {
        var card = Run.CreateFolder(name);
        Directory.CreateDirectory(Path.Combine(card, musicFolder));
        File.WriteAllText(Path.Combine(card, "Notes.txt"), "mine");
        return card;
    }

    /// <summary>Chooses the drive, opens Playlist 5 and syncs it, then returns where its songs went, relative to the card.</summary>
    private async Task<string[]> SyncToAsync(Checks checks, Drive drive, CancellationToken cancellationToken)
    {
        const long gigabyte = 1L << 30;
        var drives = JsonSerializer.Serialize(new[]
        {
            new { root = drive.Root, label = drive.Label, bus = drive.Bus, totalBytes = 16 * gigabyte, freeBytes = 8 * gigabyte, folder = drive.Folder },
        });
        var app = await Run.StartAppAsync(Run.Launch() with { TestDrives = drives }, cancellationToken);
        var grid = await app.UntilAsync(() => app.FindById("PlayerGridView"), "the MP3 player page");
        (await app.UntilAsync(() => grid.FindListItem(drive.Label), $"the {drive.Label} drive", 10)).Invoke();
        // A newly chosen player may show the library rather than reopen the last playlist.
        await app.UntilAsync(() => app.Text("PlaylistTitle") == Playlist5 || app.FindById("LibraryGridView") is not null, "the library or the last opened playlist", 60);
        if (app.Text("PlaylistTitle") != Playlist5)
        {
            await app.SelectFilterAsync("All");
            await app.OpenLibraryItemAsync("Playlist 5");
        }
        await app.WaitContentsAsync(checks, $"Playlist 5 on {drive.Label}", "6 tracks, *");

        (await app.UntilAsync(() => app.FindById("SyncButton") is { IsEnabled: true } button ? button : null, "the Sync button", 15)).Invoke();
        var songs = await app.UntilAsync(() => Songs(drive.Folder) is { Length: 6 } six ? six : null, $"the six songs on {drive.Label}", 60);
        await app.UntilAsync(() => OnPlayer().IsMatch(app.AllText()), "the synced song to show as on the player", 30);
        // The sync goes on after the last file is in place (the playlist folder's marker, then the result), and closing
        // the window before then asks "Stop syncing and close?" (SYNC-10).
        await app.UntilAsync(() => app.AllText().Contains("Synced 6 songs to", StringComparison.OrdinalIgnoreCase), $"the sync to {drive.Label} to finish", 30);
        checks.Passed($"[PLAYER-7] The scan reads where {drive.Label} was synced: the synced song is on the player");
        await app.CloseAsync(cancellationToken);
        return [.. songs.Select(song => Path.GetRelativePath(drive.Folder, song))];
    }

    private void CheckNothingRemembered(Checks checks)
    {
        var settings = File.ReadAllText(Run.SettingsFile);
        checks.Check("[PLAYER-7] Where to sync is decided from the card, not remembered", () => settings.ShouldNotContain("\"musicFolders\""));
    }

    /// <summary>The songs on a card, which the sync may be writing to at the time.</summary>
    private static string[] Songs(string card)
    {
        try
        {
            return Directory.GetFiles(card, "*.m4a", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return [];
        }
    }

    [GeneratedRegex("(?mi)^On player$")]
    private static partial Regex OnPlayer();
}
