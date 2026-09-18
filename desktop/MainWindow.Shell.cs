using System.Windows;
using System.Windows.Automation;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private bool developerToolsEnabled;
    private bool sessionScreenContextApproved;
    private bool sessionAutomationApproved;

    private void ConfigureProductShell()
    {
        developerToolsEnabled = Environment.GetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS") == "1";
        sessionScreenContextApproved =
            Environment.GetEnvironmentVariable("MSGUIDE_SESSION_SCREEN_CONTEXT") == "1";
        sessionAutomationApproved =
            Environment.GetEnvironmentVariable("MSGUIDE_SESSION_AUTOMATION") == "1";
        DeveloperToolsExpander.Visibility = developerToolsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ResetScreenContextUi();
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
        MicrophonePicker.BringIntoView();
        MicrophonePicker.Focus();
    }

    private void BackToQuestion_Click(object sender, RoutedEventArgs e)
    {
        SettingsExpander.IsExpanded = false;
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
