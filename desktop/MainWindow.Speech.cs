using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private bool replaceVoiceDraft;
    private bool applyingTranscript;

    private void UpdateSpeechControls()
    {
        if (MicButton is null || AddVoiceButton is null) return;
        bool hasDraft = !string.IsNullOrWhiteSpace(PromptBox.Text);
        var state = SpeechControlsState.From(speech, hasDraft, MicrophonePicker.SelectedItem is MicrophoneChoice);
        MicButton.Content = state.RecordLabel;
        System.Windows.Automation.AutomationProperties.SetName(MicButton, state.RecordName);
        MicButton.IsEnabled = state.CanRecord;
        AddVoiceButton.Visibility = state.ShowAppend
            ? Visibility.Visible : Visibility.Collapsed;
        AddVoiceButton.IsEnabled = state.CanAppend;
        MicrophoneStateText.Text = state.InputState;
        MicrophonePicker.IsEnabled = state.CanSelectInput;
        RefreshMicrophonesButton.IsEnabled = MicrophonePicker.IsEnabled;
        StopAudioButton.Content = state.StopLabel;
        System.Windows.Automation.AutomationProperties.SetName(StopAudioButton, state.StopName);
        SpeechInputPanel.Visibility = state.ShowLevel ? Visibility.Visible : Visibility.Collapsed;
        if (companion is not null)
        {
            companion.Prompt.Voice.Update(state, MicrophonePicker.Items.OfType<MicrophoneChoice>().ToArray(),
                MicrophonePicker.SelectedItem as MicrophoneChoice, SpeechText.Text,
                SpeechPreviewText.Text, SpeechInputLevel.Value);
            companion.Prompt.UpdateDraft(PromptBox.Text, AskPromptButton.IsEnabled);
        }
    }

    private void StartVoiceDraft(bool append)
    {
        if (speech.Busy)
        {
            ShowPromptFeedback("Stop the current recording or transcription before starting another.");
            return;
        }
        if (MicrophonePicker.SelectedItem is not MicrophoneChoice)
        {
            ShowPromptFeedback("Choose a microphone before starting dictation.");
            return;
        }
        CancelWork(cancelCameraRecovery: false);
        replaceVoiceDraft = !append;
        microphoneStarted = DateTimeOffset.UtcNow;
        speech.Toggle();
        if (speech.Listening)
            ShowPromptFeedback(append
                ? "Recording more for this question. Nothing will be submitted automatically."
                : "Recording a new question. The current draft stays until new words are recognized.");
    }

    private void AddVoice_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            ShowPromptFeedback("Record or type a question before adding more.");
            return;
        }
        StartVoiceDraft(append: true);
    }

    private void RefreshMicrophones()
    {
        try
        {
            var previous = MicrophonePicker.SelectedItem as MicrophoneChoice ?? speech.Input;
            var choices = MicrophoneChoice.Enumerate();
            MicrophonePicker.ItemsSource = choices;
            MicrophonePicker.SelectedItem = choices.FirstOrDefault(choice => choice == previous);
            MicButton.IsEnabled = !speech.Busy && MicrophonePicker.SelectedItem is MicrophoneChoice;
            if (!MicButton.IsEnabled)
            {
                SpeechText.Text = "The previously selected microphone is unavailable. Choose an input before recording.";
                System.Windows.Automation.AutomationProperties.SetHelpText(SpeechText, SpeechText.Text);
            }
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            SpeechText.Text = "Microphone devices could not be listed. Check Sound input settings and refresh.";
            System.Windows.Automation.AutomationProperties.SetHelpText(SpeechText, SpeechText.Text);
        }
        UpdateSpeechControls();
    }

    private void RefreshMicrophones_Click(object sender, RoutedEventArgs e)
    {
        if (speech.Busy)
        {
            ShowPromptFeedback("Stop dictation before refreshing microphone devices.");
            return;
        }
        RefreshMicrophones();
    }

    private void MicrophonePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || MicrophonePicker.SelectedItem is not MicrophoneChoice input) return;
        if (speech.Busy)
        {
            if (input != speech.Input) MicrophonePicker.SelectedItem = speech.Input;
            ShowPromptFeedback(speech.Status);
            UpdateSpeechControls();
            return;
        }
        speech.SelectInput(input);
        UpdateSpeechControls();
    }

    private void ApplyTranscript(string text)
    {
        if (closing) return;
        string combined = replaceVoiceDraft ? text.Trim() : (PromptBox.Text + " " + text).Trim();
        replaceVoiceDraft = false;
        bool truncated = combined.Length > PromptBox.MaxLength;
        applyingTranscript = true;
        try { PromptBox.Text = truncated ? combined[..PromptBox.MaxLength] : combined; }
        finally { applyingTranscript = false; }
        PromptBox.CaretIndex = PromptBox.Text.Length;
        if (truncated)
            ShowPromptFeedback("The question reached its length limit. Review and shorten it before adding more speech.");
    }

    private void MicrophoneSettings_Click(object sender, RoutedEventArgs e)
    {
        speech.Stop();
        if (speech.Busy)
        {
            ShowPromptFeedback("Wait for confirmed microphone closure before changing audio input settings.");
            return;
        }
        try
        {
            using var launched = Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
            SpeechText.Text = "In Sound settings, choose the microphone under Input and check its volume and mute switch. Then return and start dictation.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            SpeechText.Text = "Windows Sound settings could not be opened. Open Settings > System > Sound > Input manually.";
        }
        System.Windows.Automation.AutomationProperties.SetHelpText(SpeechText, SpeechText.Text);
    }
}
