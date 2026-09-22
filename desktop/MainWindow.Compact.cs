using System.Windows;
using System.Windows.Controls;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private void ConfigureCompactWorkspace()
    {
        companion.Prompt.AttachWorkspace(CameraRecoveryCard, SettingsExpander,
            ScreenContextExpander, DeveloperToolsExpander, StatusText);
        CompanionPositionSettingsCard.Visibility = Visibility.Collapsed;
        companion.Prompt.Dismissed += () =>
        {
            if (closing) return;
            CancelCameraRecoveryForSupersession();
            speech.Stop();
        };
        UpdateCompactCameraUi();
    }

    private void ToggleCompactSettings()
    {
        SettingsExpander.IsExpanded = !SettingsExpander.IsExpanded;
        companion.Prompt.SetSettingsVisible(SettingsExpander.IsExpanded);
        if (SettingsExpander.IsExpanded) SettingsExpander.BringIntoView();
    }

    private void UpdateCompactCameraUi()
    {
        if (companion is null) return;
        bool visible = CameraRecoveryCard.Visibility == Visibility.Visible;
        companion.Prompt.UpdateCamera(visible);
    }
}
