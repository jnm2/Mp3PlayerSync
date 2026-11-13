using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Mp3PlayerSync.Core;

namespace Mp3PlayerSync;

/// <summary>Shows the chosen MP3 player as "Player: [icon] name", e.g. SANDISK (E:) or Folder (D:\Music).</summary>
public sealed partial class PlayerDisplay : UserControl
{
    private PlayerOption? player;

    public PlayerDisplay()
    {
        InitializeComponent();
    }

    public PlayerOption? Player
    {
        get => player;
        set
        {
            player = value;
            GlyphRun.Text = value?.Glyph ?? "";
            Show();
        }
    }

    /// <summary>Whether to shorten a long folder path in the middle, with the full name in a tooltip.</summary>
    public bool ShortensPath
    {
        get;
        set
        {
            field = value;
            Show();
        }
    }

    private void Show()
    {
        var name = (ShortensPath ? player?.ShortDisplayName : player?.DisplayName) ?? "";
        NameRun.Text = name;
        AutomationProperties.SetName(Heading, player is null ? "" : "Player: " + player.DisplayName);
        ToolTipService.SetToolTip(Heading, player is not null && name != player.DisplayName ? player.DisplayName : null);
    }

    /// <summary>The automation ID of the heading text, whose name reads "Player: <name>", for UI automation.</summary>
    public string NameAutomationId
    {
        get => AutomationProperties.GetAutomationId(Heading);
        set => AutomationProperties.SetAutomationId(Heading, value);
    }
}