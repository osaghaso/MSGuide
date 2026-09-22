using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace MSGuide.Desktop;

internal static class CompanionModeTests
{
    internal static void Select(RadioButton option)
    {
        var peer = new RadioButtonAutomationPeer(option);
        IntegrationTests.Require(peer.GetAutomationControlType() == AutomationControlType.RadioButton
            && peer.GetName() == option.Content.ToString());
        var selection = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
        IntegrationTests.Require(selection is not null);
        selection!.Select();
    }

    internal static void Run()
    {
        var main = new MainWindow(new SpeechService(), new CompanionPosition());
        try { main.CheckExplicitModeChoices(); }
        finally { main.Close(); }
    }
}

public partial class MainWindow
{
    internal void CheckExplicitModeChoices()
    {
        cameraRecoverySensing = new CompactCameraTests.Sensing();
        loaded = true;
        try
        {
            var compact = companion.Prompt;
            PromptBox.Text = "Keep this draft while selecting a mode";
            UpdateCameraRecoveryUi();
            IntegrationTests.Require(compact.GuideModeOption.IsChecked == true
                && compact.FixModeOption.IsChecked == false);
            CompanionModeTests.Select(compact.FixModeOption);
            IntegrationTests.Require(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                && compact.FixModeOption.IsChecked == true && compact.GuideModeOption.IsChecked == false
                && PromptBox.Text == "Keep this draft while selecting a mode");
            cameraRecovery.Start(authorizeCameraOn: true);
            cameraRecovery.ChooseTeamsWindow("synthetic-mode-target", "Synthetic meeting");
            cameraRecovery.ApplyTeamsObservation(new("synthetic-mode-target", TeamsCameraFinding.CameraOff, "",
                new("synthetic-target", CameraRecoveryPinnedTargets.TeamsTurnCameraOn,
                    Kind: CameraRecoveryTargetKind.TeamsCameraButton)));
            UpdateCameraRecoveryUi();
            var active = cameraRecovery;
            int activeGeneration = cameraRecoveryGeneration;
            CompanionModeTests.Select(compact.FixModeOption);
            IntegrationTests.Require(ReferenceEquals(active, cameraRecovery)
                && active.CameraOnRequestAuthorized && active.Target is not null
                && activeGeneration == cameraRecoveryGeneration);
            IntegrationTests.Require(AutomationProperties.GetHelpText(compact.GuideModeOption).Contains("Stops the current task"));
            CompanionModeTests.Select(compact.GuideModeOption);
            IntegrationTests.Require(SelectedCameraMode == CameraRecoveryInteractionMode.Guide
                && compact.GuideModeOption.IsChecked == true && compact.FixModeOption.IsChecked == false
                && !active.CameraOnRequestAuthorized && active.Target is null
                && cameraRecovery.CanStart && !cameraRecovery.CameraOnRequestAuthorized
                && !cameraRecoveryBusy);
            foreach (var option in new[] { compact.GuideModeOption, compact.FixModeOption })
            {
                option.ApplyTemplate();
                option.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                IntegrationTests.Require(option.Focusable && option.IsTabStop && option.DesiredSize.Height >= 44
                    && option.Template.FindName("SelectedMark", option) is Path mark
                    && (mark.Visibility == Visibility.Visible) == (option.IsChecked == true)
                    && option.Content?.ToString() is "Guide me" or "Fix it for me");
            }
            cameraRecoverySensing = new PendingCameraRecoverySensing();
            UpdateCameraRecoveryUi();
            IntegrationTests.Require(!compact.FixModeOption.IsEnabled && compact.GuideModeOption.IsEnabled
                && AutomationProperties.GetHelpText(compact.FixModeOption).Contains("unavailable"));
            compact.FixModeOption.IsChecked = true;
            IntegrationTests.Require(SelectedCameraMode == CameraRecoveryInteractionMode.Guide
                && compact.GuideModeOption.IsChecked == true && compact.FixModeOption.IsChecked == false);
        }
        finally { loaded = false; }
    }
}
