using System.Windows;
using System.Windows.Input;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private bool promptRequestActive;

    private void UpdatePromptSubmissionUi()
    {
        if (AskPromptButton is null || PromptBox is null) return;
        AskPromptButton.IsEnabled = !cameraRecoveryBusy && !speech.Listening && !speech.Finishing
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
        if (speech.Listening || speech.Finishing)
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
            ResetCameraRecovery();
            UpdateCameraRecoveryUi();
            UseScreenContextButton.Visibility = Visibility.Visible;
            StatusText.Text = "Additional context needed. No action has been taken.";
            ShowPromptFeedback("I don't have an automated fix for this request yet. Use screen context to choose a window and approve what is shared for guidance.");
            return;
        }

        promptRequestActive = true;
        await StartCameraRecoveryAsync(fromPrompt: true);
    }
}
