using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

public class PlayersTests
{
    private static DriveCandidate Drive(string root, DriveType type, StorageBus bus, string label = "", string? volumeId = null, uint? serial = null, bool system = false) =>
        new(root, label, type, bus, system, volumeId ?? $@"\\?\Volume{{{root[0]}}}\", serial, 32L << 30, 12L << 30);

    [TestCase(DriveType.Removable, StorageBus.Usb, false, ExpectedResult = true, Description = "USB stick or player")]
    [TestCase(DriveType.Removable, StorageBus.SecureDigital, false, ExpectedResult = true, Description = "SD card")]
    [TestCase(DriveType.Removable, StorageBus.Unknown, false, ExpectedResult = true)]
    [TestCase(DriveType.Fixed, StorageBus.Usb, false, ExpectedResult = true, Description = "Player that reports itself as fixed")]
    [TestCase(DriveType.Fixed, StorageBus.Mmc, false, ExpectedResult = true)]
    [TestCase(DriveType.Fixed, StorageBus.Other, false, ExpectedResult = false, Description = "Internal SATA or NVMe disk")]
    [TestCase(DriveType.Fixed, StorageBus.Unknown, false, ExpectedResult = false)]
    [TestCase(DriveType.Network, StorageBus.Unknown, false, ExpectedResult = false)]
    [TestCase(DriveType.CDRom, StorageBus.Other, false, ExpectedResult = false)]
    [TestCase(DriveType.Removable, StorageBus.Usb, true, ExpectedResult = false, Description = "Windows running from USB")]
    public bool Is_player(DriveType type, StorageBus bus, bool system) => Players.IsPlayer(Drive("E:\\", type, bus, system: system));

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

        Assert.That(options.Select(o => (o.Kind, o.Title, o.Subtitle)), Is.EqualTo(new[]
        {
            (PlayerOptionKind.Drive, "SANSA CLIP", "Drive E:"),
            (PlayerOptionKind.Drive, "Memory card", "Drive F:"),
            (PlayerOptionKind.OtherFolder, "Other folder", "Choose any folder"),
        }));
        Assert.That(options[0].Detail, Is.EqualTo("12 GB free of 32 GB"));
        Assert.That(remembered, Is.Null);
    }

    [Test]
    public void Remembers_a_drive_by_volume_whatever_its_letter()
    {
        var chosen = Players.ChoiceFor(Players.BuildOptions([Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-1", serial: 7)], null, _ => true).Options[0]);

        var (options, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-2"), Drive("G:\\", DriveType.Removable, StorageBus.Usb, volumeId: "VOL-1")],
            chosen, _ => true);

        Assert.That(chosen, Is.EqualTo(new PlayerChoice("vol-1", 7, "")));
        Assert.That(remembered?.Drive?.RootPath, Is.EqualTo("G:\\"));
        Assert.That(options, Does.Contain(remembered));
    }

    [Test]
    public void Falls_back_to_volume_serial_and_label()
    {
        var (_, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "CARD", volumeId: "elsewhere", serial: 42)],
            new PlayerChoice("vol-1", 42, "CARD"), _ => true);

        Assert.That(remembered?.Drive?.RootPath, Is.EqualTo("E:\\"));
    }

    [Test]
    public void Absent_drive_is_not_remembered()
    {
        var (_, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, volumeId: "vol-2", serial: 1)],
            new PlayerChoice("vol-1", 42, "CARD"), _ => true);

