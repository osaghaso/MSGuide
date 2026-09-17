using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace MSGuide.Desktop;

internal static class PromptTests
{
    public static void Run()
    {
        if (!MainWindow.IsPromptSubmitKey(Key.Enter, ModifierKeys.None)
            || MainWindow.IsPromptSubmitKey(Key.Enter, ModifierKeys.Shift)
            || MainWindow.IsPromptSubmitKey(Key.Space, ModifierKeys.None))
            throw new InvalidOperationException("Prompt submission keyboard contract failed.");
        string? previous = Environment.GetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS");
        try
        {
            Environment.SetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS", "0");
            var window = new MainWindow();
            try { window.CheckPromptComposer(); }
            finally { window.Close(); }
            Environment.SetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS", "1");
            var developer = new MainWindow();
            try { developer.CheckDeveloperShell(); }
            finally { developer.Close(); }
        }
        finally { Environment.SetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS", previous); }
    }
}

public partial class MainWindow
{
    internal void CheckPromptComposer()
    {
        static void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Prompt composer regression failed.");
        }
        static bool Within(DependencyObject item, DependencyObject ancestor)
        {
            for (DependencyObject? current = item; current is not null; current = LogicalTreeHelper.GetParent(current))
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }
        loaded = true;
        try
        {
            var peer = new ButtonAutomationPeer(AskPromptButton);
            Check(peer.GetName() == "Ask MSGuide" && AskPromptButton.Focusable
                && !AskPromptButton.IsDefault && PromptBox.AcceptsReturn);
            Check(PromptBox.Text.Length == 0 && !AskPromptButton.IsEnabled
                && ProductSubtitle.Text == "Your guide to getting things done at Microsoft."
                && DeveloperToolsExpander.Visibility == Visibility.Collapsed
                && SettingsExpander.Visibility == Visibility.Visible
                && ScreenContextExpander.Visibility == Visibility.Collapsed
                && CameraRecoveryCard.Visibility == Visibility.Collapsed
                && InteractionModePanel.Visibility == Visibility.Visible
                && Within(PromptBox, WorkspaceScroll)
                && Within(CameraRecoveryCard, WorkspaceScroll)
                && Within(SettingsExpander, WorkspaceScroll));
            Check(CameraStatePanel.Visibility == Visibility.Collapsed
                && CameraProgressPanel.Visibility == Visibility.Collapsed
                && CameraIdleHint.Visibility == Visibility.Visible);
            PromptBox.Text = "";
            Check(!AskPromptButton.IsEnabled);
            PromptBox.Text = "Help me fix my camera in Teams";
            Check(AskPromptButton.IsEnabled && cameraRecovery.State == CameraRecoveryState.Idle
                && cameraRecovery.Target is null && !cameraRecoveryBusy);
            PromptBox.Text = "Explain a different application";
            AskPromptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(PromptFeedbackText.Visibility == Visibility.Visible
                && PromptFeedbackText.Text.Contains("don't have an automated fix", StringComparison.Ordinal)
                && UseScreenContextButton.Visibility == Visibility.Visible
                && ScreenContextExpander.Visibility == Visibility.Collapsed
                && cameraRecovery.State == CameraRecoveryState.Idle);
            UseScreenContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ScreenContextExpander.Visibility == Visibility.Visible && ScreenContextExpander.IsExpanded
                && ReviewPanel.Visibility == Visibility.Collapsed && snapshot is null
                && ConsentBox.IsChecked != true && ShareImage.IsChecked != true
                && !SendButton.IsEnabled && !speech.Listening);
            CloseScreenContext_Click(this, new RoutedEventArgs());
            Check(ScreenContextExpander.Visibility == Visibility.Collapsed && snapshot is null);
            VoiceSettings_Click(this, new RoutedEventArgs());
            Check(SettingsExpander.IsExpanded && !speech.Listening && !speech.Finishing);
            SettingsExpander.IsExpanded = false;
            cameraRecoverySensing = new FixtureCameraRecoverySensing();
            cameraRecovery.Start();
            UpdateCameraRecoveryUi();
            Check(!CameraGuideMode.IsEnabled && !CameraControlMode.IsEnabled
                && CameraSwitchModeButton.Visibility == Visibility.Visible
                && CameraRecoveryCard.Visibility == Visibility.Visible);
            CameraSwitchModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                && cameraRecovery.CanStart && cameraRecovery.Target is null
                && !cameraRecoveryBusy && CameraGuideMode.IsEnabled && CameraControlMode.IsEnabled);
            cameraRecovery.Start();
            PromptBox.Text = "A new request while an old task was active";
            Check(cameraRecovery.CanStart && CameraRecoveryCard.Visibility == Visibility.Collapsed
                && cameraRecovery.Target is null && CameraGuideMode.IsEnabled && CameraControlMode.IsEnabled);
            PromptBox.Text = "";
            ApplyTranscript("Check my Teams camera");
            Check(PromptBox.Text == "Check my Teams camera" && AskPromptButton.IsEnabled
                && cameraRecovery.CanStart && !speech.Listening);
            ApplyTranscript(new string('a', 4000));
            Check(PromptBox.Text.Length == PromptBox.MaxLength
                && PromptFeedbackText.Text.Contains("length limit", StringComparison.Ordinal));
        }
        finally { loaded = false; }
    }

    internal void CheckDeveloperShell()
    {
        if (DeveloperToolsExpander.Visibility != Visibility.Visible
            || ScreenContextExpander.Visibility != Visibility.Visible
            || !developerToolsEnabled
            || ConsentBox.IsChecked == true || ShareImage.IsChecked == true)
            throw new InvalidOperationException("Developer tooling must be explicit without granting capture/share consent.");
    }
}
