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
    public void Is_player(DriveType type, StorageBus bus, bool system, bool expected)
    {
        Players.IsPlayer(Drive("E:\\", type, bus, system: system)).ShouldBe(expected);
    }

    [Test]
    public void Offers_player_drives_then_other_folder()
    {
        var (options, remembered) = Players.BuildOptions(
            [
                Drive("C:\\", DriveType.Fixed, StorageBus.Other, system: true),
                Drive("F:\\", DriveType.Removable, StorageBus.SecureDigital),
                Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANSA CLIP"),
            ],
            remembered: null, folderExists: _ => true);

        options.Select(o => (o.Kind, o.Title, o.Subtitle)).ShouldBe(new[]
        {
            (PlayerOptionKind.Drive, "SANSA CLIP", "Drive E:"),
            (PlayerOptionKind.Drive, "Memory card", "Drive F:"),
            (PlayerOptionKind.OtherFolder, "Other folder", "Choose any folder"),
        });
        options[0].Detail.ShouldBe("12 GB free of 32 GB");
        remembered.ShouldBeNull();
    }

    [Test]
    public void Remembers_a_drive_by_volume_whatever_its_letter()
    {
        var chosen = Players.ChoiceFor(Players.BuildOptions([Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-1", serial: 7)], null, _ => true).Options[0]);

        var (options, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-2"), Drive("G:\\", DriveType.Removable, StorageBus.Usb, volumeId: "VOL-1")],
            chosen, _ => true);

        chosen.ShouldBe(new PlayerChoice("vol-1", 7, ""));
        remembered.ShouldNotBeNull().Drive.ShouldNotBeNull().RootPath.ShouldBe("G:\\");
        options.ShouldContain(remembered);
    }

    [Test]
    public void Falls_back_to_volume_serial_and_label()
    {
        var (_, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "CARD", volumeId: "elsewhere", serial: 42)],
            new PlayerChoice("vol-1", 42, "CARD"), _ => true);

        remembered.ShouldNotBeNull().Drive.ShouldNotBeNull().RootPath.ShouldBe("E:\\");
    }

    [Test]
    public void Absent_drive_is_not_remembered()
    {
        var (_, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-2", serial: 1)],
            new PlayerChoice("vol-1", 42, "CARD"), _ => true);

        remembered.ShouldBeNull();
    }

    [Test]
    public void Remembered_folder_is_offered_while_it_exists()
    {
        var choice = new PlayerChoice(FolderPath: @"D:\Music\Player\");

        var (options, remembered) = Players.BuildOptions([], choice, _ => true);
        options.Select(o => o.Title).ShouldBe(new[] { "Player", "Other folder" });
        remembered.ShouldBeSameAs(options[0]);
        Players.ChoiceFor(options[0]).ShouldBe(choice);

        var (withoutFolder, none) = Players.BuildOptions([], choice, _ => false);
        (withoutFolder.Count, none).ShouldBe((1, (PlayerOption?)null));
    }

    [Test]
    public void Remembered_root_of_a_player_drive_is_that_player()
    {
        var (options, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANDISK", volumeId: "vol-1")],
            new PlayerChoice(FolderPath: @"E:\"), _ => true);

        options.Select(o => o.Kind).ShouldBe(new[] { PlayerOptionKind.Drive, PlayerOptionKind.OtherFolder });
        var tile = remembered.ShouldNotBeNull();
        tile.ShouldBeSameAs(options[0]);
        Players.ChoiceFor(tile).VolumeId.ShouldBe("vol-1");
        tile.DisplayName.ShouldBe("SANDISK (E:)");
    }

    [Test]
    public void Remembered_root_of_another_drive_is_a_recent_folder()
    {
        var (options, remembered) = Players.BuildOptions([], new PlayerChoice(FolderPath: @"F:\"), _ => true);

        remembered.ShouldNotBeNull().ShouldSatisfyAllConditions(
            r => r.Kind.ShouldBe(PlayerOptionKind.Folder),
            r => r.Title.ShouldBe(@"F:\"),
            r => r.Subtitle.ShouldBe("Recent folder"),
            r => r.DisplayName.ShouldBe(@"Folder (F:\)"));
        options.Count.ShouldBe(2);
    }

    [Test]
    public void Folder_dialog_opens_in_the_player_folder_then_the_last_picked_one()
    {
        var exists = new HashSet<string>([@"D:\Player", @"D:\Picked"]).Contains;

        Players.DialogStartFolder(@"D:\Player", @"D:\Picked", exists).ShouldBe(@"D:\Player");
        Players.DialogStartFolder(null, @"D:\Picked", exists).ShouldBe(@"D:\Picked");
        Players.DialogStartFolder(@"D:\Gone", @"D:\Picked", exists).ShouldBe(@"D:\Picked");
        Players.DialogStartFolder(@"D:\Gone", null, exists).ShouldBeNull();
    }

    [Test]
    public void Long_folder_subtitles_are_shortened_in_the_middle_with_the_full_path_as_a_tooltip()
    {
        var longFolder = Players.ForFolder(@"C:\Users\someone\OneDrive\Documents\Music\Players\mp3player", []);
        longFolder.ShouldSatisfyAllConditions(
            f => f.ShortSubtitle.ShouldStartWith(@"C:\…\", Case.Sensitive),
            f => f.ShortSubtitle.ShouldEndWith(@"\mp3player", Case.Sensitive),
            f => f.SubtitleToolTip.ShouldBe(f.FolderPath));

        var shortFolder = Players.ForFolder(@"D:\Music\Player", []);
        shortFolder.ShouldSatisfyAllConditions(
            s => s.ShortSubtitle.ShouldBe(@"D:\Music\Player"),
            s => s.SubtitleToolTip.ShouldBeNull());
    }

    [Test]
    public void Folder_is_named_by_its_path_once_chosen()
    {
        var option = Players.ForFolder(@"C:\Users\me\Desktop\mp3player", []);

        option.ShouldSatisfyAllConditions(
            o => o.Title.ShouldBe("mp3player"),
            o => o.Subtitle.ShouldBe(@"C:\Users\me\Desktop\mp3player"),
            o => o.DisplayName.ShouldBe(@"Folder (C:\Users\me\Desktop\mp3player)"));
    }

    [Test]
    [Arguments(@"D:\Music\Player")]
    [Arguments(@"E:\")] // Root of a player drive
    [Arguments(@"F:\")] // Root of another drive
    [Arguments(@"\\server\share")] // Root of a network share
    [Arguments(@"\\server\share\Player")]
    public void No_tile_shows_the_same_text_twice(string rememberedFolder)
    {
        // TEXT-13 in docs/design-decisions.md
        var (options, _) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANDISK"), Drive("G:\\", DriveType.Removable, StorageBus.SecureDigital)],
            new PlayerChoice(FolderPath: rememberedFolder), _ => true);

        options.ShouldAllBe(option => ShownTexts(option).Distinct().Count() == ShownTexts(option).Count);
    }

    private static List<string> ShownTexts(PlayerOption option) =>
        new[] { option.Title, option.Subtitle, option.Detail }.Where(t => t.Length > 0).ToList();

    [Test]
    [Arguments(100L, 50L, false)]
    [Arguments(100L, 11L, false)]
    [Arguments(100L, 10L, true)] // 90% used
    [Arguments(100L, 0L, true)]
    public void Drive_is_nearly_full_from_90_percent_used(long total, long free, bool expected)
    {
        var option = new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", Drive("E:\\", DriveType.Removable, StorageBus.Usb) with { TotalBytes = total, FreeBytes = free });
        option.UsedFraction.ShouldBe((total - free) / (double)total);
        option.IsNearlyFull.ShouldBe(expected);
    }

    [Test]
    public void Usage_is_only_shown_for_drives_of_known_size()
    {
        var drive = Drive("E:\\", DriveType.Removable, StorageBus.Usb);
        new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", drive).ShowsUsage.ShouldBeTrue();
        new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", drive with { TotalBytes = 0 }).ShowsUsage.ShouldBeFalse();
        new PlayerOption(PlayerOptionKind.Folder, "Player", "", "", FolderPath: @"D:\Music").ShowsUsage.ShouldBeFalse();
    }

    [Test]
    [Arguments(@"C:\Users\me\Desktop\mp3player", @"C:\Users\me\Desktop\mp3player")] // Short enough
    [Arguments(@"C:\Users\me\AppData\Local\Temp\Mp3PlayerSyncE2E-22808\E2E Player", @"C:\…\Mp3PlayerSyncE2E-22808\E2E Player")]
    [Arguments(@"C:\Users\me\AppData\Local\Temp\A very long folder name for the player", @"C:\…\A very long folder name for the player")] // The last folder is always kept
    [Arguments(@"D:\A folder name that is far too long to fit", @"D:\A folder name that is far too long to fit")] // Nothing to leave out
    [Arguments(@"\\server\share\music\players\sandisk\clip sport", @"\\server\share\…\sandisk\clip sport")]
    public void ShortenPath_keeps_the_drive_and_the_last_folders(string path, string expected)
    {
        Players.ShortenPath(path).ShouldBe(expected);
    }

    [Test]
    public void ShortDisplayName_shortens_only_folders()
    {
        var folder = Players.ForFolder(@"C:\Users\me\AppData\Local\Temp\Mp3PlayerSyncE2E-22808\E2E Player", []);
        folder.ShortDisplayName.ShouldBe(@"Folder (C:\…\Mp3PlayerSyncE2E-22808\E2E Player)");
        var drive = new PlayerOption(PlayerOptionKind.Drive, "SANDISK", "", "", Drive(@"E:\", DriveType.Removable, StorageBus.Usb));
        drive.ShortDisplayName.ShouldBe(drive.DisplayName);
    }

    [Test]
    public void A_locked_card_says_so()
    {
        var (options, _) = Players.BuildOptions([Drive("E:\\", DriveType.Removable, StorageBus.SecureDigital) with { IsWriteProtected = true }], null, _ => false);
        options[0].Detail.ShouldBe("This card is locked");
    }
}