        Assert.That(remembered, Is.Null);
    }

    [Test]
    public void Remembered_folder_is_offered_while_it_exists()
    {
        var choice = new PlayerChoice(FolderPath: @"D:\Music\Player\");

        var (options, remembered) = Players.BuildOptions([], choice, _ => true);
        Assert.That(options.Select(o => o.Title), Is.EqualTo(new[] { "Player", "Other folder" }));
        Assert.That(remembered, Is.SameAs(options[0]));
        Assert.That(Players.ChoiceFor(options[0]), Is.EqualTo(choice));

        var (withoutFolder, none) = Players.BuildOptions([], choice, _ => false);
        Assert.That((withoutFolder.Count, none), Is.EqualTo((1, (PlayerOption?)null)));
    }

    [Test]
    public void Remembered_root_of_a_player_drive_is_that_player()
    {
        var (options, remembered) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANDISK", volumeId: "vol-1")],
            new PlayerChoice(FolderPath: @"E:\"), _ => true);

        Assert.That(options.Select(o => o.Kind), Is.EqualTo(new[] { PlayerOptionKind.Drive, PlayerOptionKind.OtherFolder }));
        Assert.That(remembered, Is.SameAs(options[0]));
        Assert.That(Players.ChoiceFor(remembered!).VolumeId, Is.EqualTo("vol-1"));
        Assert.That(remembered!.DisplayName, Is.EqualTo("SANDISK (E:)"));
    }

    [Test]
    public void Remembered_root_of_another_drive_is_a_recent_folder()
    {
        var (options, remembered) = Players.BuildOptions([], new PlayerChoice(FolderPath: @"F:\"), _ => true);

        Assert.That((remembered?.Kind, remembered?.Title, remembered?.Subtitle), Is.EqualTo((PlayerOptionKind.Folder, @"F:\", "Recent folder")));
        Assert.That(remembered!.DisplayName, Is.EqualTo(@"Folder (F:\)"));
        Assert.That(options, Has.Count.EqualTo(2));
    }

    [Test]
    public void Folder_dialog_opens_in_the_player_folder_then_the_last_picked_one()
    {
        var exists = new HashSet<string>([@"D:\Player", @"D:\Picked"]).Contains;

        Assert.That(Players.DialogStartFolder(@"D:\Player", @"D:\Picked", exists), Is.EqualTo(@"D:\Player"));
        Assert.That(Players.DialogStartFolder(null, @"D:\Picked", exists), Is.EqualTo(@"D:\Picked"));
        Assert.That(Players.DialogStartFolder(@"D:\Gone", @"D:\Picked", exists), Is.EqualTo(@"D:\Picked"));
        Assert.That(Players.DialogStartFolder(@"D:\Gone", null, exists), Is.Null);
    }

    [Test]
    public void Long_folder_subtitles_are_shortened_in_the_middle_with_the_full_path_as_a_tooltip()
    {
        var longFolder = Players.ForFolder(@"C:\Users\someone\OneDrive\Documents\Music\Players\mp3player", []);
        Assert.That(longFolder.ShortSubtitle, Does.StartWith(@"C:\…\").And.EndWith(@"\mp3player"));
        Assert.That(longFolder.SubtitleToolTip, Is.EqualTo(longFolder.FolderPath));

        var shortFolder = Players.ForFolder(@"D:\Music\Player", []);
        Assert.That((shortFolder.ShortSubtitle, shortFolder.SubtitleToolTip), Is.EqualTo((@"D:\Music\Player", (string?)null)));
    }

    [Test]
    public void Folder_is_named_by_its_path_once_chosen()
    {
        var option = Players.ForFolder(@"C:\Users\me\Desktop\mp3player", []);

        Assert.That((option.Title, option.Subtitle, option.DisplayName), Is.EqualTo(("mp3player", @"C:\Users\me\Desktop\mp3player", @"Folder (C:\Users\me\Desktop\mp3player)")));
    }

    [TestCase(@"D:\Music\Player")]
    [TestCase(@"E:\", Description = "Root of a player drive")]
    [TestCase(@"F:\", Description = "Root of another drive")]
    [TestCase(@"\\server\share", Description = "Root of a network share")]
    [TestCase(@"\\server\share\Player")]
    public void No_tile_shows_the_same_text_twice(string rememberedFolder)
    {
        // TEXT-13 in docs/design-decisions.md
        var (options, _) = Players.BuildOptions(
            [Drive("E:\\", DriveType.Removable, StorageBus.Usb, label: "SANDISK"), Drive("G:\\", DriveType.Removable, StorageBus.SecureDigital)],
            new PlayerChoice(FolderPath: rememberedFolder), _ => true);

        Assert.Multiple(() =>
        {
            foreach (var option in options)
            {
                var texts = new[] { option.Title, option.Subtitle, option.Detail }.Where(t => t.Length > 0).ToList();
                Assert.That(texts, Is.Unique, $"Tile '{option.Title}'");
            }
        });
    }

    [TestCase(100L, 50L, ExpectedResult = false)]
    [TestCase(100L, 11L, ExpectedResult = false)]
    [TestCase(100L, 10L, ExpectedResult = true, Description = "90% used")]
    [TestCase(100L, 0L, ExpectedResult = true)]
    public bool Drive_is_nearly_full_from_90_percent_used(long total, long free)
    {
        var option = new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", Drive("E:\\", DriveType.Removable, StorageBus.Usb) with { TotalBytes = total, FreeBytes = free });
        Assert.That(option.UsedFraction, Is.EqualTo((total - free) / (double)total));
        return option.IsNearlyFull;
    }

    [Test]
    public void Usage_is_only_shown_for_drives_of_known_size()
    {
        var drive = Drive("E:\\", DriveType.Removable, StorageBus.Usb);
        Assert.That(new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", drive).ShowsUsage, Is.True);
        Assert.That(new PlayerOption(PlayerOptionKind.Drive, "Player", "", "", drive with { TotalBytes = 0 }).ShowsUsage, Is.False);
        Assert.That(new PlayerOption(PlayerOptionKind.Folder, "Player", "", "", FolderPath: @"D:\Music").ShowsUsage, Is.False);
    }

    [TestCase(@"C:\Users\me\Desktop\mp3player", ExpectedResult = @"C:\Users\me\Desktop\mp3player", Description = "Short enough")]
    [TestCase(@"C:\Users\me\AppData\Local\Temp\Mp3PlayerSyncE2E-22808\E2E Player", ExpectedResult = @"C:\…\Mp3PlayerSyncE2E-22808\E2E Player")]
    [TestCase(@"C:\Users\me\AppData\Local\Temp\A very long folder name for the player", ExpectedResult = @"C:\…\A very long folder name for the player", Description = "The last folder is always kept")]
    [TestCase(@"D:\A folder name that is far too long to fit", ExpectedResult = @"D:\A folder name that is far too long to fit", Description = "Nothing to leave out")]
    [TestCase(@"\\server\share\music\players\sandisk\clip sport", ExpectedResult = @"\\server\share\…\sandisk\clip sport")]
    public string ShortenPath_keeps_the_drive_and_the_last_folders(string path) => Players.ShortenPath(path);

    [Test]
    public void ShortDisplayName_shortens_only_folders()
    {
        var folder = Players.ForFolder(@"C:\Users\me\AppData\Local\Temp\Mp3PlayerSyncE2E-22808\E2E Player", []);
        Assert.That(folder.ShortDisplayName, Is.EqualTo(@"Folder (C:\…\Mp3PlayerSyncE2E-22808\E2E Player)"));
        var drive = new PlayerOption(PlayerOptionKind.Drive, "SANDISK", "", "", Drive(@"E:\", DriveType.Removable, StorageBus.Usb));
        Assert.That(drive.ShortDisplayName, Is.EqualTo(drive.DisplayName));
    }

    [Test]
    public void A_locked_card_says_so()
    {
        var (options, _) = Players.BuildOptions([Drive("E:\\", DriveType.Removable, StorageBus.SecureDigital) with { IsWriteProtected = true }], null, _ => false);
        Assert.That(options[0].Detail, Is.EqualTo("This card is locked"));
    }
}
