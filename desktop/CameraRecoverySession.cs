namespace MSGuide.Desktop;

internal enum CameraRecoveryState
{
    Idle,
    NeedsTeamsObservation,
    Diagnosis,
    NeedsCameraSettings,
    NeedsSettingsObservation,
    VerifiedTarget,
    PermissionObservedOn,
    NeedsLocalVerification,
    NeedsCameraReinitialization,
    Ready,
    FixtureComplete,
    ReadOnlyAssessment,
    ControlRevalidationRequired,
    WrongSettingsPage,
    ManagedOrDisabled,
    AlreadyOnOrWrongCause,
    StaleOrMoved,
    UnresolvedAfterPermission,
    Unsupported,
    Cancelled
}

internal enum TeamsCameraFinding
{
    CameraOff,
    CameraOn,
    PermissionMayBeOff,
    PermissionAlreadyOnOrDifferentCause,
    ManagedOrDisabled,
    StaleOrMoved,
    Unsupported
}

internal enum CameraSettingsFinding
{
    PermissionOff,
    PermissionOn,
    WrongPage,
    ManagedOrDisabled,
    StaleOrMoved,
    Unsupported
}

internal enum CameraVerificationFinding
{
    Ready,
    AlreadyReady,
    NeedsReinitialization,
    Unresolved,
    StaleOrMoved,
    Unsupported
}

internal enum CameraRecoverySensingMode
{
    UnsupportedFallback,
    Fixture,
    Connected
}

internal enum CameraRecoveryInteractionMode
{
    Guide,
    Control
}

internal enum CameraRecoveryTargetKind
{
    Unknown,
    TeamsCameraButton,
    PackagedTeamsPermission,
    DeviceCameraPermission,
    AppCameraPermission
}

internal enum TeamsRestartFinding
{
    Restarted,
    StaleOrMoved,
    Unsupported,
    Failed
}

internal enum CameraSettingsObservationSource
{
    Unknown,
    ControlsOnly,
    PrivateVisual,
    Fixture
}

internal enum CameraSettingsPage
{
    Unknown,
    CameraPrivacy,
    Other
}

internal static class CameraRecoveryPinnedTargets
{
    private const string TeamsCameraShortcut = "Ctrl+Shift+O";
    public const string TeamsVideoSettings = "VideoSettings";
    public const string PackagedTeamsCameraToggle = "MSTeams_8wekyb3d8bbwe_ToggleSwitch";
    public const string DeviceCameraToggle = "SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch";
    public const string AppCameraToggle = "SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch";
    public const string TeamsTurnCameraOn = "Turn camera on";
    public const string TeamsTurnCameraOff = "Turn camera off";
    public const string TeamsCameraToggle = "Camera";
    public const string FixturePreparation =
        "Fixture mode simulates a Teams camera button that starts off.";

    public static bool IsTeamsTurnCameraOn(string? label) =>
        MatchesTeamsCommand(label, TeamsTurnCameraOn);

    public static bool IsTeamsTurnCameraOff(string? label) =>
        MatchesTeamsCommand(label, TeamsTurnCameraOff);

    public static bool IsTeamsCameraToggle(string? label) =>
        string.Equals(label, TeamsCameraToggle, StringComparison.Ordinal);

    internal static bool IsTeamsCameraElement(ElementInfo element) =>
        element.Role is "button" or "checkbox" && !element.IsPassword && !element.IsOffscreen
        && (IsTeamsTurnCameraOn(element.Label) || IsTeamsTurnCameraOff(element.Label)
            || IsTeamsCameraToggle(element.Label) && element.ToggleState is "off" or "on");

    public static bool IsPermission(CameraRecoveryTargetKind kind) =>
        kind is CameraRecoveryTargetKind.PackagedTeamsPermission
            or CameraRecoveryTargetKind.DeviceCameraPermission
            or CameraRecoveryTargetKind.AppCameraPermission;

    public static bool IsPermissionTarget(string? automationId, CameraRecoveryTargetKind kind) =>
        kind switch
        {
            CameraRecoveryTargetKind.PackagedTeamsPermission => automationId == PackagedTeamsCameraToggle,
            CameraRecoveryTargetKind.DeviceCameraPermission => automationId == DeviceCameraToggle,
            CameraRecoveryTargetKind.AppCameraPermission => automationId == AppCameraToggle,
            _ => false
        };

    private static bool MatchesTeamsCommand(string? label, string command)
    {
        if (string.Equals(label, command, StringComparison.Ordinal)) return true;
        return string.Equals(
            label, $"{command} ({TeamsCameraShortcut})", StringComparison.Ordinal);
    }
}

