using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class PlayersTests
{
    private static DriveCandidate Drive(string root, DriveType type, StorageBus bus, string label = "", string? volumeId = null, uint? serial = null, bool system = false) =>
        new(root, label, type, bus, system, volumeId ?? $@"\\?\Volume{{{root[0]}}}\", serial, 32L << 30, 12L << 30);

    [Test]
    [Arguments(DriveType.Removable, StorageBus.Usb, false, true)] // USB stick or player
    [Arguments(DriveType.Removable, StorageBus.SecureDigital, false, true)] // SD card
    [Arguments(DriveType.Removable, StorageBus.Unknown, false, true)]
    [Arguments(DriveType.Fixed, StorageBus.Usb, false, true)] // Player that reports itself as fixed
    [Arguments(DriveType.Fixed, StorageBus.Mmc, false, true)]
    [Arguments(DriveType.Fixed, StorageBus.Other, false, false)] // Internal SATA or NVMe disk
    [Arguments(DriveType.Fixed, StorageBus.Unknown, false, false)]
    [Arguments(DriveType.Network, StorageBus.Unknown, false, false)]
    [Arguments(DriveType.CDRom, StorageBus.Other, false, false)]
    [Arguments(DriveType.Removable, StorageBus.Usb, true, false)] // Windows running from USB
    public async Task Is_player(DriveType type, StorageBus bus, bool system, bool expected)
    {
        await Assert.That(Players.IsPlayer(Drive("E:\\", type, bus, system: system))).IsEqualTo(expected);
    }

    [Test]
    public async Task Offers_player_drives_then_other_folder()
    {
        var (options, remembered) = Players.BuildOptions(
            [
                Drive("C:\\", DriveType.Fixed, StorageBus.Other, system: true),
                Drive("F:\\", DriveType.Removable, StorageBus.SecureDigital),
                Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANSA CLIP"),
            ],
            remembered: null, folderExists: _ => true);

        await Assert.That(options.Select(o => (o.Kind, o.Title, o.Subtitle))).IsEquivalentTo(new[]
        {
            (PlayerOptionKind.Drive, "SANSA CLIP", "Drive E:"),
            (PlayerOptionKind.Drive, "Memory card", "Drive F:"),
            (PlayerOptionKind.OtherFolder, "Other folder", "Choose any folder"),
        }, CollectionOrdering.Matching);
        await Assert.That(options[0].Detail).IsEqualTo("12 GB free of 32 GB");
        await Assert.That(remembered).IsNull();
    }

    [Test]
    public async Task Remembers_a_drive_by_volume_whatever_its_letter()
    {
        var chosen = Players.ChoiceFor(Players.BuildOptions([Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-1", serial: 7)], null, _ => true).Options[0]);

        var (options, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-2"), Drive("G:\\", DriveType.Removable, StorageBus.Usb, volumeId: "VOL-1")],
            chosen, _ => true);

        await Assert.That(chosen).IsEqualTo(new PlayerChoice("vol-1", 7, ""));
        await Assert.That(remembered?.Drive?.RootPath).IsEqualTo("G:\\");
        await Assert.That(options).Contains(remembered!);
    }

    [Test]
    public async Task Falls_back_to_volume_serial_and_label()
    {
        var (_, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "CARD", volumeId: "elsewhere", serial: 42)],
            new PlayerChoice("vol-1", 42, "CARD"), _ => true);

        await Assert.That(remembered?.Drive?.RootPath).IsEqualTo("E:\\");
    }

    [Test]
    public async Task Absent_drive_is_not_remembered()
    {
        var (_, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-2", serial: 1)],
            new PlayerChoice("vol-1", 42, "CARD"), _ => true);

        await Assert.That(remembered).IsNull();
    }

    [Test]
    public async Task Remembered_folder_is_offered_while_it_exists()
    {
        var choice = new PlayerChoice(FolderPath: @"D:\Music\Player\");

        var (options, remembered) = Players.BuildOptions([], choice, _ => true);
        await Assert.That(options.Select(o => o.Title)).IsEquivalentTo(new[] { "Player", "Other folder" }, CollectionOrdering.Matching);
        await Assert.That(remembered).IsSameReferenceAs(options[0]);
        await Assert.That(Players.ChoiceFor(options[0])).IsEqualTo(choice);

        var (withoutFolder, none) = Players.BuildOptions([], choice, _ => false);
        await Assert.That((withoutFolder.Count, none)).IsEqualTo((1, (PlayerOption?)null));
    }

    [Test]
    public async Task Remembered_root_of_a_player_drive_is_that_player()
    {
        var (options, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANDISK", volumeId: "vol-1")],
            new PlayerChoice(FolderPath: @"E:\"), _ => true);

        await Assert.That(options.Select(o => o.Kind)).IsEquivalentTo(new[] { PlayerOptionKind.Drive, PlayerOptionKind.OtherFolder }, CollectionOrdering.Matching);
        await Assert.That(remembered).IsSameReferenceAs(options[0]);
        await Assert.That(Players.ChoiceFor(remembered!).VolumeId).IsEqualTo("vol-1");
        await Assert.That(remembered!.DisplayName).IsEqualTo("SANDISK (E:)");
    }

    [Test]
    public async Task Remembered_root_of_another_drive_is_a_recent_folder()
    {
        var (options, remembered) = Players.BuildOptions([], new PlayerChoice(FolderPath: @"F:\"), _ => true);

        await Assert.That((remembered?.Kind, remembered?.Title, remembered?.Subtitle)).IsEqualTo((PlayerOptionKind.Folder, @"F:\", "Recent folder"));
        await Assert.That(remembered!.DisplayName).IsEqualTo(@"Folder (F:\)");
        await Assert.That(options).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Folder_dialog_opens_in_the_player_folder_then_the_last_picked_one()
    {
        var exists = new HashSet<string>([@"D:\Player", @"D:\Picked"]).Contains;

        await Assert.That(Players.DialogStartFolder(@"D:\Player", @"D:\Picked", exists)).IsEqualTo(@"D:\Player");
        await Assert.That(Players.DialogStartFolder(null, @"D:\Picked", exists)).IsEqualTo(@"D:\Picked");
        await Assert.That(Players.DialogStartFolder(@"D:\Gone", @"D:\Picked", exists)).IsEqualTo(@"D:\Picked");
        await Assert.That(Players.DialogStartFolder(@"D:\Gone", null, exists)).IsNull();
    }

    [Test]
    public async Task Long_folder_subtitles_are_shortened_in_the_middle_with_the_full_path_as_a_tooltip()
    {
        var longFolder = Players.ForFolder(@"C:\Users\someone\OneDrive\Documents\Music\Players\mp3player", []);
        await Assert.That(longFolder.ShortSubtitle).StartsWith(@"C:\…\").And.EndsWith(@"\mp3player");
        await Assert.That(longFolder.SubtitleToolTip).IsEqualTo(longFolder.FolderPath);

        var shortFolder = Players.ForFolder(@"D:\Music\Player", []);
        await Assert.That((shortFolder.ShortSubtitle, shortFolder.SubtitleToolTip)).IsEqualTo((@"D:\Music\Player", (string?)null));
    }

    [Test]
    public async Task Folder_is_named_by_its_path_once_chosen()
    {
        var option = Players.ForFolder(@"C:\Users\me\Desktop\mp3player", []);

        await Assert.That((option.Title, option.Subtitle, option.DisplayName)).IsEqualTo(("mp3player", @"C:\Users\me\Desktop\mp3player", @"Folder (C:\Users\me\Desktop\mp3player)"));
    }

    [Test]
    [Arguments(@"D:\Music\Player")]
    [Arguments(@"E:\")] // Root of a player drive
    [Arguments(@"F:\")] // Root of another drive
    [Arguments(@"\\server\share")] // Root of a network share
    [Arguments(@"\\server\share\Player")]
    public async Task No_tile_shows_the_same_text_twice(string rememberedFolder)
    {
        // TEXT-13 in docs/design-decisions.md
        var (options, _) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANDISK"), Drive("G:\\", DriveType.Removable, StorageBus.SecureDigital)],
            new PlayerChoice(FolderPath: rememberedFolder), _ => true);

        using (Assert.Multiple())
        {
            foreach (var option in options)
            {
                var texts = new[] { option.Title, option.Subtitle, option.Detail }.Where(t => t.Length > 0).ToList();
                await Assert.That(texts).HasDistinctItems().Because($"Tile '{option.Title}'");
            }
        }
    }

    [Test]
    [Arguments(100L, 50L, false)]
    [Arguments(100L, 11L, false)]
    [Arguments(100L, 10L, true)] // 90% used
    [Arguments(100L, 0L, true)]
    public async Task Drive_is_nearly_full_from_90_percent_used(long total, long free, bool expected)
    {
        var option = new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", Drive("E:\\", DriveType.Removable, StorageBus.Usb) with { TotalBytes = total, FreeBytes = free });
        await Assert.That(option.UsedFraction).IsEqualTo((total - free) / (double)total);
        await Assert.That(option.IsNearlyFull).IsEqualTo(expected);
    }

    [Test]
    public async Task Usage_is_only_shown_for_drives_of_known_size()
    {
        var drive = Drive("E:\\", DriveType.Removable, StorageBus.Usb);
        await Assert.That(new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", drive).ShowsUsage).IsTrue();
        await Assert.That(new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", drive with { TotalBytes = 0 }).ShowsUsage).IsFalse();
        await Assert.That(new PlayerOption(PlayerOptionKind.Folder, "Player", "", "", FolderPath: @"D:\Music").ShowsUsage).IsFalse();
    }

    [Test]
    [Arguments(@"C:\Users\me\Desktop\mp3player", @"C:\Users\me\Desktop\mp3player")] // Short enough
    [Arguments(@"C:\Users\me\AppData\Local\Temp\Mp3PlayerSyncE2E-22808\E2E Player", @"C:\…\Mp3PlayerSyncE2E-22808\E2E Player")]
    [Arguments(@"C:\Users\me\AppData\Local\Temp\A very long folder name for the player", @"C:\…\A very long folder name for the player")] // The last folder is always kept
    [Arguments(@"D:\A folder name that is far too long to fit", @"D:\A folder name that is far too long to fit")] // Nothing to leave out
    [Arguments(@"\\server\share\music\players\sandisk\clip sport", @"\\server\share\…\sandisk\clip sport")]
    public async Task ShortenPath_keeps_the_drive_and_the_last_folders(string path, string expected)
    {
        await Assert.That(Players.ShortenPath(path)).IsEqualTo(expected);
    }

    [Test]
    public async Task ShortDisplayName_shortens_only_folders()
    {
        var folder = Players.ForFolder(@"C:\Users\me\AppData\Local\Temp\Mp3PlayerSyncE2E-22808\E2E Player", []);
        await Assert.That(folder.ShortDisplayName).IsEqualTo(@"Folder (C:\…\Mp3PlayerSyncE2E-22808\E2E Player)");
        var drive = new PlayerOption(PlayerOptionKind.Drive, "SANDISK", "", "", Drive(@"E:\", DriveType.Removable, StorageBus.Usb));
        await Assert.That(drive.ShortDisplayName).IsEqualTo(drive.DisplayName);
    }

    [Test]
    public async Task A_locked_card_says_so()
    {
        var (options, _) = Players.BuildOptions([Drive("E:\\", DriveType.Removable, StorageBus.SecureDigital) with { IsWriteProtected = true }], null, _ => false);
        await Assert.That(options[0].Detail).IsEqualTo("This card is locked");
    }
}
