using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class CompactCameraTests
{
    internal static void RunConsentChecks()
    {
        foreach (var mode in new[] { CameraRecoveryInteractionMode.Guide, CameraRecoveryInteractionMode.Control })
        foreach (bool submitted in new[] { false, true })
        {
            var session = new CameraRecoverySession(mode);
            session.Start(submitted);
            IntegrationTests.Require(!session.ConsumeCameraOnRequest());
            session.ChooseTeamsWindow("teams-1", "Synthetic meeting");
            session.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.CameraOff, "",
                CameraTarget));
            bool allowed = submitted && mode == CameraRecoveryInteractionMode.Control;
            IntegrationTests.Require(session.ConsumeCameraOnRequest() == allowed
                && !session.ConsumeCameraOnRequest());
        }
        foreach (var scope in new[]
        {
            CameraRecoveryTargetKind.DeviceCameraPermission,
            CameraRecoveryTargetKind.AppCameraPermission,
            CameraRecoveryTargetKind.PackagedTeamsPermission
        })
        {
            var session = new CameraRecoverySession(CameraRecoveryInteractionMode.Control);
            session.Start(true);
            session.ChooseTeamsWindow("teams-1", "Synthetic meeting");
            session.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.PermissionMayBeOff, "",
                PermissionScope: scope));
            session.PrepareToOpenSettings();
            session.MarkSettingsOpened();
            session.ApplySettingsObservation(PermissionOff(scope));
            IntegrationTests.Require(session.CanControlTarget && !session.ConsumeCameraOnRequest()
                && session.CameraOnRequestAuthorized && !session.CanRestartTeams);
            session.ApplySettingsObservation(PermissionOn);
            session.MarkReturnedToTeams();
            IntegrationTests.Require(session.CanInspectTeams);
            session.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.CameraOff, "", CameraTarget));
            IntegrationTests.Require(session.ConsumeCameraOnRequest() && !session.ConsumeCameraOnRequest());
        }
        foreach (string cancellation in new[] { "stop", "switch-window", "reset", "stale", "unsupported" })
        {
            var session = new CameraRecoverySession(CameraRecoveryInteractionMode.Control);
            session.Start(true);
            session.ChooseTeamsWindow("teams-1", "Synthetic meeting");
            switch (cancellation)
            {
                case "stop": session.Cancel(); break;
                case "switch-window": session.ChooseTeamsWindow("teams-2", "Another meeting"); break;
                case "reset": session.Reset(CameraRecoveryInteractionMode.Control); break;
                case "stale": session.MarkStale("Synthetic stale result"); break;
                case "unsupported": session.MarkUnsupported("Synthetic incomplete controls"); break;
            }
            IntegrationTests.Require(!session.CameraOnRequestAuthorized && !session.ConsumeCameraOnRequest());
        }
        var layout = new MainWindow(new SpeechService(), new CompanionPosition());
        try { layout.CheckCompactCameraLayout(); }
        finally { layout.Close(); }
    }

    private static CameraRecoveryTarget CameraTarget => new(
        "synthetic-camera", CameraRecoveryPinnedTargets.TeamsTurnCameraOn,
        "synthetic-camera", CameraRecoveryTargetKind.TeamsCameraButton);

    private static CameraSettingsObservation PermissionOff(CameraRecoveryTargetKind scope) => new(
        CameraSettingsFinding.PermissionOff, "Synthetic permission is off.",
        new("synthetic-" + scope, "Synthetic permission", scope switch
        {
            CameraRecoveryTargetKind.DeviceCameraPermission => CameraRecoveryPinnedTargets.DeviceCameraToggle,
            CameraRecoveryTargetKind.AppCameraPermission => CameraRecoveryPinnedTargets.AppCameraToggle,
            _ => CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle
        }, scope), CameraSettingsObservationSource.Fixture, true, CameraSettingsPage.CameraPrivacy);

    private static CameraSettingsObservation PermissionOn => new(
        CameraSettingsFinding.PermissionOn, "Synthetic permissions are on.",
        Source: CameraSettingsObservationSource.Fixture, ProbeValidated: true, Page: CameraSettingsPage.CameraPrivacy);

    internal sealed class Sensing(params CameraRecoveryTargetKind[] scopes)
        : ICameraRecoverySensing, ICameraRecoveryControl, ICameraWindowDiscovery
    {
        private Queue<CameraRecoveryTargetKind> remaining = new(scopes);
        private bool enabled;
        internal bool AlreadyOn { get; init; }
        internal bool UnknownOutcome { get; init; }
        internal bool NoEffect { get; init; }
        internal bool NeedsRestart { get; init; }
        internal bool AlreadySatisfiedDuringActivation { get; init; }
        internal bool RejectRevalidation { get; init; }
        internal TaskCompletionSource? ObservationGate { get; init; }
        internal TaskCompletionSource? DiscoveryGate { get; init; }
        internal Func<WindowChoice, CameraSurfaceFinding>? Surface { get; init; }
        internal List<CameraRecoveryTargetKind> Actions { get; } = [];
        internal int Verifications, Restarts, ControlAttempts;
        public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.Fixture;
        public async Task<CameraSurfaceFinding> InspectCameraSurfaceAsync(WindowChoice window, CancellationToken ct)
        {
            if (DiscoveryGate is not null) await DiscoveryGate.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return Surface?.Invoke(window) ?? CameraSurfaceFinding.MeetingCamera;
        }
        public void Reset()
        {
            remaining = new(scopes);
            enabled = AlreadyOn;
            Actions.Clear();
            Verifications = Restarts = ControlAttempts = 0;
        }

        internal void SimulateUserRepair()
        {
            remaining.Clear();
            enabled = true;
        }

        public async Task<TeamsCameraObservation> ObserveTeamsAsync(WindowChoice window, CancellationToken ct)
        {
            if (ObservationGate is not null) await ObservationGate.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (remaining.TryPeek(out var scope))
                return new(window.Id, TeamsCameraFinding.PermissionMayBeOff, "Synthetic blocked permission.",
                    PermissionScope: scope);
            return new(window.Id, enabled ? TeamsCameraFinding.CameraOn : TeamsCameraFinding.CameraOff,
                "Synthetic camera state.", enabled ? null : CameraTarget);
        }

        public Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(remaining.TryPeek(out var scope) ? PermissionOff(scope) : PermissionOn);
        }

        public Task<CameraVerificationResult> VerifyTeamsAsync(WindowChoice window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Verifications++;
            bool ready = enabled && (!NeedsRestart || Restarts > 0);
            return Task.FromResult(new CameraVerificationResult(window.Id,
                ready ? CameraVerificationFinding.Ready : CameraVerificationFinding.NeedsReinitialization,
                ready, "Synthetic readiness evidence.", IsFixture: true, Reinitialized: ready));
        }

        public Task<CameraTargetPresentation> ShowTargetAsync(CameraRecoveryTarget target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new CameraTargetPresentation(true, "Synthetic target only."));
        }

        public Task<CameraTargetControlResult> ActivateTargetAsync(CameraRecoveryTarget target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ControlAttempts++;
            if (RejectRevalidation)
                return Task.FromResult(new CameraTargetControlResult(false, true,
                    "Synthetic camera control could not be revalidated. No action was started."));
            if (AlreadySatisfiedDuringActivation)
            {
                if (target.Kind == CameraRecoveryTargetKind.TeamsCameraButton) enabled = true;
                else
                {
                    IntegrationTests.Require(remaining.Peek() == target.Kind);
                    remaining.Dequeue();
                }
                return Task.FromResult(new CameraTargetControlResult(false, true,
                    "Synthetic control already enabled.", StateAlreadySatisfied: true));
            }
            Actions.Add(target.Kind);
            if (UnknownOutcome) return Task.FromResult(new CameraTargetControlResult(true, false, "Synthetic unknown outcome."));
            if (!NoEffect)
            {
                if (target.Kind == CameraRecoveryTargetKind.TeamsCameraButton) enabled = true;
                else
                {
                    IntegrationTests.Require(remaining.Peek() == target.Kind);
                    remaining.Dequeue();
                }
            }
            return Task.FromResult(new CameraTargetControlResult(true, true, "Synthetic single action."));
        }

        public Task<TeamsRestartResult> RestartTeamsAsync(WindowChoice window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Restarts++;
            return Task.FromResult(new TeamsRestartResult(TeamsRestartFinding.Restarted, "Synthetic restart.", window));
        }
    }

    internal static async Task RunNativeAsync(CancellationToken ct)
    {
        var fixture = new Window
        {
            Title = "MSGuide synthetic meeting | Microsoft Teams",
            Width = 640, Height = 420, ShowActivated = false,
            Content = new TextBlock { Text = "Owned synthetic camera fixture; no camera or microphone is used." }
        };
        try
        {
            fixture.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var selected = new WindowChoice(new WindowInteropHelper(fixture).Handle,
                (uint)Environment.ProcessId, fixture.Title);
            IntegrationTests.Require(selected.Matches());
            foreach (string scenario in new[]
            {
                "camera-off", "already-on", "permissions", "guide", "guide-permission", "guide-global",
                "switch-target", "unknown", "no-effect", "restart", "cancel", "mode-switch",
                "discovery-cancel", "reassess-permission", "guide-reassess", "reassess-cancel",
                "permission-already-enabled", "camera-already-enabled", "revalidation-rejected"
            })
            {
                ct.ThrowIfCancellationRequested();
                var sensing = scenario == "permissions"
                    ? new Sensing(CameraRecoveryTargetKind.DeviceCameraPermission,
                        CameraRecoveryTargetKind.AppCameraPermission, CameraRecoveryTargetKind.PackagedTeamsPermission)
                    : scenario is "guide-global" or "switch-target" or "reassess-permission" or "guide-reassess"
                        ? new Sensing(CameraRecoveryTargetKind.DeviceCameraPermission)
                    : scenario == "guide-permission"
                        ? new Sensing(CameraRecoveryTargetKind.PackagedTeamsPermission)
                    : scenario is "permission-already-enabled" or "revalidation-rejected"
                        ? new Sensing(CameraRecoveryTargetKind.DeviceCameraPermission)
                        {
                            AlreadySatisfiedDuringActivation = scenario == "permission-already-enabled",
                            RejectRevalidation = scenario == "revalidation-rejected"
                        }
                    : new Sensing
                    {
                        AlreadyOn = scenario == "already-on",
                        UnknownOutcome = scenario == "unknown",
                        NoEffect = scenario == "no-effect",
                        NeedsRestart = scenario == "restart",
                        AlreadySatisfiedDuringActivation = scenario == "camera-already-enabled",
                        ObservationGate = scenario is "cancel" or "mode-switch" ? new() : null,
                        DiscoveryGate = scenario is "discovery-cancel" or "reassess-cancel" ? new() : null
                    };
                var main = new MainWindow(new SpeechService(), new CompanionPosition());
                try { await main.CheckCompactCameraAsync(selected, sensing, scenario); }
                finally { main.Close(); }
            }
        }
        finally { fixture.Close(); }
    }
}