internal sealed record TeamsCameraObservation(
    string WindowId, TeamsCameraFinding Finding, string Detail, CameraRecoveryTarget? Target = null,
    CameraRecoveryTargetKind PermissionScope = CameraRecoveryTargetKind.Unknown);
internal sealed record CameraRecoveryTarget(
    string ObservationId, string Label, string? AutomationId = null,
    CameraRecoveryTargetKind Kind = CameraRecoveryTargetKind.Unknown);
internal sealed record CameraSettingsObservation(
    CameraSettingsFinding Finding, string Detail, CameraRecoveryTarget? Target = null,
    CameraSettingsObservationSource Source = CameraSettingsObservationSource.Unknown,
    bool ProbeValidated = false, CameraSettingsPage Page = CameraSettingsPage.Unknown);
internal sealed record CameraVerificationResult(
    string WindowId, CameraVerificationFinding Finding, bool LocalVerifierPassed, string Detail,
    bool IsFixture = false, bool Reinitialized = false);
internal sealed record CameraTargetPresentation(bool Shown, string Detail);
internal sealed record CameraTargetControlResult(
    bool Invoked, bool OutcomeKnown, string Detail, bool StateAlreadySatisfied = false);
internal sealed record TeamsRestartResult(
    TeamsRestartFinding Finding, string Detail, WindowChoice? ReopenedWindow = null);

internal interface ICameraRecoverySensing
{
    CameraRecoverySensingMode Mode { get; }
    void Reset();
    // Stay rooted to window.Handle, but do not require WebView descendants to share its process ID.
    Task<TeamsCameraObservation> ObserveTeamsAsync(WindowChoice window, CancellationToken cancellationToken);
    // Supported findings must identify a disclosed observation source that has passed a live probe.
    Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken cancellationToken);
    Task<CameraVerificationResult> VerifyTeamsAsync(WindowChoice window, CancellationToken cancellationToken);
    Task<CameraTargetPresentation> ShowTargetAsync(CameraRecoveryTarget target, CancellationToken cancellationToken);
}

internal interface ICameraRecoveryControl
{
    Task<CameraTargetControlResult> ActivateTargetAsync(
        CameraRecoveryTarget target, CancellationToken cancellationToken);
    Task<TeamsRestartResult> RestartTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken);
}

internal sealed class PendingCameraRecoverySensing : ICameraRecoverySensing
{
    private const string Pending =
        "Controls-only camera sensing is not available in this branch yet. This action did not take a screenshot or send data.";

    public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.UnsupportedFallback;
    public void Reset() { }

    public Task<TeamsCameraObservation> ObserveTeamsAsync(WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TeamsCameraObservation(window.Id, TeamsCameraFinding.Unsupported, Pending));
    }

    public Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CameraSettingsObservation(CameraSettingsFinding.Unsupported, Pending));
    }

    public Task<CameraVerificationResult> VerifyTeamsAsync(WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CameraVerificationResult(
            window.Id, CameraVerificationFinding.Unsupported, false, Pending));
    }

    public Task<CameraTargetPresentation> ShowTargetAsync(
        CameraRecoveryTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CameraTargetPresentation(false,
            "A verified target presenter has not been connected. No outline or click was attempted."));
    }
}

internal sealed class FixtureCameraRecoverySensing : ICameraRecoverySensing, ICameraRecoveryControl, ICameraWindowDiscovery
{
    private int teamsObservations;
    private int settingsObservations;
    private int verificationAttempts;
    private bool cameraEnabled;
    private bool permissionFlow;

    public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.Fixture;

