using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private CameraRecoverySession cameraRecovery = new();
    private ICameraRecoverySensing cameraRecoverySensing = new PendingCameraRecoverySensing();
    private CancellationTokenSource? cameraRecoveryOperation;
    private int cameraRecoveryGeneration;
    private bool cameraRecoveryBusy;
    private bool refreshingCameraWindows;
    private bool cameraTargetPresentationAttempted;
    private bool cameraTargetShown;
    private string? cameraRecoveryNotice;
    private CameraRecoveryInteractionMode SelectedCameraMode =>
        CameraControlMode?.IsChecked == true
            ? CameraRecoveryInteractionMode.Control
            : CameraRecoveryInteractionMode.Guide;

    internal void UseCameraRecoverySensing(ICameraRecoverySensing sensing)
    {
        ArgumentNullException.ThrowIfNull(sensing);
        if (loaded || cameraRecovery.State != CameraRecoveryState.Idle)
            throw new InvalidOperationException("Camera recovery sensing must be connected before the window is loaded.");
        cameraRecoverySensing = sensing;
        UpdateCameraRecoveryUi();
    }

    private void InitializeCameraRecovery()
    {
        cameraRecoverySensing = Environment.GetEnvironmentVariable("MSGUIDE_CAMERA_RECOVERY_MODE") switch
        {
            "fixture" => new FixtureCameraRecoverySensing(),
            "disabled" => new PendingCameraRecoverySensing(),
            _ => new LiveCameraRecoverySensing(overlay)
        };
        UpdateCameraRecoveryUi();
    }

    private void RefreshCameraWindows(IReadOnlyList<WindowChoice> windows)
    {
        var selected = CameraWindowPicker.SelectedItem as WindowChoice;
        var teamsWindows = windows.Where(window => window.IsMicrosoftTeamsWindow).ToArray();
        refreshingCameraWindows = true;
        CameraWindowPicker.ItemsSource = teamsWindows;
        CameraWindowPicker.SelectedItem =
            teamsWindows.FirstOrDefault(window => window.Id == selected?.Id)
            ?? (teamsWindows.Length == 1 ? teamsWindows[0] : null);
        refreshingCameraWindows = false;
        CameraWindowHint.Text = teamsWindows.Length switch
        {
            0 => "No Teams window found. Open Teams, then refresh.",
            1 => "1 Teams window found and selected.",
            _ => $"{teamsWindows.Length} Teams windows found. Choose the one showing Settings > Devices."
        };
    }

    private async Task StartCameraRecoveryAsync(bool fromPrompt)
    {
        ResetCameraRecovery();
        cameraRecovery.Start();
        RefreshWindows();
        if (CameraWindowPicker.SelectedItem is WindowChoice selected)
            cameraRecovery.ChooseTeamsWindow(selected.Id, selected.Title);
        cameraRecoveryNotice = fromPrompt
            ? "Camera-help intent recognized locally from the shared editable prompt. Choose the exact Teams window."
            : null;
        StatusText.Text = "Camera recovery guide active · no screenshot, upload, click, or setting change started.";
        UpdateCameraRecoveryUi(fromPrompt ? null : CameraWindowPicker);
        await ObserveSelectedTeamsAsync();
    }

    private void ResetCameraRecovery()
    {
        CancelCameraOperation();
        cameraRecoverySensing.Reset();
        cameraRecovery.Reset(SelectedCameraMode);
        cameraTargetPresentationAttempted = false;
        cameraTargetShown = false;
        cameraRecoveryNotice = null;
    }

    private void CancelCameraRecoveryForSupersession()
    {
        CancelCameraOperation();
        if (cameraRecovery.State == CameraRecoveryState.Idle) return;
        cameraRecovery.Cancel("Stopped because another workflow or prompt replaced this camera recovery session.");
        cameraTargetPresentationAttempted = false;
        cameraTargetShown = false;
        cameraRecoveryNotice = null;
        UpdateCameraRecoveryUi();
    }

    private void CancelCameraOperation()
    {
        cameraRecoveryGeneration++;
        cameraRecoveryOperation?.Cancel();
        cameraRecoveryOperation?.Dispose();
        cameraRecoveryOperation = null;
        cameraRecoveryBusy = false;
    }

    private (CancellationToken Token, int Generation) BeginCameraOperation()
    {
        CancelCameraOperation();
        cameraRecoveryOperation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        cameraRecoveryBusy = true;
        UpdateCameraRecoveryUi();
        return (cameraRecoveryOperation.Token, cameraRecoveryGeneration);
    }

    private bool CurrentCameraOperation(int generation, CancellationToken cancellationToken) =>
        !closing && generation == cameraRecoveryGeneration && !cancellationToken.IsCancellationRequested;

    private void FinishCameraOperation(int generation)
    {
        if (generation != cameraRecoveryGeneration) return;
        cameraRecoveryOperation?.Dispose();
        cameraRecoveryOperation = null;
        cameraRecoveryBusy = false;
    }

    private void UpdateCameraRecoveryUi(FrameworkElement? focus = null)
    {
        if (CameraStateText is null) return;
        bool modeStateAllowsSelection = cameraRecovery.CanSelectMode;
        bool modeSelectionEnabled = !cameraRecoveryBusy && modeStateAllowsSelection;
        bool controlAvailable = cameraRecoverySensing is ICameraRecoveryControl;
        var mode = modeStateAllowsSelection ? SelectedCameraMode : cameraRecovery.Mode;
        if (!controlAvailable && mode == CameraRecoveryInteractionMode.Control)
        {
            CameraGuideMode.IsChecked = true;
            mode = CameraRecoveryInteractionMode.Guide;
        }
        bool controlMode = mode == CameraRecoveryInteractionMode.Control;
        CameraGuideMode.IsEnabled = modeSelectionEnabled;
        CameraControlMode.IsEnabled = modeSelectionEnabled && controlAvailable;
        CameraSwitchModeButton.Visibility = !modeSelectionEnabled && controlAvailable
            ? Visibility.Visible : Visibility.Collapsed;
        CameraSwitchModeButton.Content = controlMode
            ? "Stop & switch to Guide me" : "Stop & switch to Fix it for me";
        AutomationProperties.SetName(CameraSwitchModeButton, controlMode
            ? "Stop recovery, clear approval, and switch to Guide me"
            : "Stop recovery, clear approval, and switch to Fix it for me");
        CameraModePillText.Text = controlMode ? "FIX IT FOR ME" : "GUIDE ME";
        CameraModePill.SetResourceReference(Border.BackgroundProperty,
            controlMode ? "WarningSoftBrush" : "AccentSoftBrush");
        CameraModePillText.SetResourceReference(TextBlock.ForegroundProperty,
            controlMode ? "WarningBrush" : "AccentBrush");
        CameraModeSubtitle.Text = controlMode
            ? "MSGuide can act only on a freshly verified camera control after approval."
            : "You make every click. MSGuide verifies each next step.";
        CameraActionPolicyText.Text = controlMode ? "Approved actions only" : "No automatic clicks";
        string lockedModeHelp = controlAvailable
            ? "Mode is locked while recovery is active. Use Stop and switch to change mode; previous approval will be cleared."
            : "Mode is locked while recovery is active. Stop recovery to choose another mode.";
        CameraModeHint.Text = modeSelectionEnabled
            ? controlMode
                ? sessionAutomationApproved
                    ? "Fix mode uses the launch grant for bounded screen tasks. Camera changes still require separate approval."
                    : "MSGuide asks for approval before making a supported change."
                : "You make changes; MSGuide guides you and checks results."
            : lockedModeHelp;
        string guideModeHelp = modeSelectionEnabled
            ? "Choose Guide me so you perform each action and MSGuide verifies it."
            : lockedModeHelp;
        string controlModeHelp = !modeSelectionEnabled
            ? lockedModeHelp
            : controlAvailable
                ? "Choose Fix it for me to approve one freshly verified camera action."
                : "Fix it for me is unavailable because verified local camera control is not connected.";
        AutomationProperties.SetHelpText(CameraGuideMode, guideModeHelp);
        AutomationProperties.SetHelpText(CameraControlMode, controlModeHelp);
        CameraGuideMode.ToolTip = guideModeHelp;
        CameraControlMode.ToolTip = controlModeHelp;
        CameraStateText.Text = cameraRecoveryBusy && cameraRecovery.CanStart
            ? "Checking current camera"
            : CameraStateLabel(cameraRecovery.State);
        AutomationProperties.SetHelpText(CameraStateText, CameraStateText.Text);
        CameraSensingText.Text = cameraRecoverySensing.Mode switch
        {
            CameraRecoverySensingMode.Fixture => "Fixture mode",
            CameraRecoverySensingMode.Connected => "Local sensing",
            _ => "Sensing unavailable"
        };
        CameraSensingText.ToolTip = cameraRecoverySensing.Mode switch
        {
            CameraRecoverySensingMode.Fixture => CameraRecoveryPinnedTargets.FixturePreparation,
            CameraRecoverySensingMode.Connected => "Connected local provider. Each observation method is disclosed before use.",
            _ => "Unsupported fallback. No screenshot is taken."
        };
        CameraStepText.Text = cameraRecoveryNotice ?? cameraRecovery.Detail;
        AutomationProperties.SetHelpText(CameraStepText, CameraStepText.Text);
        bool idle = cameraRecovery.CanStart && !cameraRecoveryBusy;
        CameraRecoveryCard.Visibility = idle && !developerToolsEnabled ? Visibility.Collapsed : Visibility.Visible;
        CameraIdleHint.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        CameraIdleHint.Text = cameraRecoveryNotice
            ?? "Choose a mode, then ask your question above or start camera recovery below.";
        CameraStatePanel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        CameraProgressPanel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        CameraStartButton.Content = cameraRecovery.CanStart
            ? "Start camera recovery" : "Start over";
        AutomationProperties.SetName(CameraStartButton, cameraRecovery.CanStart
            ? "Start Teams camera recovery"
            : "Reset camera recovery and choose a mode");
        CameraStartButton.IsEnabled = !cameraRecoveryBusy;
        CameraChooseWindowButton.Content = CameraWindowPicker.HasItems
            ? "Choose Teams window" : "Refresh Teams windows";
        AutomationProperties.SetName(CameraChooseWindowButton, CameraWindowPicker.HasItems
            ? "Choose the exact Microsoft Teams window"
            : "Refresh the list of visible Microsoft Teams windows");
        CameraChooseWindowButton.IsEnabled = !cameraRecoveryBusy;
        CameraWindowPicker.IsEnabled = !cameraRecoveryBusy && CameraWindowPicker.HasItems;
        bool selectedWindowValid = CameraWindowPicker.SelectedItem is WindowChoice selectedWindow
            && selectedWindow.Matches();
        CameraInspectButton.IsEnabled = !cameraRecoveryBusy
            && cameraRecovery.CanInspectTeams
            && selectedWindowValid;
        CameraPrivateCheckButton.IsEnabled = !cameraRecoveryBusy
            && (cameraRecovery.CanInspectSettings
                || cameraRecovery.CanVerifyTeams && selectedWindowValid);
        CameraPrivateCheckButton.Content = cameraRecovery.CanInspectSettings
            ? "Inspect Camera Settings"
            : "Private visual check";
        AutomationProperties.SetName(CameraPrivateCheckButton, cameraRecovery.CanInspectSettings
            ? "Inspect Camera Settings controls without capturing pixels"
            : "Run the local Teams camera readiness verifier");
        CameraOpenSettingsButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanOpenSettings;
        CameraShowButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanShowTarget;
        CameraChangedCheckButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanCheckChangedSetting;
        CameraChangedCheckButton.Content =
            cameraRecovery.Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton
                ? "I turned it on · check"
                : "I changed it · check again";
        AutomationProperties.SetName(CameraChangedCheckButton,
            cameraRecovery.Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton
                ? "Check the Teams camera control after my change"
                : "Check Camera Settings after my change");
        CameraControlTargetButton.IsEnabled = !cameraRecoveryBusy
            && cameraRecovery.CanControlTarget
            && cameraRecoverySensing is ICameraRecoveryControl;
        CameraControlTargetButton.Content = cameraRecovery.Target?.Kind switch
        {
            CameraRecoveryTargetKind.TeamsCameraButton => "Approve & turn camera on",
            CameraRecoveryTargetKind.DeviceCameraPermission => "Approve & enable Camera access",
            CameraRecoveryTargetKind.AppCameraPermission => "Approve & allow camera access for apps",
            _ => "Approve & allow Teams camera"
        };
        AutomationProperties.SetName(CameraControlTargetButton, cameraRecovery.Target?.Kind switch
        {
            CameraRecoveryTargetKind.TeamsCameraButton =>
                "Approve one invocation of the verified Teams camera-on control",
            CameraRecoveryTargetKind.DeviceCameraPermission =>
                "Approve enabling device-wide Camera access for all already permitted apps",
            CameraRecoveryTargetKind.AppCameraPermission =>
                "Approve enabling camera access for permitted apps for this user",
            _ => "Approve one toggle of the verified Microsoft Teams camera permission"
        });
        CameraReturnButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanReturnToTeams;
        CameraRestartTeamsButton.IsEnabled = !cameraRecoveryBusy
            && cameraRecovery.CanRestartTeams
            && selectedWindowValid
            && cameraRecoverySensing is ICameraRecoveryControl;
        bool canStop = cameraRecoveryBusy || !cameraRecovery.IsTerminal
            && cameraRecovery.State is not (CameraRecoveryState.Idle or CameraRecoveryState.Cancelled);
        CameraStopButton.IsEnabled = canStop;
        CameraTakeOverButton.IsEnabled = canStop;

        bool showRestart = !cameraRecoveryBusy
            && (cameraRecovery.State == CameraRecoveryState.Idle || cameraRecovery.IsTerminal);
        CameraStartButton.Visibility = showRestart ? Visibility.Visible : Visibility.Collapsed;
        CameraSelectionPanel.Visibility = cameraRecovery.State == CameraRecoveryState.Idle
                || cameraRecovery.IsTerminal
            ? Visibility.Collapsed : Visibility.Visible;
        CameraChooseWindowButton.Visibility = !cameraRecoveryBusy
                && cameraRecovery.State is (
                    CameraRecoveryState.NeedsTeamsObservation
                    or CameraRecoveryState.NeedsCameraReinitialization)
                && !selectedWindowValid
            ? Visibility.Visible : Visibility.Collapsed;
        CameraInspectButton.Visibility = CameraInspectButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraOpenSettingsButton.Visibility = CameraOpenSettingsButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraPrivateCheckButton.Visibility = CameraPrivateCheckButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        CameraShowButton.Content = cameraTargetPresentationAttempted ? "Try showing again" : "Show me";
        CameraShowButton.Style = (Style)FindResource(cameraTargetPresentationAttempted
            ? "QuietButtonStyle" : "PrimaryButtonStyle");
        CameraShowButton.Visibility = CameraShowButton.IsEnabled && !cameraTargetShown
            ? Visibility.Visible : Visibility.Collapsed;
        CameraChangedCheckButton.Visibility = CameraChangedCheckButton.IsEnabled
                && (cameraTargetShown || cameraTargetPresentationAttempted)
            ? Visibility.Visible : Visibility.Collapsed;
        CameraControlTargetButton.Visibility = CameraControlTargetButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        CameraReturnButton.Visibility = CameraReturnButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        CameraRestartTeamsButton.Visibility = CameraRestartTeamsButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        CameraSessionActions.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;

        int progress = CameraProgress(cameraRecovery.State);
        CameraProgressBar.IsIndeterminate = cameraRecoveryBusy;
        CameraProgressBar.Value = progress;
        CameraProgressText.Text = cameraRecoveryBusy
            ? "Checking…"
            : cameraRecovery.State == CameraRecoveryState.Idle
                ? "Ready when you are"
                : cameraRecovery.State == CameraRecoveryState.Ready
                    ? "Complete"
                    : cameraRecovery.State == CameraRecoveryState.FixtureComplete
                        ? "Fixture run complete"
                    : cameraRecovery.IsTerminal
                        ? "Needs attention"
                        : $"{progress} of 7";
        AutomationProperties.SetName(CameraProgressBar,
            cameraRecoveryBusy ? "Camera recovery check in progress" : $"Camera recovery step {progress} of 7");
        UpdateCameraStateAppearance(progress);
        UpdatePromptSubmissionUi();
        focus?.Focus();
    }

    private static string CameraStateLabel(CameraRecoveryState state) => state switch
    {
        CameraRecoveryState.Idle => "Ready to help",
        CameraRecoveryState.NeedsTeamsObservation => "Choose and inspect Teams",
        CameraRecoveryState.Diagnosis => "Camera permission may be blocked",
        CameraRecoveryState.NeedsCameraSettings => "Open Camera settings",
        CameraRecoveryState.NeedsSettingsObservation => "Check the Camera privacy page",
        CameraRecoveryState.VerifiedTarget => "Camera control found",
        CameraRecoveryState.PermissionObservedOn => "Permission is on · verify Teams next",
        CameraRecoveryState.NeedsLocalVerification => "Check the Teams camera",
        CameraRecoveryState.NeedsCameraReinitialization => "Reinitialize the Teams camera",
        CameraRecoveryState.Ready => "Camera ready",
        CameraRecoveryState.FixtureComplete => "Fixture complete · not a real readiness claim",
        CameraRecoveryState.WrongSettingsPage => "Camera settings page not found",
        CameraRecoveryState.ManagedOrDisabled => "Camera access is managed or disabled",
        CameraRecoveryState.AlreadyOnOrWrongCause => "Permission is not the cause",
        CameraRecoveryState.StaleOrMoved => "Selected window changed",
        CameraRecoveryState.UnresolvedAfterPermission => "Camera still needs attention",
        CameraRecoveryState.Unsupported => "This screen cannot be checked safely",
        CameraRecoveryState.Cancelled => "Recovery stopped",
        _ => "Recovery stopped"
    };

    private static int CameraProgress(CameraRecoveryState state) => state switch
    {
        CameraRecoveryState.Idle => 0,
        CameraRecoveryState.NeedsTeamsObservation or CameraRecoveryState.StaleOrMoved
            or CameraRecoveryState.Unsupported or CameraRecoveryState.Cancelled => 1,
        CameraRecoveryState.Diagnosis or CameraRecoveryState.NeedsCameraSettings
            or CameraRecoveryState.AlreadyOnOrWrongCause or CameraRecoveryState.ManagedOrDisabled => 2,
        CameraRecoveryState.NeedsSettingsObservation or CameraRecoveryState.WrongSettingsPage => 3,
        CameraRecoveryState.VerifiedTarget => 4,
        CameraRecoveryState.PermissionObservedOn => 5,
        CameraRecoveryState.NeedsLocalVerification or CameraRecoveryState.NeedsCameraReinitialization
            or CameraRecoveryState.UnresolvedAfterPermission => 6,
        CameraRecoveryState.Ready or CameraRecoveryState.FixtureComplete => 7,
        _ => 0
    };

    private void UpdateCameraStateAppearance(int progress)
    {
        bool success = cameraRecovery.State == CameraRecoveryState.Ready;
        bool fixture = cameraRecovery.State == CameraRecoveryState.FixtureComplete;
        bool terminalProblem = cameraRecovery.IsTerminal
            && cameraRecovery.State is not (CameraRecoveryState.Ready or CameraRecoveryState.FixtureComplete);
        bool warning = cameraRecovery.State is CameraRecoveryState.PermissionObservedOn
            or CameraRecoveryState.NeedsCameraReinitialization or CameraRecoveryState.FixtureComplete;

        string background = success ? "SuccessSoftBrush"
            : terminalProblem ? "DangerSoftBrush"
            : warning ? "WarningSoftBrush"
            : "SurfaceRaisedBrush";
        string foreground = success ? "SuccessBrush"
            : terminalProblem ? "DangerBrush"
            : warning ? "WarningBrush"
            : "AccentBrush";
        CameraStatePanel.Background = (Brush)FindResource(background);
        CameraStatePanel.BorderBrush = (Brush)FindResource(foreground);
        CameraStateGlyph.Foreground = (Brush)FindResource(foreground);
        CameraStateGlyph.Text = success ? "✓" : fixture ? "◇" : terminalProblem ? "!"
            : progress == 0 ? "○" : progress.ToString();
    }

    private async Task ObserveCameraSettings()
    {
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Verifying the Camera privacy page and observing its controls locally…";
        UpdateCameraRecoveryUi();
        try
        {
            var observation = await cameraRecoverySensing.ObserveSettingsAsync(token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            cameraRecovery.ApplySettingsObservation(observation);
            cameraTargetPresentationAttempted = false;
            cameraTargetShown = false;
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Camera Settings inspection was cancelled or timed out. No setting was changed.";
        }
        catch (IncompleteAutomationReadException ex)
        {
            if (generation == cameraRecoveryGeneration)
            {
                cameraRecoveryNotice = null;
                cameraRecovery.MarkUnsupported(ex.Message);
            }
        }
        catch (Exception)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecovery.MarkUnsupported("Camera Settings could not be inspected safely. No setting was changed.");
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
    }

    private FrameworkElement? CameraFocusForState() => cameraRecovery.State switch
    {
        CameraRecoveryState.NeedsTeamsObservation when CameraWindowPicker.SelectedItem is null => CameraWindowPicker,
        CameraRecoveryState.NeedsTeamsObservation => CameraInspectButton,
        CameraRecoveryState.Diagnosis or CameraRecoveryState.NeedsCameraSettings => CameraOpenSettingsButton,
        CameraRecoveryState.NeedsSettingsObservation => CameraPrivateCheckButton,
        CameraRecoveryState.VerifiedTarget when cameraRecovery.CanControlTarget
            => CameraControlTargetButton,
        CameraRecoveryState.VerifiedTarget => CameraShowButton,
        CameraRecoveryState.PermissionObservedOn => CameraReturnButton,
        CameraRecoveryState.NeedsCameraReinitialization
            when CameraWindowPicker.SelectedItem is not WindowChoice rebound
                || !rebound.Matches()
            => CameraChooseWindowButton,
        CameraRecoveryState.NeedsCameraReinitialization when cameraRecovery.CanRestartTeams
            => CameraRestartTeamsButton,
        CameraRecoveryState.NeedsLocalVerification or CameraRecoveryState.NeedsCameraReinitialization
            => CameraPrivateCheckButton,
        _ => CameraStartButton
    };

    private void CameraMode_Changed(object sender, RoutedEventArgs e)
    {
        if (CameraModeHint is null || cameraRecoveryBusy || !cameraRecovery.CanSelectMode)
            return;
        if (loaded && screenTask?.Running == true) CancelWork(cancelCameraRecovery: false);
        bool endedRun = !cameraRecovery.CanStart;
        ResetCameraRecovery();
        if (loaded && endedRun)
            StatusText.Text = "Camera recovery reset for the selected mode · start when ready.";
        UpdateCameraRecoveryUi();
        UpdateScreenActionUi();
    }

    private void CameraSwitchMode_Click(object sender, RoutedEventArgs e)
    {
        bool switchToControl = SelectedCameraMode == CameraRecoveryInteractionMode.Guide;
        if (switchToControl && cameraRecoverySensing is not ICameraRecoveryControl)
        {
            StatusText.Text = "Fix it for me is unavailable because verified local control is not connected.";
            return;
        }
        promptRequestActive = false;
        CancelWork();
        ResetCameraRecovery();
        var selectedMode = switchToControl ? CameraControlMode : CameraGuideMode;
        selectedMode.IsChecked = true;
        string modeName = switchToControl ? "Fix it for me" : "Guide me";
        StatusText.Text = $"Switched to {modeName}. Previous approval was cleared; completed changes are not undone.";
        ShowPromptFeedback($"Selected {modeName}. Ask MSGuide to continue with a fresh check.");
        UpdateCameraRecoveryUi(selectedMode);
    }

    private async void CameraStart_Click(object sender, RoutedEventArgs e)
    {
        bool resetOnly = !cameraRecovery.CanStart;
        CancelWork();
        if (resetOnly)
        {
            ResetCameraRecovery();
            StatusText.Text = "Camera recovery reset · choose a mode, then start when ready.";
            UpdateCameraRecoveryUi(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                ? CameraControlMode : CameraGuideMode);
            await ReassessSelectedCameraAsync();
            return;
        }
        await StartCameraRecoveryAsync(fromPrompt: false);
    }

    private async Task ReassessSelectedCameraAsync()
    {
        if (CameraWindowPicker.SelectedItem is not WindowChoice selected || !selected.Matches())
        {
            cameraRecoveryNotice = "Choose a mode, then start recovery to select a current Teams window.";
            UpdateCameraRecoveryUi();
            return;
        }
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Checking the selected Teams camera's current state. No action is authorized.";
        UpdateCameraRecoveryUi();
        try
        {
            var assessment = await CameraRecoverySession.AssessCurrentAsync(
                cameraRecoverySensing, selected, SelectedCameraMode, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecovery.ApplyReadOnlyAssessment(assessment);
            cameraRecoveryNotice = cameraRecovery.LocalVerifierPassed ? null : cameraRecovery.Detail;
            StatusText.Text = cameraRecovery.LocalVerifierPassed
                ? cameraRecovery.Detail
                : "Current camera state checked. Choose a mode to continue; no action was taken.";
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Current camera check was cancelled or timed out. No action was taken.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = ex is IncompleteAutomationReadException
                    ? ex.Message
                    : "The current camera state could not be established. Start recovery to inspect again; no action was taken.";
        }
        finally
        {
            if (generation == cameraRecoveryGeneration)
            {
                FinishCameraOperation(generation);
                UpdateCameraRecoveryUi(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                    ? CameraControlMode : CameraGuideMode);
            }
        }
    }

    private void CameraChooseWindow_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        if (!CameraWindowPicker.HasItems)
        {
            cameraRecoveryNotice = "No Microsoft Teams window is visible yet. Open Teams, then refresh.";
            StatusText.Text = "Waiting for a visible Microsoft Teams window.";
            UpdateCameraRecoveryUi(CameraChooseWindowButton);
            return;
        }
        CameraWindowPicker.Focus();
        CameraWindowPicker.IsDropDownOpen = true;
    }

    private async void CameraWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || refreshingCameraWindows) return;
        CancelCameraOperation();
        cameraRecoveryNotice = null;
        cameraTargetPresentationAttempted = false;
        cameraTargetShown = false;
        if (CameraWindowPicker.SelectedItem is WindowChoice selected)
            cameraRecovery.ChooseTeamsWindow(selected.Id, selected.Title);
        else
            cameraRecovery.ChooseTeamsWindow("", "");
        StatusText.Text = "Camera recovery target changed · previous camera observations were discarded.";
        UpdateCameraRecoveryUi(CameraInspectButton);
        if (cameraRecovery.CanVerifyTeams)
            await VerifySelectedTeamsAsync();
        else
            await ObserveSelectedTeamsAsync();
    }

    private async void CameraInspect_Click(object sender, RoutedEventArgs e) =>
        await ObserveSelectedTeamsAsync();

    private async Task ObserveSelectedTeamsAsync()
    {
        bool checkingChangedCamera = cameraRecovery.CanCheckChangedSetting
            && cameraRecovery.Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton;
        if ((!cameraRecovery.CanInspectTeams && !checkingChangedCamera)
            || CameraWindowPicker.SelectedItem is not WindowChoice selected)
            return;
        if (!selected.Matches())
        {
            cameraRecovery.MarkStale("The selected Teams window moved, closed, or changed. Choose it again.");
            UpdateCameraRecoveryUi(CameraStartButton);
            return;
        }

        bool verifyCurrentCamera = false;
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Inspecting Teams controls locally…";
        UpdateCameraRecoveryUi();
        try
        {
            var observation = await cameraRecoverySensing.ObserveTeamsAsync(selected, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            cameraRecovery.ApplyTeamsObservation(observation);
            verifyCurrentCamera = cameraRecovery.CanVerifyTeams;
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Teams controls inspection was cancelled or timed out. No screenshot was taken.";
        }
        catch (IncompleteAutomationReadException ex)
        {
            if (generation == cameraRecoveryGeneration)
            {
                cameraRecoveryNotice = null;
                cameraRecovery.MarkUnsupported(ex.Message);
            }
        }
        catch (Exception)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecovery.MarkUnsupported("Teams controls could not be inspected safely. No screenshot was taken.");
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
        if (verifyCurrentCamera && CurrentCameraOperation(generation, token))
            await VerifySelectedTeamsAsync();
    }

    private void CameraOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            cameraRecoveryNotice = null;
            cameraRecovery.PrepareToOpenSettings();
            UpdateCameraRecoveryUi();
            using var launched = Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam")
            {
                UseShellExecute = true
            });
            cameraRecovery.MarkSettingsOpened();
            StatusText.Text = "Windows Settings launch requested by your click · Camera page not yet verified · MSGuide changed nothing.";
            UpdateCameraRecoveryUi(CameraPrivateCheckButton);
        }
        catch
        {
            cameraRecovery.MarkUnsupported(
                "Windows could not open ms-settings:privacy-webcam. No alternate command or setting change was attempted.");
            UpdateCameraRecoveryUi(CameraStartButton);
        }
    }

    private async void CameraShow_Click(object sender, RoutedEventArgs e)
    {
        if (!cameraRecovery.CanShowTarget || cameraRecovery.Target is not { } target) return;
        cameraTargetPresentationAttempted = true;
        cameraTargetShown = false;
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Showing only the current verified target…";
        UpdateCameraRecoveryUi();
        try
        {
            var presentation = await cameraRecoverySensing.ShowTargetAsync(target, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            cameraRecovery.RecordTargetPresentation(presentation);
            cameraTargetShown = presentation.Shown;
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Target presentation was cancelled or timed out. No click was attempted.";
        }
        catch (Exception)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "The verified target could not be shown. No click was attempted.";
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
    }

    private async void CameraChangedCheck_Click(object sender, RoutedEventArgs e)
    {
        if (cameraRecovery.Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton)
            await ObserveSelectedTeamsAsync();
        else
            await ObserveCameraSettings();
    }

    private void CameraReturn_Click(object sender, RoutedEventArgs e) =>
        ReturnToSelectedTeams("Return to Teams requested by your click · no Teams control was clicked.");

    private bool ReturnToSelectedTeams(string status)
    {
        if (!cameraRecovery.CanReturnToTeams || CameraWindowPicker.SelectedItem is not WindowChoice selected)
            return false;
        if (selected.Id != cameraRecovery.TeamsWindowId || !selected.Matches())
        {
            cameraRecovery.MarkStale("The selected Teams window moved, closed, or changed. Choose it again.");
            UpdateCameraRecoveryUi(CameraStartButton);
            return false;
        }
        cameraRecoveryNotice = null;
        cameraRecovery.MarkReturnedToTeams();
        try { AutomationElement.FromHandle(selected.Handle).SetFocus(); }
        catch
        {
            cameraRecoveryNotice =
                "Use the taskbar to return to the selected Teams window, then come back and run Private visual check.";
        }
        StatusText.Text = status;
        UpdateCameraRecoveryUi(CameraFocusForState());
        return true;
    }

    private async void CameraPrivateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (cameraRecovery.CanInspectSettings)
        {
            await ObserveCameraSettings();
            return;
        }
        await VerifySelectedTeamsAsync();
    }

    private async Task VerifySelectedTeamsAsync()
    {
        if (!cameraRecovery.CanVerifyTeams || CameraWindowPicker.SelectedItem is not WindowChoice selected) return;
        if (selected.Id != cameraRecovery.TeamsWindowId || !selected.Matches())
        {
            cameraRecovery.MarkStale("The selected Teams window moved, closed, or changed before verification.");
            UpdateCameraRecoveryUi(CameraStartButton);
            return;
        }
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Running the local Teams camera readiness verifier…";
        UpdateCameraRecoveryUi();
        try
        {
            var result = await cameraRecoverySensing.VerifyTeamsAsync(selected, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            cameraRecovery.ApplyVerification(result);
            StatusText.Text = cameraRecovery.State == CameraRecoveryState.FixtureComplete
                ? "Fixture complete · simulated local verifier passed; real camera-ready was not claimed."
                : cameraRecovery.State == CameraRecoveryState.Ready
                ? cameraRecovery.Detail
                : "Camera not verified ready · see the explicit recovery state.";
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Local Teams verification was cancelled or timed out. Camera-ready was not claimed.";
        }
        catch (IncompleteAutomationReadException ex)
        {
            if (generation == cameraRecoveryGeneration)
            {
                cameraRecoveryNotice = null;
                cameraRecovery.MarkUnsupported(ex.Message);
            }
        }
        catch (Exception)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecovery.MarkUnsupported("The local Teams verifier failed safely. Camera-ready was not claimed.");
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
    }

    private async void CameraControlTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!cameraRecovery.CanControlTarget
            || cameraRecovery.Target is not { } approvedTarget
            || cameraRecoverySensing is not ICameraRecoveryControl controller)
            return;
        if (approvedTarget.Kind == CameraRecoveryTargetKind.TeamsCameraButton
            && CameraWindowPicker.SelectedItem is not WindowChoice)
            return;

        bool verifyAfter = false;
        bool returnBeforeVerify = false;
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = approvedTarget.Kind == CameraRecoveryTargetKind.TeamsCameraButton
            ? "Applying the one approved Teams camera-on action…"
            : "Applying only the separately approved camera permission change…";
        StatusText.Text = "One exact camera action approved · no retries are authorized.";
        UpdateCameraRecoveryUi();
        try
        {
            var control = await controller.ActivateTargetAsync(approvedTarget, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            if (!control.OutcomeKnown)
            {
                cameraRecovery.RecordControlFailure(control.Detail);
            }
            else if (!control.Invoked)
            {
                cameraRecovery.MarkStale(control.Detail);
            }
            else
            {
                await Task.Delay(300, token);
                if (approvedTarget.Kind == CameraRecoveryTargetKind.TeamsCameraButton
                    && CameraWindowPicker.SelectedItem is WindowChoice selected)
                {
                    TeamsCameraObservation observation = await cameraRecoverySensing
                        .ObserveTeamsAsync(selected, token);
                    for (int attempt = 0;
                        attempt < 5 && observation.Finding == TeamsCameraFinding.CameraOff;
                        attempt++)
                    {
                        await Task.Delay(250, token);
                        observation = await cameraRecoverySensing
                            .ObserveTeamsAsync(selected, token);
                    }
                    if (!CurrentCameraOperation(generation, token)) return;
                    cameraRecovery.ApplyTeamsObservation(observation);
                    if (cameraRecovery.State == CameraRecoveryState.VerifiedTarget)
                        cameraRecovery.RecordControlFailure(
                            "The Teams camera control still appears off after the approved action. No retry occurred.");
                    verifyAfter = cameraRecovery.State == CameraRecoveryState.NeedsLocalVerification;
                }
                else
                {
                    CameraSettingsObservation observation = await cameraRecoverySensing
                        .ObserveSettingsAsync(token);
                    for (int attempt = 0;
                        attempt < 5 && observation.Finding == CameraSettingsFinding.PermissionOff
                            && observation.Target?.Kind == approvedTarget.Kind;
                        attempt++)
                    {
                        await Task.Delay(250, token);
                        observation = await cameraRecoverySensing.ObserveSettingsAsync(token);
                    }
                    if (!CurrentCameraOperation(generation, token)) return;
                    cameraRecovery.ApplySettingsObservation(observation);
                    cameraTargetPresentationAttempted = false;
                    cameraTargetShown = false;
                    overlay.Hide();
                    if (cameraRecovery.State == CameraRecoveryState.VerifiedTarget
                        && cameraRecovery.Target?.Kind == approvedTarget.Kind)
                        cameraRecovery.RecordControlFailure(
                            "The approved camera permission still appears off. No retry occurred.");
                    returnBeforeVerify =
                        cameraRecovery.State == CameraRecoveryState.PermissionObservedOn;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "The approved camera action was cancelled. No retry occurred.";
        }
        catch (Exception)
        {
            if (generation == cameraRecoveryGeneration)
            {
                cameraRecoveryNotice = null;
                cameraRecovery.RecordControlFailure(
                    "The approved camera action failed safely. No retry occurred.");
            }
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }

        if (returnBeforeVerify
            && ReturnToSelectedTeams(
                "Approved camera permission change verified · returned to Teams for fresh inspection."))
        {
            if (cameraRecovery.CanInspectTeams)
                await ObserveSelectedTeamsAsync();
            else
                await VerifySelectedTeamsAsync();
        }
        else if (verifyAfter)
            await VerifySelectedTeamsAsync();
    }

    private async void CameraRestartTeams_Click(object sender, RoutedEventArgs e)
    {
        if (!cameraRecovery.CanRestartTeams
            || CameraWindowPicker.SelectedItem is not WindowChoice selected
            || cameraRecoverySensing is not ICameraRecoveryControl controller)
            return;

        bool verifyAfter = false;
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice =
            "Restarting Teams after separate approval. This can end an active meeting…";
        StatusText.Text = "Teams restart separately approved.";
        UpdateCameraRecoveryUi();
        try
        {
            var result = await controller.RestartTeamsAsync(selected, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            cameraRecovery.RecordTeamsRestart(result);
            if (result.Finding == TeamsRestartFinding.Restarted)
            {
                RefreshWindows();
                if (result.ReopenedWindow is { } reopened)
                {
                    refreshingCameraWindows = true;
                    CameraWindowPicker.SelectedItem =
                        CameraWindowPicker.Items.Cast<WindowChoice>()
                            .FirstOrDefault(candidate => candidate.Id == reopened.Id)
                        ?? CameraWindowPicker.SelectedItem;
                    refreshingCameraWindows = false;
                }
                if (CameraWindowPicker.SelectedItem is WindowChoice rebound
                    && rebound.Matches())
                {
                    cameraRecovery.ChooseTeamsWindow(rebound.Id, rebound.Title);
                    verifyAfter = true;
                }
                else
                {
                    CameraWindowPicker.SelectedItem = null;
                    cameraRecovery.ChooseTeamsWindow("", "");
                }
                cameraRecoveryNotice = null;
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "The approved Teams restart was cancelled.";
        }
        catch (Exception)
        {
            if (generation == cameraRecoveryGeneration)
            {
                cameraRecoveryNotice = null;
                cameraRecovery.RecordControlFailure("Teams could not be restarted safely.");
            }
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }

        if (verifyAfter)
            await VerifySelectedTeamsAsync();
    }

    private void CameraStop_Click(object sender, RoutedEventArgs e)
    {
        CancelCameraOperation();
        cameraRecovery.Cancel();
        cameraTargetPresentationAttempted = false;
        cameraTargetShown = false;
        cameraRecoveryNotice = null;
        StatusText.Text = "Camera recovery stopped · no further observation or guidance will run.";
        UpdateCameraRecoveryUi(CameraStartButton);
    }

    private void CameraTakeOver_Click(object sender, RoutedEventArgs e)
    {
        CancelCameraOperation();
        cameraRecovery.Cancel("Manual takeover. Continue in Teams or Settings yourself; MSGuide has no active camera guidance.");
        cameraTargetPresentationAttempted = false;
        cameraTargetShown = false;
        cameraRecoveryNotice = null;
        StatusText.Text = "Manual takeover · camera guidance stopped.";
        UpdateCameraRecoveryUi(CameraStartButton);
    }
}
