using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mp3PlayerSync;

public sealed partial class LoadingIndicator : UserControl
{
    public LoadingIndicator()
    {
        InitializeComponent();

        // Only spin while shown.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Ring.IsActive = Visibility == Visibility.Visible);
    }

    public string Text
    {
        get => Label.Text;
        set => Label.Text = value;
    }

    /// <summary>The automation ID of the text, for UI automation.</summary>
    public string TextAutomationId
    {
        get => AutomationProperties.GetAutomationId(Label);
        set => AutomationProperties.SetAutomationId(Label, value);
    }
}