    public Task<CameraSurfaceFinding> InspectCameraSurfaceAsync(WindowChoice window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CameraSurfaceFinding.MeetingCamera);
    }

    public void Reset()
    {
        teamsObservations = 0;
        settingsObservations = 0;
        verificationAttempts = 0;
        cameraEnabled = false;
        permissionFlow = false;
    }

    public Task<TeamsCameraObservation> ObserveTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        teamsObservations++;
        if (!cameraEnabled && teamsObservations == 1)
            return Task.FromResult(new TeamsCameraObservation(
                window.Id, TeamsCameraFinding.CameraOff, "Fixture: Teams camera is off.",
                new CameraRecoveryTarget(
                    "fixture-teams-camera-1", CameraRecoveryPinnedTargets.TeamsTurnCameraOn,
                    "fixture-teams-camera-toggle", CameraRecoveryTargetKind.TeamsCameraButton)));
        cameraEnabled = true;
        return Task.FromResult(new TeamsCameraObservation(
            window.Id, TeamsCameraFinding.CameraOn, "Fixture: Teams camera is on."));
    }

    public Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        permissionFlow = true;
        settingsObservations++;
        return Task.FromResult(settingsObservations == 1
            ? new CameraSettingsObservation(CameraSettingsFinding.PermissionOff,
                "Fixture: packaged Teams permission off.",
                new CameraRecoveryTarget("fixture-settings-1", "Microsoft Teams Currently in use",
                    CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle,
                    CameraRecoveryTargetKind.PackagedTeamsPermission),
                CameraSettingsObservationSource.Fixture, ProbeValidated: true,
                Page: CameraSettingsPage.CameraPrivacy)
            : new CameraSettingsObservation(CameraSettingsFinding.PermissionOn, "Fixture: permission on.",
                Source: CameraSettingsObservationSource.Fixture, ProbeValidated: true,
                Page: CameraSettingsPage.CameraPrivacy));
    }

    public Task<CameraVerificationResult> VerifyTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!permissionFlow)
            return Task.FromResult(new CameraVerificationResult(
                window.Id, CameraVerificationFinding.Ready, true,
                "Fixture: simulated Teams camera button is on.",
                IsFixture: true, Reinitialized: true));
        verificationAttempts++;
        return Task.FromResult(verificationAttempts == 1
            ? new CameraVerificationResult(window.Id, CameraVerificationFinding.NeedsReinitialization,
                false, "Fixture: reopen the camera surface.", IsFixture: true)
            : new CameraVerificationResult(window.Id, CameraVerificationFinding.Ready,
                true, "Fixture: simulated verifier passed.", IsFixture: true, Reinitialized: true));
    }

    public Task<CameraTargetPresentation> ShowTargetAsync(
        CameraRecoveryTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CameraTargetPresentation(true, "Fixture: simulated target shown."));
    }

    public Task<CameraTargetControlResult> ActivateTargetAsync(
        CameraRecoveryTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Kind == CameraRecoveryTargetKind.TeamsCameraButton
            && target.ObservationId == "fixture-teams-camera-1")
        {
            cameraEnabled = true;
            return Task.FromResult(new CameraTargetControlResult(
                true, true, "Fixture: simulated Teams camera button invoked."));
        }
        if (target.Kind == CameraRecoveryTargetKind.PackagedTeamsPermission
            && target.AutomationId == CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle)
        {
            permissionFlow = true;
            settingsObservations = Math.Max(settingsObservations, 2);
            return Task.FromResult(new CameraTargetControlResult(
                true, true, "Fixture: simulated Teams permission toggle invoked."));
        }
        return Task.FromResult(new CameraTargetControlResult(
            false, true, "Fixture target changed before the approved action."));
    }

    public Task<TeamsRestartResult> RestartTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        verificationAttempts = Math.Max(verificationAttempts, 1);
        return Task.FromResult(new TeamsRestartResult(
            TeamsRestartFinding.Restarted, "Fixture: simulated Teams restart.", window));
    }
}

internal sealed class CameraRecoverySession
{
    public CameraRecoveryState State { get; private set; } = CameraRecoveryState.Idle;
    public CameraRecoveryInteractionMode Mode { get; private set; }
    public string? TeamsWindowId { get; private set; }
    public string? TeamsWindowTitle { get; private set; }
    public CameraRecoveryTarget? Target { get; private set; }
    public CameraRecoveryTargetKind RecoveryTargetKind { get; private set; }
    public string Detail { get; private set; } = "";
    public bool LocalVerifierPassed { get; private set; }
    public bool TeamsRestartAttempted { get; private set; }
    public bool CameraOnRequestAuthorized { get; private set; }
    private bool globalPermissionRecovery;
    private bool permissionWasRestored;

    public CameraRecoverySession(
        CameraRecoveryInteractionMode mode = CameraRecoveryInteractionMode.Guide)
    {
        Reset(mode);
    }

    public void Reset(CameraRecoveryInteractionMode mode)
    {
        Mode = mode;
        State = CameraRecoveryState.Idle;
        TeamsWindowId = null;
        TeamsWindowTitle = null;
        Target = null;
        RecoveryTargetKind = CameraRecoveryTargetKind.Unknown;
        LocalVerifierPassed = false;
        TeamsRestartAttempted = false;
        CameraOnRequestAuthorized = false;
        globalPermissionRecovery = false;
        permissionWasRestored = false;
        Detail = mode == CameraRecoveryInteractionMode.Control
            ? "Start when you want MSGuide to fix a verified Teams camera control after approval."
            : "Start when you want guide-only help with a Teams camera.";
    }

