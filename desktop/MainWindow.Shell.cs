using System.Windows;
using System.Windows.Automation;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private bool developerToolsEnabled;
    private bool sessionScreenContextApproved;
    private bool sessionAutomationApproved;
    private bool updatingCompanionPosition;

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ConfigureProductShell()
    {
        developerToolsEnabled = Environment.GetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS") == "1";
        sessionScreenContextApproved =
            Environment.GetEnvironmentVariable("MSGUIDE_SESSION_SCREEN_CONTEXT") == "1";
        sessionAutomationApproved =
            Environment.GetEnvironmentVariable("MSGUIDE_SESSION_AUTOMATION") == "1";
        DeveloperToolsExpander.Visibility = developerToolsEnabled ? Visibility.Visible : Visibility.Collapsed;
        companion.Position.Changed += UpdateCompanionPositionUi;
        UpdateCompanionPositionUi();
        ResetScreenContextUi();
    }

    private void UpdateCompanionPositionUi()
    {
        updatingCompanionPosition = true;
        try { FollowPointerSetting.IsChecked = companion.Position.FollowPointer; }
        finally { updatingCompanionPosition = false; }
        CompanionPositionText.Text = companion.Position.Description;
    }

    private void FollowPointerSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!updatingCompanionPosition && companion is not null)
            companion.SetFollowing(FollowPointerSetting.IsChecked == true);
    }

    internal static bool CanAutoCapture(bool sessionApproved, WindowChoice? window) =>
        sessionApproved && window is not null;

    private void ResetScreenContextUi()
    {
        UseScreenContextButton.Visibility = Visibility.Collapsed;
        ScreenContextExpander.IsExpanded = false;
        ScreenContextExpander.Visibility = developerToolsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VoiceSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = true;
        companion.Prompt.SetSettingsVisible(true);
        MicrophonePicker.BringIntoView();
        MicrophonePicker.Focus();
    }

    private void BackToQuestion_Click(object sender, RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = false;
        companion.Prompt.SetSettingsVisible(false);
        PromptBox.BringIntoView();
        PromptBox.Focus();
    }

    private void UseScreenContext_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            ShowPromptFeedback("Enter your question before choosing screen context.");
            return;
        }
        OpenScreenContext();
    }

    private void OpenScreenContext()
    {
        speech.Stop();
        CancelWork();
        ResetCameraRecovery();
        UpdateCameraRecoveryUi();
        ScreenContextExpander.Visibility = Visibility.Visible;
        ScreenContextExpander.IsExpanded = true;
        companion.ShowCameraTask();
        RefreshWindows();
        AnswerText.Text = "Choose a window, capture it locally, then review what you want to share.";
        StatusText.Text = "Choose screen context. Nothing has been captured or shared yet.";
        WindowPicker.BringIntoView();
        WindowPicker.Focus();
    }

    private void CloseScreenContext_Click(object sender, RoutedEventArgs e)
    {
        CancelWork();
        ResetScreenContextUi();
        StatusText.Text = "Screen context closed. No further data will be sent.";
        PromptBox.Focus();
    }

    private void SetConnectionStatus(string text)
    {
        ConnectionStatusText.Text = text;
        AutomationProperties.SetHelpText(ConnectionStatusText, text);
    }
}