public partial class MainWindow
{
    internal void CheckCompactCameraLayout()
    {
        cameraRecoverySensing = new CompactCameraTests.Sensing();
        cameraRecovery = new CameraRecoverySession(CameraRecoveryInteractionMode.Control);
        cameraRecovery.Start(true);
        cameraRecovery.ChooseTeamsWindow("synthetic", "Synthetic Teams meeting");
        cameraRecovery.ApplyTeamsObservation(new("synthetic", TeamsCameraFinding.PermissionMayBeOff,
            "Synthetic permission explanation."));
        refreshingCameraWindows = true;
        var first = new WindowChoice((nint)101, 1, "First | Microsoft Teams", "Synthetic");
        var second = new WindowChoice((nint)102, 1, "Second | Microsoft Teams", "Synthetic");
        CameraWindowPicker.ItemsSource = new[] { first, second };
        CameraWindowPicker.SelectedItem = first;
        refreshingCameraWindows = false;
        RefreshCameraWindows([first, second]);
        IntegrationTests.Require(CameraWindowPicker.SelectedItem is null);
        RefreshCameraWindows([first]);
        IntegrationTests.Require(CameraWindowPicker.SelectedItem is null);
        UpdateCameraRecoveryUi();
        CameraStepText.Text = string.Join(" ", Enumerable.Repeat(
            "Device-wide Camera access affects other apps that already have permission. Review this scope before approving.", 4));
        companion.Prompt.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var content = (FrameworkElement)companion.Prompt.Content;
        foreach (double width in new[] { 470d, 380d })
        {
            content.Measure(new Size(width, 650));
            content.Arrange(new Rect(0, 0, width, 650));
            content.UpdateLayout();
            var scroll = companion.Prompt.WorkspaceScroll;
            var settings = companion.Prompt.SettingsButton;
            Point right = settings.TranslatePoint(new Point(settings.ActualWidth, 0), content);
            IntegrationTests.Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1
                && right.X <= width && settings.ActualWidth >= 60
                && CameraStepText.ActualWidth < width
                && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                && Math.Abs(companion.Prompt.DraftControl.TranslatePoint(default, content).X
                    - CameraRecoveryCard.TranslatePoint(default, content).X) < 1
                && Math.Abs(companion.Prompt.Voice.TranslatePoint(default, content).X
                    - CameraRecoveryCard.TranslatePoint(default, content).X) < 1);
        }
    }

    internal async Task CheckCompactCameraAsync(WindowChoice selected, CompactCameraTests.Sensing sensing, string scenario)
    {
        cameraRecoverySensing = sensing;
        loaded = true;
        try
        {
            if (!scenario.StartsWith("guide", StringComparison.Ordinal)) CameraControlMode.IsChecked = true;
            PromptBox.Text = "Fix my Teams camera";
            invokedWindow = selected;
            if (scenario == "reassess-cancel")
            {
                ResetCameraRecovery();
                refreshingCameraWindows = true;
                CameraWindowPicker.SelectedItem = null;
                refreshingCameraWindows = false;
                Task pending = ReassessSelectedCameraAsync();
                PromptBox.Text = "Explain something unrelated";
                var freshSession = cameraRecovery;
                string freshFeedback = PromptFeedbackText.Text;
                sensing.DiscoveryGate!.TrySetResult();
                await pending;
                IntegrationTests.Require(ReferenceEquals(cameraRecovery, freshSession)
                    && cameraRecovery.State == CameraRecoveryState.Idle
                    && CameraRecoveryCard.Visibility == Visibility.Collapsed
                    && PromptFeedbackText.Text == freshFeedback
                    && sensing.Actions.Count == 0 && cameraRecovery.Target is null);
                return;
            }
            Task request = SubmitPromptAsync();
            if (scenario is "cancel" or "mode-switch" or "discovery-cancel")
            {
                if (scenario is "cancel" or "discovery-cancel") CameraStop_Click(this, new RoutedEventArgs());
                else
                {
                    var original = cameraRecovery;
                    CompanionModeTests.Select(companion.Prompt.FixModeOption);
                    IntegrationTests.Require(ReferenceEquals(original, cameraRecovery)
                        && cameraRecoveryBusy && cameraRecovery.CameraOnRequestAuthorized);
                    CompanionModeTests.Select(companion.Prompt.GuideModeOption);
                    IntegrationTests.Require(SelectedCameraMode == CameraRecoveryInteractionMode.Guide
                        && companion.Prompt.GuideModeOption.IsChecked == true
                        && companion.Prompt.FixModeOption.IsChecked == false);
                }
                sensing.ObservationGate?.TrySetResult();
                sensing.DiscoveryGate?.TrySetResult();
                await request;
                IntegrationTests.Require(sensing.Actions.Count == 0 && !cameraRecovery.CameraOnRequestAuthorized
                    && cameraRecovery.Target is null);
                return;
            }
            await request;
            IntegrationTests.Require(!IsVisible && companion.Prompt.HasCameraWorkspace
                && companion.Prompt.CameraKeepsPromptVisible
                && CameraWindowPicker.SelectedItem is WindowChoice picked && picked.Id == selected.Id);
            if (scenario is "permission-already-enabled" or "revalidation-rejected")
            {
                IntegrationTests.Require(cameraRecovery.Target?.Kind == CameraRecoveryTargetKind.DeviceCameraPermission
                    && sensing.ControlAttempts == 0);
                await ActivateCameraTargetAsync();
                if (scenario == "revalidation-rejected")
                {
                    await ContinueCameraRepairAsync();
                    IntegrationTests.Require(sensing.ControlAttempts == 1 && sensing.Actions.Count == 0
                        && cameraRecovery.State == CameraRecoveryState.ControlRevalidationRequired
                        && cameraRecovery.IsTerminal && cameraRecovery.Target is null
                        && !cameraRecovery.CameraOnRequestAuthorized
                        && CameraStateText.Text == "Camera control needs another check");
                    return;
                }
            }
            if (scenario is "permission-already-enabled" or "camera-already-enabled")
            {
                IntegrationTests.Require(cameraRecovery.State == CameraRecoveryState.FixtureComplete
                    && sensing.Verifications == 1 && sensing.Actions.Count == 0
                    && sensing.ControlAttempts == (scenario == "permission-already-enabled" ? 2 : 1));
                return;
            }
            if (scenario is "reassess-permission" or "guide-reassess")
            {
                double height = companion.Prompt.Height;
                ResetCameraRecovery();
                await ReassessSelectedCameraAsync();
                IntegrationTests.Require(cameraRecovery.State == CameraRecoveryState.ReadOnlyAssessment
                    && CameraRecoveryCard.Visibility == Visibility.Visible
                    && companion.Prompt.CameraKeepsPromptVisible && companion.Prompt.Height == height
                    && CameraStatePanel.Visibility == Visibility.Visible
                    && CameraStartButton.Content?.ToString() == (scenario == "guide-reassess" ? "Continue guide" : "Fix camera")
                    && !cameraRecovery.CameraOnRequestAuthorized && cameraRecovery.Target is null
                    && sensing.Actions.Count == 0
                    && !cameraRecovery.Detail.Contains("Choose a mode", StringComparison.Ordinal));
                IntegrationTests.Require(AutomationProperties.GetName(CameraStartButton)
                    .Contains(scenario == "guide-reassess" ? "no automatic changes" : "authorize turning", StringComparison.Ordinal));
                if (scenario == "reassess-permission")
                    IntegrationTests.Require(AutomationProperties.GetHelpText(CameraStartButton).Contains("ask separately"));
                return;
            }
            if (scenario == "switch-target")
            {
                IntegrationTests.Require(cameraRecovery.CameraOnRequestAuthorized && cameraRecovery.Target is not null
                    && CameraChooseWindowButton.Visibility == Visibility.Visible);
                BeginCameraWindowSelection();
                IntegrationTests.Require(CameraSelectionPanel.Visibility == Visibility.Visible
                    && CameraWindowPicker.SelectedItem is null && cameraRecovery.Target is null
                    && !cameraRecovery.CameraOnRequestAuthorized && sensing.Actions.Count == 0);
                return;
            }
            if (scenario is "guide-permission" or "guide-global")
            {
                IntegrationTests.Require(cameraRecovery.CanOpenSettings && sensing.Actions.Count == 0);
                await OpenCameraSettingsAsync();
                IntegrationTests.Require(cameraRecovery.CanCheckChangedSetting && !cameraRecovery.CanControlTarget);
                sensing.SimulateUserRepair();
                await ObserveCameraSettings();
                IntegrationTests.Require(cameraRecovery.State == CameraRecoveryState.FixtureComplete
                    && sensing.Verifications == 1 && sensing.Actions.Count == 0 && sensing.Restarts == 0);
                return;
            }
            if (scenario == "permissions")
            {
                foreach (var scope in new[]
                {
                    CameraRecoveryTargetKind.DeviceCameraPermission,
                    CameraRecoveryTargetKind.AppCameraPermission,
                    CameraRecoveryTargetKind.PackagedTeamsPermission
                })
                {
                    IntegrationTests.Require(cameraRecovery.State == CameraRecoveryState.VerifiedTarget
                        && cameraRecovery.Target?.Kind == scope && cameraRecovery.CameraOnRequestAuthorized
                        && !sensing.Actions.Contains(scope) && sensing.Restarts == 0);
                    await ActivateCameraTargetAsync();
                }
                IntegrationTests.Require(sensing.Actions.SequenceEqual(new[]
                {
                    CameraRecoveryTargetKind.DeviceCameraPermission, CameraRecoveryTargetKind.AppCameraPermission,
                    CameraRecoveryTargetKind.PackagedTeamsPermission, CameraRecoveryTargetKind.TeamsCameraButton
                }));
            }
            if (scenario == "guide")
            {
                IntegrationTests.Require(sensing.Actions.Count == 0 && cameraRecovery.CanShowTarget
                    && !cameraRecovery.CanControlTarget && !cameraRecovery.CameraOnRequestAuthorized);
                return;
            }
            if (scenario is "unknown" or "no-effect")
            {
                await ContinueCameraRepairAsync();
                IntegrationTests.Require(sensing.Actions.Count == 1 && sensing.Verifications == 0
                    && cameraRecovery.State == CameraRecoveryState.Unsupported
                    && !cameraRecovery.CameraOnRequestAuthorized);
                return;
            }
            if (scenario == "restart")
            {
                IntegrationTests.Require(cameraRecovery.CanRestartTeams && sensing.Restarts == 0
                    && sensing.Actions.Count == 1);
                return;
            }
            IntegrationTests.Require(cameraRecovery.State == CameraRecoveryState.FixtureComplete
                && cameraRecovery.LocalVerifierPassed && sensing.Verifications == 1
                && sensing.Actions.Count == (scenario == "permissions" ? 4 : scenario == "already-on" ? 0 : 1));
            companion.Prompt.DismissPrompt();
            IntegrationTests.Require(!cameraRecovery.CameraOnRequestAuthorized);
        }
        finally { loaded = false; }
    }
}
