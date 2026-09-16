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
    public const string TeamsVideoSettings = "VideoSettings";
    public const string PackagedTeamsCameraToggle = "MSTeams_8wekyb3d8bbwe_ToggleSwitch";
    public const string FixturePreparation =
        "Set the packaged Teams camera permission Off before Teams initializes its camera.";
}

internal sealed record TeamsCameraObservation(string WindowId, TeamsCameraFinding Finding, string Detail);
internal sealed record CameraRecoveryTarget(string ObservationId, string Label, string? AutomationId = null);
internal sealed record CameraSettingsObservation(
    CameraSettingsFinding Finding, string Detail, CameraRecoveryTarget? Target = null,
    CameraSettingsObservationSource Source = CameraSettingsObservationSource.Unknown,
    bool ProbeValidated = false, CameraSettingsPage Page = CameraSettingsPage.Unknown);
internal sealed record CameraVerificationResult(
    string WindowId, CameraVerificationFinding Finding, bool LocalVerifierPassed, string Detail,
    bool IsFixture = false, bool Reinitialized = false);
internal sealed record CameraTargetPresentation(bool Shown, string Detail);

internal interface ICameraRecoverySensing
{
    CameraRecoverySensingMode Mode { get; }
    // Stay rooted to window.Handle, but do not require WebView descendants to share its process ID.
    Task<TeamsCameraObservation> ObserveTeamsAsync(WindowChoice window, CancellationToken cancellationToken);
    // Supported findings must identify a disclosed observation source that has passed a live probe.
    Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken cancellationToken);
    Task<CameraVerificationResult> VerifyTeamsAsync(WindowChoice window, CancellationToken cancellationToken);
    Task<CameraTargetPresentation> ShowTargetAsync(CameraRecoveryTarget target, CancellationToken cancellationToken);
}

internal sealed class PendingCameraRecoverySensing : ICameraRecoverySensing
{
    private const string Pending =
        "Controls-only camera sensing is not available in this branch yet. This action did not take a screenshot or send data.";

    public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.UnsupportedFallback;

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

internal sealed class FixtureCameraRecoverySensing : ICameraRecoverySensing
{
    private int settingsObservations;
    private int verificationAttempts;

    public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.Fixture;