    internal static async Task<CameraRecoverySession> AssessCurrentAsync(
        ICameraRecoverySensing sensing, WindowChoice window, CameraRecoveryInteractionMode mode,
        CancellationToken cancellationToken)
    {
        var assessment = new CameraRecoverySession(mode);
        assessment.Start();
        assessment.ChooseTeamsWindow(window.Id, window.Title);
        var observation = await sensing.ObserveTeamsAsync(window, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        assessment.ApplyTeamsObservation(observation);
        if (assessment.CanVerifyTeams)
        {
            var verification = await sensing.VerifyTeamsAsync(window, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            assessment.ApplyVerification(verification);
        }
        return assessment;
    }

    internal void ApplyReadOnlyAssessment(CameraRecoverySession assessment)
    {
        Require(CanStart, "A read-only reassessment must start from idle.");
        TeamsWindowId = assessment.TeamsWindowId;
        TeamsWindowTitle = assessment.TeamsWindowTitle;
        if (assessment.LocalVerifierPassed
            && assessment.State is CameraRecoveryState.Ready or CameraRecoveryState.FixtureComplete)
        {
            LocalVerifierPassed = true;
            MoveTo(assessment.State, assessment.Detail);
        }
        else
        {
            MoveTo(CameraRecoveryState.ReadOnlyAssessment,
                assessment.Detail + " Read-only check complete. Submit your camera request to start a fresh repair.");
        }
    }

    public bool CanStart => State == CameraRecoveryState.Idle;
    public bool CanComplete => LocalVerifierPassed
        && State is CameraRecoveryState.Ready or CameraRecoveryState.FixtureComplete;
    public bool CanSelectMode => CanStart || IsTerminal;
    public bool CanInspectTeams => State == CameraRecoveryState.NeedsTeamsObservation
        && !string.IsNullOrWhiteSpace(TeamsWindowId);
    public bool CanOpenSettings => State is CameraRecoveryState.Diagnosis or CameraRecoveryState.NeedsCameraSettings;
    public bool CanInspectSettings => State == CameraRecoveryState.NeedsSettingsObservation;
    public bool CanShowTarget => State == CameraRecoveryState.VerifiedTarget && Target is not null;
    public bool CanCheckChangedSetting => Mode == CameraRecoveryInteractionMode.Guide
        && State == CameraRecoveryState.VerifiedTarget;
    public bool CanControlTarget => Mode == CameraRecoveryInteractionMode.Control
        && State == CameraRecoveryState.VerifiedTarget && Target is not null;
    public bool CanReturnToTeams => State == CameraRecoveryState.PermissionObservedOn;
    public bool CanVerifyTeams => State is (CameraRecoveryState.NeedsLocalVerification
            or CameraRecoveryState.NeedsCameraReinitialization)
        && !string.IsNullOrWhiteSpace(TeamsWindowId);
    public bool CanRestartTeams => Mode == CameraRecoveryInteractionMode.Control
        && State == CameraRecoveryState.NeedsCameraReinitialization
        && !TeamsRestartAttempted
        && !string.IsNullOrWhiteSpace(TeamsWindowId);
    public bool IsTerminal => State is CameraRecoveryState.Ready or CameraRecoveryState.FixtureComplete
        or CameraRecoveryState.ReadOnlyAssessment
        or CameraRecoveryState.ControlRevalidationRequired
        or CameraRecoveryState.WrongSettingsPage or CameraRecoveryState.ManagedOrDisabled
        or CameraRecoveryState.AlreadyOnOrWrongCause or CameraRecoveryState.StaleOrMoved
        or CameraRecoveryState.UnresolvedAfterPermission or CameraRecoveryState.Unsupported
        or CameraRecoveryState.Cancelled;

    public static bool IsCameraHelpIntent(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        string text = prompt.ToLowerInvariant();
        bool mentionsCamera = text.Contains("camera") || text.Contains("webcam") || text.Contains("video");
        bool mentionsTeams = text.Contains("teams") || text.Contains("team's")
            || text.Contains("team\u2019s") || text.Contains("meeting");
        bool asksForHelp = text.Contains("help") || text.Contains("fix") || text.Contains("not working")
            || text.Contains("won't") || text.Contains("cannot") || text.Contains("can't")
            || text.Contains("permission") || text.Contains("blocked") || text.Contains("off")
            || text.Contains("check") || text.Contains("working") || text.Contains("status");
        return mentionsCamera && mentionsTeams && asksForHelp;
    }

    public void Start(bool authorizeCameraOn = false)
    {
        Require(CanStart, "Reset camera recovery before starting another run.");
        State = CameraRecoveryState.NeedsTeamsObservation;
        Target = null;
        RecoveryTargetKind = CameraRecoveryTargetKind.Unknown;
        LocalVerifierPassed = false;
        TeamsRestartAttempted = false;
        CameraOnRequestAuthorized = authorizeCameraOn && Mode == CameraRecoveryInteractionMode.Control;
        Detail = CameraOnRequestAuthorized
            ? "Inspecting Teams first. Your Fix request permits one verified camera-on action; permission changes and restart need separate approval."
            : Mode == CameraRecoveryInteractionMode.Control
            ? "Open a Teams meeting or prejoin screen. MSGuide will inspect first and ask before changing anything."
            : "Open a Teams meeting, prejoin, or Settings > Devices screen, then inspect controls only.";
    }

    public void ChooseTeamsWindow(string windowId, string title)
    {
        if (TeamsWindowId is not null && TeamsWindowId != windowId)
            CameraOnRequestAuthorized = false;
        bool reinitializing = State is CameraRecoveryState.PermissionObservedOn
            or CameraRecoveryState.NeedsLocalVerification
            or CameraRecoveryState.NeedsCameraReinitialization;
        if (reinitializing)
        {
            TeamsWindowId = string.IsNullOrWhiteSpace(windowId) ? null : windowId;
            TeamsWindowTitle = Clean(title);
            Target = null;
            RecoveryTargetKind = CameraRecoveryTargetKind.Unknown;
            LocalVerifierPassed = false;
            State = CameraRecoveryState.NeedsCameraReinitialization;
            Detail = TeamsWindowId is null
                ? "Choose the reopened Teams window before running the private visual check."
                : $"Rebound to “{TeamsWindowTitle}”. Open Teams Devices or prejoin, then run Private visual check.";
            return;
        }
        if (IsTerminal) Reset(Mode);
        if (CanStart) Start();
        TeamsWindowId = string.IsNullOrWhiteSpace(windowId) ? null : windowId;
        TeamsWindowTitle = Clean(title);
        Target = null;
        LocalVerifierPassed = false;
        State = CameraRecoveryState.NeedsTeamsObservation;
        Detail = TeamsWindowId is null
            ? "Choose the exact Teams window before inspection."
            : $"Selected “{TeamsWindowTitle}”. Open its meeting, prejoin, or Settings > Devices camera surface, then inspect.";
    }

    public void ApplyTeamsObservation(TeamsCameraObservation observation)
    {
        bool checkingChangedCamera = State == CameraRecoveryState.VerifiedTarget
            && Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton;
        Require(State == CameraRecoveryState.NeedsTeamsObservation || checkingChangedCamera,
            "Teams observation is not expected now.");
        if (TeamsWindowId is null || observation.WindowId != TeamsWindowId)
        {
            MoveTo(CameraRecoveryState.StaleOrMoved,
                "The Teams window changed or the observation was for another window. Choose it again.");
            return;
        }

        Target = null;
        switch (observation.Finding)
        {
            case TeamsCameraFinding.CameraOff when observation.Target is not null:
                if (observation.Target.Kind != CameraRecoveryTargetKind.TeamsCameraButton
                    || string.IsNullOrWhiteSpace(observation.Target.ObservationId)
                    || !CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(observation.Target.Label)
                        && !CameraRecoveryPinnedTargets.IsTeamsCameraToggle(observation.Target.Label))
                {
                    MoveTo(CameraRecoveryState.Unsupported,
                        "The Teams camera target did not match the pinned camera-on control.");
                    break;
                }
                Target = observation.Target;
                RecoveryTargetKind = CameraRecoveryTargetKind.TeamsCameraButton;
                MoveTo(CameraRecoveryState.VerifiedTarget,
                    CameraOnRequestAuthorized
                        ? "The verified Teams camera is off. Your submitted Fix request authorizes turning it on once, followed by a local readiness check."
                        : Mode == CameraRecoveryInteractionMode.Control
                        ? "The exact Teams camera control is off. Approve the action to let MSGuide turn it on."
                        : "The exact Teams camera control is off. Choose Show me, turn it on, then check again.");
                break;
            case TeamsCameraFinding.CameraOff:
                MoveTo(CameraRecoveryState.Unsupported,
                    "Teams reports the camera off, but no current verified control was supplied.");
                break;
            case TeamsCameraFinding.CameraOn:
                MoveTo(CameraRecoveryState.NeedsLocalVerification,
                    checkingChangedCamera || Mode == CameraRecoveryInteractionMode.Control
                        ? "The Teams camera control is now on. Running a local readiness check is the next step."
                        : "The Teams camera control is on. Run the private visual check to verify readiness.");
                break;
            case TeamsCameraFinding.PermissionMayBeOff:
                globalPermissionRecovery |= observation.PermissionScope is
                    CameraRecoveryTargetKind.DeviceCameraPermission or CameraRecoveryTargetKind.AppCameraPermission;
                MoveTo(CameraRecoveryState.Diagnosis,
                    DetailOr(observation.Detail, "Teams indicates camera permission may be blocking access."));
                break;
            case TeamsCameraFinding.PermissionAlreadyOnOrDifferentCause:
                if (globalPermissionRecovery || permissionWasRestored)
                {
                    MoveTo(CameraRecoveryState.NeedsLocalVerification,
                        "Camera permissions have been restored. Verify the Teams Devices preview locally; permission-on alone is not readiness.");
                    break;
                }
                MoveTo(CameraRecoveryState.AlreadyOnOrWrongCause,
                    "Permission does not appear to be the cause. Do not force the permission path.");
                break;
            case TeamsCameraFinding.ManagedOrDisabled:
                MoveTo(CameraRecoveryState.ManagedOrDisabled,
                    DetailOr(observation.Detail, "Camera access appears disabled or managed. MSGuide will not change policy."));
                break;
            case TeamsCameraFinding.StaleOrMoved:
                MoveTo(CameraRecoveryState.StaleOrMoved,
                    "Teams moved, closed, or changed during observation.");
                break;
            default:
                MoveTo(CameraRecoveryState.Unsupported,
                    DetailOr(observation.Detail,
                        "Controls-only Teams sensing is unavailable or unsupported. No camera-recovery screenshot was captured."));
                break;
        }
    }

    public void PrepareToOpenSettings()
    {
        Require(State is CameraRecoveryState.Diagnosis or CameraRecoveryState.NeedsCameraSettings,
            "Camera Settings is not the next verified step.");
        MoveTo(CameraRecoveryState.NeedsCameraSettings,
            "Open Windows Camera privacy settings. You remain responsible for every setting change.");
    }

    public void MarkSettingsOpened()
    {
        Require(State == CameraRecoveryState.NeedsCameraSettings, "Camera Settings was not expected now.");
        MoveTo(CameraRecoveryState.NeedsSettingsObservation,
            "Windows Settings launch requested. Leave it visible and verify the Camera privacy page before trusting any toggle.");
    }

    public void ApplySettingsObservation(CameraSettingsObservation observation)
    {
        Require(State == CameraRecoveryState.NeedsSettingsObservation
            || State == CameraRecoveryState.VerifiedTarget
                && CameraRecoveryPinnedTargets.IsPermission(RecoveryTargetKind),
            "A Camera Settings observation is not expected now.");
        bool initialSettingsObservation = State == CameraRecoveryState.NeedsSettingsObservation;
        Target = null;
        if (observation.Finding is CameraSettingsFinding.PermissionOff or CameraSettingsFinding.PermissionOn
                or CameraSettingsFinding.ManagedOrDisabled
            && observation.Page != CameraSettingsPage.CameraPrivacy)
        {
            MoveTo(CameraRecoveryState.WrongSettingsPage,
                "Windows Settings did not verify as the Camera privacy page. No permission claim or target was accepted.");
            return;
        }
        if (observation.Finding is CameraSettingsFinding.PermissionOff or CameraSettingsFinding.PermissionOn
                or CameraSettingsFinding.ManagedOrDisabled
            && (!observation.ProbeValidated || observation.Source == CameraSettingsObservationSource.Unknown))
        {
            MoveTo(CameraRecoveryState.Unsupported,
                "Camera Settings sensing lacked a disclosed, successfully probed method. No target or permission claim was accepted.");
            return;
        }
        switch (observation.Finding)
        {
            case CameraSettingsFinding.WrongPage:
                MoveTo(CameraRecoveryState.WrongSettingsPage,
                    "Windows Settings did not verify as the Camera privacy page. No permission claim or target was accepted.");
                break;
            case CameraSettingsFinding.PermissionOff when observation.Target is not null:
                if (!CameraRecoveryPinnedTargets.IsPermissionTarget(
                        observation.Target.AutomationId, observation.Target.Kind)
                    || string.IsNullOrWhiteSpace(observation.Target.ObservationId))
                {
                    MoveTo(CameraRecoveryState.Unsupported,
                        "The Camera Settings target did not match its verified permission scope.");
                    break;
                }
                Target = observation.Target;
                RecoveryTargetKind = observation.Target.Kind;
                globalPermissionRecovery |= RecoveryTargetKind is
                    CameraRecoveryTargetKind.DeviceCameraPermission or CameraRecoveryTargetKind.AppCameraPermission;
                MoveTo(CameraRecoveryState.VerifiedTarget,
                    RecoveryTargetKind != CameraRecoveryTargetKind.PackagedTeamsPermission
                        ? CameraPermissionEvidence.DescribeBlock(RecoveryTargetKind)
                            + (Mode == CameraRecoveryInteractionMode.Control
                                ? " Review this scope, then approve this setting only."
                                : " Choose Show me and change this setting yourself, then check again.")
                        : Mode == CameraRecoveryInteractionMode.Control
                        ? "The exact packaged Teams permission is off. Approve the action to let MSGuide turn on only this toggle."
                        : "A current Camera Settings target was verified. Choose Show me; MSGuide will not click it.");
                break;
            case CameraSettingsFinding.PermissionOff:
                MoveTo(CameraRecoveryState.Unsupported,
                    "Camera permission appears off, but no current verified target was supplied. No highlight or click was attempted.");
                break;
            case CameraSettingsFinding.PermissionOn:
                permissionWasRestored = !initialSettingsObservation || globalPermissionRecovery
                    || CameraOnRequestAuthorized;
                MoveTo(initialSettingsObservation && !globalPermissionRecovery && !CameraOnRequestAuthorized
                        ? CameraRecoveryState.AlreadyOnOrWrongCause
                        : CameraRecoveryState.PermissionObservedOn,
                    globalPermissionRecovery
                        ? CameraOnRequestAuthorized
                            ? "Windows camera permissions are on. Continuing with fresh Teams inspection under your camera repair request."
                            : "Windows camera permissions are now on. Return to Teams for fresh inspection; the Teams camera action needs its own approval."
                        : initialSettingsObservation
                        ? "Camera permission was already on before any guided change. Do not force the permission path."
                        : Mode == CameraRecoveryInteractionMode.Control
                            ? "Camera permission is observed on after the approved action. This alone does not prove the Teams camera is ready."
                            : "Camera permission is observed on after the user's change. This alone does not prove the Teams camera is ready.");
                break;
            case CameraSettingsFinding.ManagedOrDisabled:
                MoveTo(CameraRecoveryState.ManagedOrDisabled,
                    DetailOr(observation.Detail, "The camera setting appears disabled or managed. MSGuide will not override policy."));
                break;
            case CameraSettingsFinding.StaleOrMoved:
                MoveTo(CameraRecoveryState.StaleOrMoved,
                    "Camera Settings moved, closed, or changed during observation.");
                break;
            default:
                MoveTo(CameraRecoveryState.Unsupported,
                    DetailOr(observation.Detail,
                        "Controls-only Camera Settings sensing is unavailable or unsupported. No camera-recovery screenshot was captured."));
                break;
        }
    }

    public void RecordTargetPresentation(CameraTargetPresentation presentation)
    {
        Require(State == CameraRecoveryState.VerifiedTarget, "There is no current verified target to show.");
        Detail = presentation.Shown
            ? Mode == CameraRecoveryInteractionMode.Control
                ? "Verified target shown. Review it, then approve the single action when ready."
                : "Verified target shown. Make the change yourself, then choose I changed it - check."
            : "The verified target could not be shown. No click was attempted.";
        if (RecoveryTargetKind is CameraRecoveryTargetKind.DeviceCameraPermission
                or CameraRecoveryTargetKind.AppCameraPermission)
            Detail = CameraPermissionEvidence.DescribeBlock(RecoveryTargetKind) + " " + Detail;
    }

    public void RecordControlFailure(string detail) =>
        MoveTo(CameraRecoveryState.Unsupported,
            DetailOr(detail, "The approved action could not be completed safely. No retry occurred."));

    internal void RequireControlRevalidation(string detail)
    {
        Target = null;
        MoveTo(CameraRecoveryState.ControlRevalidationRequired,
            DetailOr(detail, "The camera control could not be revalidated. No action was started."));
    }

    public void MarkReturnedToTeams()
    {
        Require(State == CameraRecoveryState.PermissionObservedOn, "Returning to Teams is not the next step.");
        if (globalPermissionRecovery || CameraOnRequestAuthorized)
        {
            MoveTo(CameraRecoveryState.NeedsTeamsObservation,
                CameraOnRequestAuthorized
                    ? "Camera permissions are on. Inspecting Teams again before the camera-on action authorized by your request."
                    : "Camera permissions are on. Inspect Teams again; turning on its camera requires a separate approval.");
            return;
        }
        MoveTo(CameraRecoveryState.NeedsLocalVerification,
            "Back in Teams, run the private visual check. Permission-on alone is not camera-ready.");
    }

    public void ApplyVerification(CameraVerificationResult result)
    {
        Require(State is CameraRecoveryState.NeedsLocalVerification or CameraRecoveryState.NeedsCameraReinitialization,
            "A Teams camera verification is not expected now.");
        if (TeamsWindowId is null || result.WindowId != TeamsWindowId
            || result.Finding == CameraVerificationFinding.StaleOrMoved)
        {
            MoveTo(CameraRecoveryState.StaleOrMoved,
                "Teams moved, closed, or changed before local verification.");
            return;
        }

        LocalVerifierPassed = result.LocalVerifierPassed;
        if (result.Finding == CameraVerificationFinding.Ready && !result.Reinitialized)
        {
            LocalVerifierPassed = false;
            MoveTo(CameraRecoveryState.NeedsCameraReinitialization,
                RecoveryTargetKind == CameraRecoveryTargetKind.PackagedTeamsPermission
                    ? "The verifier did not prove that Teams reinitialized its camera after permission restoration. Reopen prejoin or Teams Devices, or relaunch Teams, then check again."
                    : "The verifier did not prove that Teams started the camera. Reopen prejoin or relaunch Teams, then check again.");
            return;
        }
        if (result.Finding == CameraVerificationFinding.NeedsReinitialization)
        {
            LocalVerifierPassed = false;
            MoveTo(CameraRecoveryState.NeedsCameraReinitialization,
                RecoveryTargetKind == CameraRecoveryTargetKind.PackagedTeamsPermission
                    ? "The existing Teams camera session may have survived the permission change. Reopen prejoin or the camera surface, or approve a Teams restart."
                    : "Teams reports the camera control on, but active camera use was not verified. Reopen the camera surface or approve a Teams restart.");
            return;
        }
        if (result.Finding is CameraVerificationFinding.Ready or CameraVerificationFinding.AlreadyReady
            && result.LocalVerifierPassed)
        {
            MoveTo(result.IsFixture ? CameraRecoveryState.FixtureComplete : CameraRecoveryState.Ready,
                result.IsFixture
                    ? "Fixture complete: the simulated verifier passed. Real camera-ready was not claimed."
                    : result.Finding == CameraVerificationFinding.AlreadyReady
                        ? "Already working: Teams reports camera on and Windows reports active camera use. No change was needed."
                        : "Locally verified: the supplied Teams camera readiness check passed.");
            return;
        }

        LocalVerifierPassed = false;
        MoveTo(result.Finding == CameraVerificationFinding.Unsupported
                ? CameraRecoveryState.Unsupported
                : CameraRecoveryState.UnresolvedAfterPermission,
            result.Finding is CameraVerificationFinding.Ready or CameraVerificationFinding.AlreadyReady
                ? "The verifier did not pass. Camera-ready was not claimed."
                : result.Finding == CameraVerificationFinding.Unsupported
                    ? "The connected local Teams verifier does not support this state. Camera-ready was not claimed."
                    : "The camera control is on, but Teams is still not locally verified ready.");
    }

    public void RecordTeamsRestart(TeamsRestartResult result)
    {
        Require(CanRestartTeams, "A Teams restart is not currently approved.");
        TeamsRestartAttempted = true;
        if (result.Finding == TeamsRestartFinding.Restarted)
        {
            Detail = result.ReopenedWindow is null
                ? "Teams restart was requested. Choose the reopened Teams window, open its camera surface, then verify again."
                : "Teams restarted after separate approval. Verifying the reopened camera surface is the next step.";
            return;
        }
        MoveTo(result.Finding == TeamsRestartFinding.StaleOrMoved
                ? CameraRecoveryState.StaleOrMoved
                : CameraRecoveryState.Unsupported,
            DetailOr(result.Detail, "Teams could not be restarted safely."));
    }

    public void MarkUnsupported(string detail) =>
        MoveTo(CameraRecoveryState.Unsupported, DetailOr(detail, "Camera recovery is unsupported."));

    public void MarkStale(string detail) =>
        MoveTo(CameraRecoveryState.StaleOrMoved, DetailOr(detail, "The observed screen is no longer current."));

    public void Cancel(string detail = "Stopped. MSGuide will perform no further camera actions.")
    {
        Target = null;
        LocalVerifierPassed = false;
        MoveTo(CameraRecoveryState.Cancelled, detail);
    }

    internal bool ConsumeCameraOnRequest()
    {
        if (!CameraOnRequestAuthorized || !CanControlTarget || TeamsWindowId is null
            || Target?.Kind != CameraRecoveryTargetKind.TeamsCameraButton) return false;
        CameraOnRequestAuthorized = false;
        return true;
    }

    private void MoveTo(CameraRecoveryState state, string detail)
    {
        State = state;
        Detail = Clean(detail);
        if (IsTerminal) CameraOnRequestAuthorized = false;
    }

    private static string DetailOr(string detail, string fallback) =>
        string.IsNullOrWhiteSpace(detail) ? fallback : Clean(detail);

    private static string Clean(string? value)
    {
        string clean = (value ?? "").Trim();
        return clean.Length <= 500 ? clean : clean[..500];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
