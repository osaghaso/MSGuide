using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MSGuide.Desktop;

internal static class CameraCompletionTests
{
    internal static void Run()
    {
        foreach (var finding in new[]
        {
            CameraVerificationFinding.Ready, CameraVerificationFinding.AlreadyReady,
            CameraVerificationFinding.NeedsReinitialization, CameraVerificationFinding.Unresolved
        })
        foreach (bool fixture in new[] { false, true })
        {
            var main = new MainWindow(new SpeechService(), new CompanionPosition());
            try { main.CheckCameraCompletion(finding, fixture); }
            finally { main.Close(); }
        }
    }
}

public partial class MainWindow
{
    internal void CheckCameraCompletion(CameraVerificationFinding finding, bool fixture)
    {
        loaded = true;
        try
        {
            var sensing = new CompactCameraTests.Sensing();
            cameraRecoverySensing = sensing;
            PromptBox.Text = "Check my Teams camera";
            cameraRecovery = new CameraRecoverySession(CameraRecoveryInteractionMode.Guide);
            cameraRecovery.Start();
            cameraRecovery.ChooseTeamsWindow("synthetic-completion", "Synthetic meeting");
            cameraRecovery.ApplyTeamsObservation(new("synthetic-completion", TeamsCameraFinding.CameraOn, ""));
            UpdateCameraRecoveryUi();
            IntegrationTests.Require(!cameraRecovery.CanComplete && !CameraDoneButton.IsEnabled
                && CameraDoneButton.Visibility == Visibility.Collapsed);
            CameraDone_Click(this, new RoutedEventArgs());
            IntegrationTests.Require(cameraRecovery.State == CameraRecoveryState.NeedsLocalVerification);

            bool verified = finding is CameraVerificationFinding.Ready or CameraVerificationFinding.AlreadyReady;
            cameraRecovery.ApplyVerification(new("synthetic-completion", finding, verified, "Synthetic evidence.",
                IsFixture: fixture, Reinitialized: finding == CameraVerificationFinding.Ready));
            UpdateCameraRecoveryUi();
            IntegrationTests.Require(cameraRecovery.CanComplete == verified
                && CameraDoneButton.IsEnabled == verified
                && (CameraDoneButton.Visibility == Visibility.Visible) == verified);
            if (!verified)
            {
                IntegrationTests.Require(!CameraStateText.Text.StartsWith("Resolved", StringComparison.Ordinal));
                return;
            }
            IntegrationTests.Require(ReferenceEquals(CameraDoneButton.Style, FindResource("PrimaryButtonStyle"))
                && ReferenceEquals(CameraStartButton.Style, FindResource("QuietButtonStyle")));
            if (fixture)
                IntegrationTests.Require(CameraStateText.Text.StartsWith("Fixture complete", StringComparison.Ordinal)
                    && AutomationProperties.GetName(CameraDoneButton).Contains("simulated")
                    && !CameraStateText.Text.Contains("Resolved"));
            else
            {
                IntegrationTests.Require(CameraStateText.Text == "Resolved · camera ready"
                    && AutomationProperties.GetName(CameraDoneButton).Contains("resolved"));
                if (finding == CameraVerificationFinding.AlreadyReady)
                    IntegrationTests.Require(CameraStepText.Text.Contains("No change was needed."));
            }
            CameraDoneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(cameraRecovery.CanStart && !cameraRecovery.CanComplete
                && !cameraRecovery.CameraOnRequestAuthorized && cameraRecovery.Target is null
                && PromptBox.Text.Length == 0 && companion.Prompt.DraftControl.Text.Length == 0
                && CameraRecoveryCard.Visibility == Visibility.Collapsed
                && CameraDoneButton.Visibility == Visibility.Collapsed
                && sensing.Actions.Count == 0 && sensing.Restarts == 0
                && !speech.Busy && !companion.Prompt.SettingsVisible);
            CameraDone_Click(this, new RoutedEventArgs());
            IntegrationTests.Require(cameraRecovery.CanStart && sensing.Actions.Count == 0);
        }
        finally { loaded = false; }
    }
}