    public Task<TeamsCameraObservation> ObserveTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TeamsCameraObservation(window.Id,
            TeamsCameraFinding.PermissionMayBeOff, "Fixture: permission diagnosis."));
    }

    public Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsObservations++;
        return Task.FromResult(settingsObservations == 1
            ? new CameraSettingsObservation(CameraSettingsFinding.PermissionOff,
                "Fixture: packaged Teams permission off.",
                new CameraRecoveryTarget("fixture-settings-1", "Microsoft Teams Currently in use",
                    CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle),
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
}

internal sealed class CameraRecoverySession
{
    public CameraRecoveryState State { get; private set; } = CameraRecoveryState.Idle;
    public string? TeamsWindowId { get; private set; }
    public string? TeamsWindowTitle { get; private set; }
    public CameraRecoveryTarget? Target { get; private set; }
    public string Detail { get; private set; } = "Start when you want guide-only help with a Teams camera.";
    public bool LocalVerifierPassed { get; private set; }

    public bool CanInspectTeams => State == CameraRecoveryState.NeedsTeamsObservation
        && !string.IsNullOrWhiteSpace(TeamsWindowId);
    public bool CanOpenSettings => State is CameraRecoveryState.Diagnosis or CameraRecoveryState.NeedsCameraSettings;
    public bool CanInspectSettings => State == CameraRecoveryState.NeedsSettingsObservation;
    public bool CanShowTarget => State == CameraRecoveryState.VerifiedTarget && Target is not null;
    public bool CanCheckChangedSetting => State == CameraRecoveryState.VerifiedTarget;
    public bool CanReturnToTeams => State == CameraRecoveryState.PermissionObservedOn;
    public bool CanVerifyTeams => State is (CameraRecoveryState.NeedsLocalVerification
            or CameraRecoveryState.NeedsCameraReinitialization)
        && !string.IsNullOrWhiteSpace(TeamsWindowId);
    public bool IsTerminal => State is CameraRecoveryState.Ready or CameraRecoveryState.FixtureComplete
        or CameraRecoveryState.WrongSettingsPage or CameraRecoveryState.ManagedOrDisabled
        or CameraRecoveryState.AlreadyOnOrWrongCause or CameraRecoveryState.StaleOrMoved
        or CameraRecoveryState.UnresolvedAfterPermission or CameraRecoveryState.Unsupported
        or CameraRecoveryState.Cancelled;

    public static bool IsCameraHelpIntent(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        string text = prompt.ToLowerInvariant();
        bool mentionsCamera = text.Contains("camera") || text.Contains("webcam") || text.Contains("video");
        bool mentionsTeams = text.Contains("teams") || text.Contains("meeting");
        bool asksForHelp = text.Contains("help") || text.Contains("fix") || text.Contains("not working")
            || text.Contains("won't") || text.Contains("cannot") || text.Contains("can't")
            || text.Contains("permission") || text.Contains("blocked") || text.Contains("off");
        return mentionsCamera && mentionsTeams && asksForHelp;
    }

    public void Start()
    {
        State = CameraRecoveryState.NeedsTeamsObservation;
        Target = null;
        LocalVerifierPassed = false;
        Detail = "Open Teams Settings > Devices, choose that Teams window, then inspect controls only.";
    }

    public void ChooseTeamsWindow(string windowId, string title)
    {
        bool reinitializing = State is CameraRecoveryState.PermissionObservedOn
            or CameraRecoveryState.NeedsLocalVerification
            or CameraRecoveryState.NeedsCameraReinitialization;
        if (reinitializing)
        {
            TeamsWindowId = string.IsNullOrWhiteSpace(windowId) ? null : windowId;
            TeamsWindowTitle = Clean(title);
            Target = null;
            LocalVerifierPassed = false;
            State = CameraRecoveryState.NeedsCameraReinitialization;
            Detail = TeamsWindowId is null
                ? "Choose the reopened Teams window before running the private visual check."
                : $"Rebound to “{TeamsWindowTitle}”. Open Teams Devices or prejoin, then run Private visual check.";
            return;
        }
        if (State is CameraRecoveryState.Idle or CameraRecoveryState.Cancelled || IsTerminal) Start();
        TeamsWindowId = string.IsNullOrWhiteSpace(windowId) ? null : windowId;
        TeamsWindowTitle = Clean(title);
        Target = null;
        LocalVerifierPassed = false;
        State = CameraRecoveryState.NeedsTeamsObservation;
        Detail = TeamsWindowId is null
            ? "Choose the exact Teams window before inspection."
            : $"Selected “{TeamsWindowTitle}”. Open Settings > Devices, then inspect controls only.";
    }

    public void ApplyTeamsObservation(TeamsCameraObservation observation)
    {
        Require(State == CameraRecoveryState.NeedsTeamsObservation, "Teams observation is not expected now.");
        if (TeamsWindowId is null || observation.WindowId != TeamsWindowId)
        {
            MoveTo(CameraRecoveryState.StaleOrMoved,
                "The Teams window changed or the observation was for another window. Choose it again.");
            return;
        }

        switch (observation.Finding)
        {
            case TeamsCameraFinding.PermissionMayBeOff:
                MoveTo(CameraRecoveryState.Diagnosis,
                    "Teams indicates camera permission may be blocking access.");
                break;
            case TeamsCameraFinding.PermissionAlreadyOnOrDifferentCause:
                MoveTo(CameraRecoveryState.AlreadyOnOrWrongCause,
                    "Permission does not appear to be the cause. Do not force the permission path.");
                break;
            case TeamsCameraFinding.ManagedOrDisabled:
                MoveTo(CameraRecoveryState.ManagedOrDisabled,
                    "Camera access appears disabled or managed. MSGuide will not change policy.");
                break;
            case TeamsCameraFinding.StaleOrMoved:
                MoveTo(CameraRecoveryState.StaleOrMoved,
                    "Teams moved, closed, or changed during observation.");
                break;
            default:
                MoveTo(CameraRecoveryState.Unsupported,
                    "Controls-only Teams sensing is unavailable or unsupported. No camera-recovery screenshot was captured.");
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
        Require(State is CameraRecoveryState.NeedsSettingsObservation or CameraRecoveryState.VerifiedTarget,
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
                if (observation.Target.AutomationId != CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle
                    || string.IsNullOrWhiteSpace(observation.Target.ObservationId))
                {
                    MoveTo(CameraRecoveryState.Unsupported,
                        "The Camera Settings target did not match the pinned Teams permission control.");
                    break;
                }
                Target = observation.Target;
                MoveTo(CameraRecoveryState.VerifiedTarget,
                    "A current Camera Settings target was verified. Choose Show me; MSGuide will not click it.");
                break;
            case CameraSettingsFinding.PermissionOff:
                MoveTo(CameraRecoveryState.Unsupported,
                    "Camera permission appears off, but no current verified target was supplied. No highlight or click was attempted.");
                break;
            case CameraSettingsFinding.PermissionOn:
                MoveTo(initialSettingsObservation
                        ? CameraRecoveryState.AlreadyOnOrWrongCause
                        : CameraRecoveryState.PermissionObservedOn,
                    initialSettingsObservation
                        ? "Camera permission was already on before any guided change. Do not force the permission path."
                        : "Camera permission is observed on after the user's change. This alone does not prove the Teams camera is ready.");
                break;
            case CameraSettingsFinding.ManagedOrDisabled:
                MoveTo(CameraRecoveryState.ManagedOrDisabled,
                    "The camera setting appears disabled or managed. MSGuide will not override policy.");
                break;
            case CameraSettingsFinding.StaleOrMoved:
                MoveTo(CameraRecoveryState.StaleOrMoved,
                    "Camera Settings moved, closed, or changed during observation.");
                break;
            default:
                MoveTo(CameraRecoveryState.Unsupported,
                    "Controls-only Camera Settings sensing is unavailable or unsupported. No camera-recovery screenshot was captured.");
                break;
        }
    }

    public void RecordTargetPresentation(CameraTargetPresentation presentation)
    {
        Require(State == CameraRecoveryState.VerifiedTarget, "There is no current verified target to show.");
        Detail = presentation.Shown
            ? "Verified target shown. Make the change yourself, then choose I changed it - check."
            : "The verified target could not be shown. No click was attempted.";
    }

    public void MarkReturnedToTeams()
    {
        Require(State == CameraRecoveryState.PermissionObservedOn, "Returning to Teams is not the next step.");
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
                "The verifier did not prove that Teams reinitialized its camera after permission restoration. Reopen prejoin or Teams Devices, or relaunch Teams yourself, then check again.");
            return;
        }
        if (result.Finding == CameraVerificationFinding.NeedsReinitialization)
        {
            LocalVerifierPassed = false;
            MoveTo(CameraRecoveryState.NeedsCameraReinitialization,
                "The existing Teams camera session may have survived the permission change. Reopen prejoin or the camera surface, or relaunch Teams yourself, then run Private visual check again.");
            return;
        }
        if (result.Finding == CameraVerificationFinding.Ready && result.LocalVerifierPassed)
        {
            MoveTo(result.IsFixture ? CameraRecoveryState.FixtureComplete : CameraRecoveryState.Ready,
                result.IsFixture
                    ? "Fixture complete: the simulated verifier passed. Real camera-ready was not claimed."
                    : "Locally verified: the supplied Teams camera readiness check passed.");
            return;
        }

        LocalVerifierPassed = false;
        MoveTo(result.Finding == CameraVerificationFinding.Unsupported
                ? CameraRecoveryState.Unsupported
                : CameraRecoveryState.UnresolvedAfterPermission,
            result.Finding == CameraVerificationFinding.Ready
                ? "The verifier did not pass. Camera-ready was not claimed."
                : result.Finding == CameraVerificationFinding.Unsupported
                    ? "The connected local Teams verifier does not support this state. Camera-ready was not claimed."
                    : "Camera permission is on, but Teams is still not locally verified ready.");
    }

    public void MarkUnsupported(string detail) =>
        MoveTo(CameraRecoveryState.Unsupported, DetailOr(detail, "Camera recovery is unsupported."));

    public void MarkStale(string detail) =>
        MoveTo(CameraRecoveryState.StaleOrMoved, DetailOr(detail, "The observed screen is no longer current."));

    public void Cancel(string detail = "Stopped. No settings or Teams controls were changed by MSGuide.")
    {
        Target = null;
        LocalVerifierPassed = false;
        MoveTo(CameraRecoveryState.Cancelled, detail);
    }

    private void MoveTo(CameraRecoveryState state, string detail)
    {
        State = state;
        Detail = Clean(detail);
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
