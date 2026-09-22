using System.Windows;
using System.Windows.Input;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private bool promptRequestActive;

    private void UpdatePromptSubmissionUi()
    {
        if (AskPromptButton is null || PromptBox is null) return;
        AskPromptButton.IsEnabled = !cameraRecoveryBusy && !speech.Busy
            && !string.IsNullOrWhiteSpace(PromptBox.Text);
        UseScreenContextButton.IsEnabled = AskPromptButton.IsEnabled;
        UpdateSpeechControls();
        if (promptRequestActive)
            ShowPromptFeedback(cameraRecoveryBusy
                ? cameraRecoveryNotice ?? "Checking the current camera state..."
                : cameraRecovery.Detail);
    }

    private void ShowPromptFeedback(string text)
    {
        PromptFeedbackText.Text = text;
        PromptFeedbackText.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(PromptFeedbackText, text);
        companion?.Prompt.SetFeedback(text);
    }

    internal static bool IsPromptSubmitKey(Key key, ModifierKeys modifiers) =>
        key == Key.Enter && modifiers == ModifierKeys.None;

    private async void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsPromptSubmitKey(e.Key, Keyboard.Modifiers)) return;
        e.Handled = true;
        await SubmitPromptAsync();
    }

    private async void AskPrompt_Click(object sender, RoutedEventArgs e) =>
        await SubmitPromptAsync();

    private async Task SubmitPromptAsync()
    {
        if (speech.Busy)
        {
            speech.FinishListening();
            ShowPromptFeedback("Finish dictation and review the transcript, then select Ask MSGuide. Nothing has been submitted.");
            return;
        }
        string prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            ShowPromptFeedback("Enter a question first.");
            return;
        }
        if (cameraRecoveryBusy)
        {
            ShowPromptFeedback("A camera check is in progress. Stop recovery before submitting another request.");
            return;
        }

        speech.Stop();
        CancelWork();
        ResetScreenContextUi();
        if (!CameraRecoverySession.IsCameraHelpIntent(prompt))
        {
            promptRequestActive = false;
            OpenScreenContext();
            if (CanAutoCapture(sessionScreenContextApproved, WindowPicker.SelectedItem as WindowChoice))
            {
                ShowPromptFeedback("Capturing the invoked app under this Copilot session grant.");
                await CaptureAndGuideAsync();
                return;
            }
            ShowPromptFeedback("Choose the app you were using, capture it, and approve the context. Copilot launch sessions can execute a bounded sequence of freshly grounded actions.");
            return;
        }

        promptRequestActive = true;
        await StartCameraRecoveryAsync(fromPrompt: true);
    }
}
