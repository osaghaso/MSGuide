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
    private bool cameraChoosingWindow;
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
            cameraChoosingWindow ? null
            : teamsWindows.FirstOrDefault(window => window.Id == selected?.Id
                && window.Id == cameraRecovery.TeamsWindowId);
        refreshingCameraWindows = false;
        CameraWindowHint.Text = teamsWindows.Length switch
        {
            0 => "No Teams window found. Open Teams, then refresh.",
            _ => "MSGuide will look for the meeting or prejoin with a camera control."
        };
    }

    private async Task StartCameraRecoveryAsync(bool fromPrompt)
    {
        var invoked = invokedWindow;
        ResetCameraRecovery();
        cameraRecovery.Start(authorizeCameraOn: fromPrompt);
        RefreshWindows();
        cameraRecoveryNotice = null;
        StatusText.Text = "Finding the Teams meeting camera. Nothing has been changed.";
        UpdateCameraRecoveryUi(fromPrompt ? null : CameraWindowPicker);
        companion.ShowCameraTask();
        if (!await SelectCameraMeetingAsync(invoked)) return;
        await ObserveSelectedTeamsAsync();
        await ContinueCameraRepairAsync();
    }

    private async Task<bool> SelectCameraMeetingAsync(WindowChoice? invoked, bool bindSession = true)
    {
        if (cameraRecoverySensing is not ICameraWindowDiscovery discovery)
        {
            cameraRecoveryNotice = "Automatic meeting inspection is unavailable. Choose the intended Teams window.";
            cameraChoosingWindow = true;
            UpdateCameraRecoveryUi();
            return false;
        }
        var candidates = CameraWindowPicker.Items.OfType<WindowChoice>().ToArray();
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Finding the Teams meeting or prejoin by its camera controls...";
        UpdateCameraRecoveryUi();
        try
        {
            var result = await CameraWindowDiscovery.SelectAsync(candidates, invoked, discovery, token);
            if (!CurrentCameraOperation(generation, token)) return false;
            cameraRecoveryNotice = result.Window is null ? result.Detail : null;
            CameraWindowHint.Text = result.Detail;
            cameraChoosingWindow = result.Window is null;
            refreshingCameraWindows = true;
            try { CameraWindowPicker.SelectedItem = result.Window; }
            finally { refreshingCameraWindows = false; }
            if (result.Window is null) return false;
            if (!result.Window.Matches())
            {
                cameraRecovery.MarkStale("The meeting changed while it was being identified. Submit the request again.");
                return false;
            }
            if (bindSession) cameraRecovery.ChooseTeamsWindow(result.Window.Id, result.Window.Title);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Meeting inspection was cancelled or timed out. No camera action was prepared.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            if (generation == cameraRecoveryGeneration)
            {
                cameraChoosingWindow = true;
                cameraRecoveryNotice = "The meeting could not be identified safely. Choose the intended Teams window; nothing was changed.";
            }
        }
        finally
        {
            FinishCameraOperation(generation);
            UpdateCameraRecoveryUi();
        }
        return false;
    }

    private void ResetCameraRecovery()
    {
        CancelCameraOperation();
        cameraRecoverySensing.Reset();
        cameraRecovery = new CameraRecoverySession(SelectedCameraMode);
        cameraChoosingWindow = false;
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
            ? "Your Fix request turns on the verified camera once. Permission changes and restart ask separately."
            : "You make every click. MSGuide verifies each next step.";
        CameraActionPolicyText.Text = controlMode ? "Approved actions only" : "No automatic clicks";
        string lockedModeHelp = controlAvailable
            ? "Mode is locked while recovery is active. Use Stop and switch to change mode; previous approval will be cleared."
            : "Mode is locked while recovery is active. Stop recovery to choose another mode.";
        CameraModeHint.Text = modeSelectionEnabled
            ? controlMode
                ? sessionAutomationApproved
                    ? "Ask to fix the camera to authorize camera-on once. Permissions and restart always ask separately."
                    : "MSGuide asks for approval before making a supported change."
                : "You make changes; MSGuide guides you and checks results."
            : lockedModeHelp;
        string guideModeHelp = modeSelectionEnabled
            ? "Choose Guide me so you perform each action and MSGuide verifies it."
            : lockedModeHelp;
        string controlModeHelp = !modeSelectionEnabled
            ? lockedModeHelp
            : controlAvailable
                ? "A submitted camera repair request authorizes one verified camera-on action. Permission changes and restart ask separately."
                : "Fix it for me is unavailable because verified local camera control is not connected.";
        AutomationProperties.SetHelpText(CameraGuideMode, guideModeHelp);
        AutomationProperties.SetHelpText(CameraControlMode, controlModeHelp);
        CameraGuideMode.ToolTip = guideModeHelp;
        CameraControlMode.ToolTip = controlModeHelp;
        UpdateCompactTaskUi();
        CameraStateText.Text = cameraRecoveryBusy && cameraRecovery.CanStart
            ? "Checking current camera"
            : CameraStateLabel(cameraRecovery.State);
        CameraMeetingText.Text = cameraRecovery.TeamsWindowTitle is { Length: > 0 } meeting
            ? "Meeting: " + meeting : "Automatically finding the meeting camera";
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
        CameraStartButton.Content = cameraRecovery.CanStart ? "Start camera recovery"
            : cameraRecovery.State == CameraRecoveryState.ReadOnlyAssessment
                ? controlMode ? "Fix camera" : "Continue guide"
                : "Check again";
        AutomationProperties.SetName(CameraStartButton, cameraRecovery.CanStart
            ? "Start Teams camera recovery"
            : cameraRecovery.State == CameraRecoveryState.ReadOnlyAssessment
                ? controlMode ? "Fix Teams camera; authorize turning the camera on once"
                    : "Continue camera guidance; no automatic changes"
                : "Recheck the camera without authorizing another change");
        AutomationProperties.SetHelpText(CameraStartButton,
            cameraRecovery.State == CameraRecoveryState.ReadOnlyAssessment && controlMode
                ? "Start a fresh repair and authorize one verified camera-on action. Permission changes and Teams restart still ask separately."
                : "Read current camera evidence without authorizing automatic changes.");
        CameraStartButton.IsEnabled = !cameraRecoveryBusy;
        bool canComplete = !cameraRecoveryBusy && cameraRecovery.CanComplete;
        CameraDoneButton.IsEnabled = canComplete;
        CameraDoneButton.Visibility = canComplete ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(CameraDoneButton,
            cameraRecovery.State == CameraRecoveryState.FixtureComplete
                ? "Done; close the simulated camera task" : "Done; close the resolved camera task");
        CameraStartButton.SetResourceReference(StyleProperty, canComplete ? "QuietButtonStyle" : "PrimaryButtonStyle");
        CameraChooseWindowButton.Content = CameraWindowPicker.SelectedItem is not null
            ? "Change meeting" : CameraWindowPicker.HasItems ? "Choose meeting" : "Find meeting";
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
        CameraSelectionPanel.Visibility = !cameraChoosingWindow && (cameraRecovery.State == CameraRecoveryState.Idle
                || cameraRecovery.IsTerminal || selectedWindowValid)
            ? Visibility.Collapsed : Visibility.Visible;
        CameraChooseWindowButton.Visibility = !cameraRecoveryBusy
                && cameraRecovery.State != CameraRecoveryState.Idle
            ? Visibility.Visible : Visibility.Collapsed;
        CameraInspectButton.Visibility = CameraInspectButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraOpenSettingsButton.Visibility = CameraOpenSettingsButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraPrivateCheckButton.Visibility = CameraPrivateCheckButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        CameraShowButton.Content = cameraTargetPresentationAttempted ? "Try showing again" : "Show me";
        CameraShowButton.Style = (Style)FindResource(cameraTargetPresentationAttempted
            ? "QuietButtonStyle" : "PrimaryButtonStyle");
        CameraShowButton.Visibility = !controlMode && CameraShowButton.IsEnabled && !cameraTargetShown
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
        UpdateCompactCameraUi();
        if (!controlMode && !ReferenceEquals(advancingCameraSession, cameraRecovery)) focus?.Focus();
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
        CameraRecoveryState.Ready => "Resolved · camera ready",
        CameraRecoveryState.FixtureComplete => "Fixture complete · not a real readiness claim",
        CameraRecoveryState.ReadOnlyAssessment => "Camera needs attention",
        CameraRecoveryState.ControlRevalidationRequired => "Camera control needs another check",
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
            or CameraRecoveryState.AlreadyOnOrWrongCause or CameraRecoveryState.ManagedOrDisabled
            or CameraRecoveryState.ReadOnlyAssessment => 2,
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

    private async Task ObserveCameraSettings(bool waitForPage = false)
    {
        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Verifying the Camera privacy page and observing its controls locally…";
        UpdateCameraRecoveryUi();
        try
        {
            var observation = await cameraRecoverySensing.ObserveSettingsAsync(token);
            for (int attempt = 0; waitForPage && attempt < 5
                && observation.Finding == CameraSettingsFinding.WrongPage; attempt++)
            {
                await Task.Delay(500, token);
                observation = await cameraRecoverySensing.ObserveSettingsAsync(token);
            }
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
        if (CurrentCameraOperation(generation, token)) await ContinueCameraRepairAsync();
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
        if (loaded) screenTask?.RevokePlan();
        bool endedRun = !cameraRecovery.CanStart;
        ResetCameraRecovery();
        if (loaded && endedRun)
            StatusText.Text = "Camera recovery reset for the selected mode · start when ready.";
        UpdateCameraRecoveryUi();
        UpdateScreenActionUi();
        UpdateScreenTaskUi();
    }

    private void CameraSwitchMode_Click(object sender, RoutedEventArgs e) =>
        SelectCameraMode(SelectedCameraMode == CameraRecoveryInteractionMode.Guide
            ? CameraRecoveryInteractionMode.Control : CameraRecoveryInteractionMode.Guide);

    private void SelectCameraMode(CameraRecoveryInteractionMode requested)
    {
        if (requested == SelectedCameraMode) return;
        bool switchToControl = requested == CameraRecoveryInteractionMode.Control;
        if (switchToControl && cameraRecoverySensing is not ICameraRecoveryControl)
        {
            StatusText.Text = "Fix it for me is unavailable because verified local control is not connected.";
            UpdateCompactTaskUi();
            return;
        }
        promptRequestActive = false;
        CancelWork();
        screenTask?.RevokePlan();
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
        if (cameraRecovery.State == CameraRecoveryState.ReadOnlyAssessment)
        {
            CancelWork();
            await StartCameraRecoveryAsync(fromPrompt: true);
            return;
        }
        bool resetOnly = !cameraRecovery.CanStart;
        CancelWork();
        if (resetOnly)
        {
            ResetCameraRecovery();
            StatusText.Text = "Checking the camera again. No additional change is authorized.";
            UpdateCameraRecoveryUi(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                ? CameraControlMode : CameraGuideMode);
            await ReassessSelectedCameraAsync();
            return;
        }
        await StartCameraRecoveryAsync(fromPrompt: false);
    }

    private void CameraDone_Click(object sender, RoutedEventArgs e)
    {
        if (cameraRecoveryBusy || !cameraRecovery.CanComplete) return;
        Pause();
        ResetCameraRecovery();
        promptRequestActive = false;
        SettingsExpander.IsExpanded = false;
        companion.Prompt.SetSettingsVisible(false);
        ShowPromptFeedback("");
        UpdateCameraRecoveryUi();
        if (!speech.Busy)
        {
            pausedBanner = null;
            StatusText.Text = "Ready for your next question.";
        }
        if (hotkeyRegistered) companion.Prompt.DismissPrompt();
    }

    private async Task ReassessSelectedCameraAsync()
    {
        var session = cameraRecovery;
        var selected = CameraWindowPicker.SelectedItem as WindowChoice;
        if (selected is null || !selected.Matches())
        {
            RefreshWindows();
            bool foundMeeting = await SelectCameraMeetingAsync(null, bindSession: false);
            if (!ReferenceEquals(cameraRecovery, session) || closing || !session.CanStart) return;
            if (!foundMeeting || CameraWindowPicker.SelectedItem is not WindowChoice found)
            {
                if (cameraRecovery.CanStart)
                    cameraRecovery.MarkStale("The meeting could not be identified for a fresh check. Submit your camera request when its controls are visible.");
                UpdateCameraRecoveryUi();
                return;
            }
            selected = found;
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
            cameraRecoveryNotice = null;
            StatusText.Text = cameraRecovery.LocalVerifierPassed
                ? cameraRecovery.Detail
                : "Read-only check complete. Submit your camera request to start a new repair.";
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
        BeginCameraWindowSelection();
        if (!CameraWindowPicker.HasItems) return;
        CameraWindowPicker.Focus();
        CameraWindowPicker.IsDropDownOpen = true;
    }

    private void BeginCameraWindowSelection()
    {
        CancelCameraOperation();
        cameraRecovery.ChooseTeamsWindow("", "");
        cameraChoosingWindow = true;
        cameraTargetPresentationAttempted = cameraTargetShown = false;
        cameraRecoveryNotice = null;
        overlay.Hide();
        RefreshWindows();
        if (!CameraWindowPicker.HasItems)
        {
            cameraRecoveryNotice = "No Microsoft Teams window is visible yet. Open Teams, then refresh.";
            StatusText.Text = "Waiting for a visible Microsoft Teams window.";
            UpdateCameraRecoveryUi(CameraChooseWindowButton);
            return;
        }
        StatusText.Text = "Choose a Teams window. Previous observations and camera-on authority were cleared.";
        UpdateCameraRecoveryUi(CameraWindowPicker);
    }

    private async void CameraWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || refreshingCameraWindows) return;
        CancelCameraOperation();
        cameraChoosingWindow = false;
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
        await ContinueCameraRepairAsync();
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
        if (CurrentCameraOperation(generation, token)) await ContinueCameraRepairAsync();
    }

    private async void CameraOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        await OpenCameraSettingsAsync();
        await ContinueCameraRepairAsync();
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

    private async void CameraReturn_Click(object sender, RoutedEventArgs e)
    {
        if (ReturnToSelectedTeams("Returning to Teams for read-only verification. No Teams control was clicked."))
            await ContinueCameraRepairAsync();
    }

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
        try
        {
            if (cameraRecoverySensing.Mode != CameraRecoverySensingMode.Fixture)
                AutomationElement.FromHandle(selected.Handle).SetFocus();
        }
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

    private async void CameraControlTarget_Click(object sender, RoutedEventArgs e) =>
        await ActivateCameraTargetAsync();

    private async Task ActivateCameraTargetAsync()
    {
        if (cameraRecoveryBusy || !cameraRecovery.CanControlTarget
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
            else if (!control.Invoked && !control.StateAlreadySatisfied)
            {
                cameraRecovery.RequireControlRevalidation(control.Detail);
            }
            else
            {
                if (control.Invoked) await Task.Delay(300, token);
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
                            control.Invoked
                                ? "The Teams camera control still appears off after the approved action. No retry occurred."
                                : "The Teams camera is no longer on. No input was sent and no automatic retry occurred.");
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
                            control.Invoked
                                ? "The approved camera permission still appears off. No retry occurred."
                                : "The approved permission is no longer on. No input was sent and no automatic retry occurred.");
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

        if (!CurrentCameraOperation(generation, token)) return;
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
        await ContinueCameraRepairAsync();
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
