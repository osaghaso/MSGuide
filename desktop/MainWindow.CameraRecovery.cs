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
        refreshingCameraWindows = true;
        CameraWindowPicker.ItemsSource = windows;
        CameraWindowPicker.SelectedItem = windows.FirstOrDefault(window => window.Id == selected?.Id);
        refreshingCameraWindows = false;
    }

    private void StartCameraRecovery(bool fromPrompt)
    {
        CancelCameraOperation();
        cameraRecovery = new CameraRecoverySession();
        cameraRecovery.Start();
        cameraTargetPresentationAttempted = false;
        cameraTargetShown = false;
        if (CameraWindowPicker.SelectedItem is WindowChoice selected)
            cameraRecovery.ChooseTeamsWindow(selected.Id, selected.Title);
        cameraRecoveryNotice = fromPrompt
            ? "Camera-help intent recognized locally from the shared editable prompt. Choose the exact Teams window."
            : null;
        StatusText.Text = "Camera recovery guide active · no screenshot, upload, click, or setting change started.";
        UpdateCameraRecoveryUi(fromPrompt ? null : CameraWindowPicker);
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
        CameraStateText.Text = CameraStateLabel(cameraRecovery.State);
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
        CameraStartButton.Content = cameraRecovery.State == CameraRecoveryState.Idle
            ? "Start camera recovery" : "Start over";
        CameraStartButton.IsEnabled = !cameraRecoveryBusy;
        CameraChooseWindowButton.IsEnabled = !cameraRecoveryBusy;
        CameraWindowPicker.IsEnabled = !cameraRecoveryBusy;
        CameraInspectButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanInspectTeams;
        CameraPrivateCheckButton.IsEnabled = !cameraRecoveryBusy
            && (cameraRecovery.CanInspectSettings || cameraRecovery.CanVerifyTeams);
        CameraPrivateCheckButton.Content = cameraRecovery.CanInspectSettings
            ? "Inspect Camera Settings"
            : "Private visual check";
        AutomationProperties.SetName(CameraPrivateCheckButton, cameraRecovery.CanInspectSettings
            ? "Inspect Camera Settings controls without capturing pixels"
            : "Run the local Teams camera readiness verifier");
        CameraOpenSettingsButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanOpenSettings;
        CameraShowButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanShowTarget;
        CameraChangedCheckButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanCheckChangedSetting;
        CameraReturnButton.IsEnabled = !cameraRecoveryBusy && cameraRecovery.CanReturnToTeams;
        bool canStop = cameraRecovery.State is not (CameraRecoveryState.Idle or CameraRecoveryState.Cancelled);
        CameraStopButton.IsEnabled = canStop;
        CameraTakeOverButton.IsEnabled = canStop;

        bool showRestart = !cameraRecoveryBusy
            && (cameraRecovery.State == CameraRecoveryState.Idle || cameraRecovery.IsTerminal);
        CameraStartButton.Visibility = showRestart ? Visibility.Visible : Visibility.Collapsed;
        CameraSelectionPanel.Visibility = cameraRecovery.State == CameraRecoveryState.Idle
            ? Visibility.Collapsed : Visibility.Visible;
        CameraChooseWindowButton.Visibility = !cameraRecoveryBusy
                && cameraRecovery.State == CameraRecoveryState.NeedsTeamsObservation
                && CameraWindowPicker.SelectedItem is null
            ? Visibility.Visible : Visibility.Collapsed;
        CameraInspectButton.Visibility = CameraInspectButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraOpenSettingsButton.Visibility = CameraOpenSettingsButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraPrivateCheckButton.Visibility = CameraPrivateCheckButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CameraShowButton.Content = cameraTargetPresentationAttempted ? "Try showing again" : "Show me";
        CameraShowButton.Style = (Style)FindResource(cameraTargetPresentationAttempted
            ? "QuietButtonStyle" : "PrimaryButtonStyle");
        CameraShowButton.Visibility = CameraShowButton.IsEnabled && !cameraTargetShown
            ? Visibility.Visible : Visibility.Collapsed;
        CameraChangedCheckButton.Visibility = CameraChangedCheckButton.IsEnabled
                && (cameraTargetShown || cameraTargetPresentationAttempted)
            ? Visibility.Visible : Visibility.Collapsed;
        CameraReturnButton.Visibility = CameraReturnButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
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
        focus?.Focus();
    }

    private static string CameraStateLabel(CameraRecoveryState state) => state switch
    {
        CameraRecoveryState.Idle => "Ready to help",
        CameraRecoveryState.NeedsTeamsObservation => "Choose and inspect Teams",
        CameraRecoveryState.Diagnosis => "Camera permission may be blocked",
        CameraRecoveryState.NeedsCameraSettings => "Open Camera settings",
        CameraRecoveryState.NeedsSettingsObservation => "Check the Camera privacy page",
        CameraRecoveryState.VerifiedTarget => "Teams permission found",
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
        CameraRecoveryState.VerifiedTarget => CameraShowButton,
        CameraRecoveryState.PermissionObservedOn => CameraReturnButton,
        CameraRecoveryState.NeedsLocalVerification or CameraRecoveryState.NeedsCameraReinitialization
            => CameraPrivateCheckButton,
        _ => CameraStartButton
    };

    private void CameraStart_Click(object sender, RoutedEventArgs e)
    {
        CancelWork();
        StartCameraRecovery(fromPrompt: false);
    }

    private void CameraChooseWindow_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        CameraWindowPicker.Focus();
        CameraWindowPicker.IsDropDownOpen = true;
    }

    private void CameraWindow_Changed(object sender, SelectionChangedEventArgs e)
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
    }

    private async void CameraInspect_Click(object sender, RoutedEventArgs e)
    {
        if (!cameraRecovery.CanInspectTeams || CameraWindowPicker.SelectedItem is not WindowChoice selected)
            return;
        if (!selected.Matches())
        {
            cameraRecovery.MarkStale("The selected Teams window moved, closed, or changed. Choose it again.");
            UpdateCameraRecoveryUi(CameraStartButton);
            return;
        }

        var (token, generation) = BeginCameraOperation();
        cameraRecoveryNotice = "Inspecting Teams controls locally…";
        UpdateCameraRecoveryUi();
        try
        {
            var observation = await cameraRecoverySensing.ObserveTeamsAsync(selected, token);
            if (!CurrentCameraOperation(generation, token)) return;
            cameraRecoveryNotice = null;
            cameraRecovery.ApplyTeamsObservation(observation);
            UpdateCameraRecoveryUi(CameraFocusForState());
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Teams controls inspection was cancelled or timed out. No screenshot was taken.";
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

    private async void CameraChangedCheck_Click(object sender, RoutedEventArgs e) => await ObserveCameraSettings();

    private void CameraReturn_Click(object sender, RoutedEventArgs e)
    {
        if (!cameraRecovery.CanReturnToTeams || CameraWindowPicker.SelectedItem is not WindowChoice selected) return;
        if (selected.Id != cameraRecovery.TeamsWindowId || !selected.Matches())
        {
            cameraRecovery.MarkStale("The selected Teams window moved, closed, or changed. Choose it again.");
            UpdateCameraRecoveryUi(CameraStartButton);
            return;
        }
        cameraRecoveryNotice = null;
        cameraRecovery.MarkReturnedToTeams();
        try { AutomationElement.FromHandle(selected.Handle).SetFocus(); }
        catch
        {
            cameraRecoveryNotice =
                "Use the taskbar to return to the selected Teams window, then come back and run Private visual check.";
        }
        StatusText.Text = "Return to Teams requested by your click · no Teams control was clicked.";
        UpdateCameraRecoveryUi(CameraPrivateCheckButton);
    }

    private async void CameraPrivateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (cameraRecovery.CanInspectSettings)
        {
            await ObserveCameraSettings();
            return;
        }
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
                ? "Camera ready · passed a local verifier supplied to the camera recovery session."
                : "Camera not verified ready · see the explicit recovery state.";
        }
        catch (OperationCanceledException)
        {
            if (generation == cameraRecoveryGeneration)
                cameraRecoveryNotice = "Local Teams verification was cancelled or timed out. Camera-ready was not claimed.";
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
