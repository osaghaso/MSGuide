namespace MSGuide.Desktop;

internal static class CameraRecoveryTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Camera recovery test failed: {name}.");
        }

        RunPermissionChecks();
        RunCurrentStateChecks();
        Check(CameraRecoverySession.IsCameraHelpIntent("Help me fix my camera in Teams"), "typed intent");
        Check(CameraRecoverySession.IsCameraHelpIntent("Is my camera working in Teams?"), "camera status question");
        Check(CameraRecoverySession.IsCameraHelpIntent("Check my team's camera."),
            "natural possessive speech transcript routes to camera help without changing the words");
        Check(CameraRecoverySession.IsCameraHelpIntent("My meeting video is not working"), "meeting video intent");
        Check(!CameraRecoverySession.IsCameraHelpIntent("Help me find the build error"), "unrelated intent");
        ElementInfo[] partialControls =
        [
            new("group", "Video settings", [0.1, 0.1, 0.2, 0.2],
                AutomationId: CameraRecoveryPinnedTargets.TeamsVideoSettings),
            new("button", "Turn camera on (Ctrl+Shift+O)", [0.4, 0.1, 0.2, 0.2],
                ToggleState: "off")
        ];
        Check(new AutomationReadResult(partialControls, "", "", true).RequireComplete() == partialControls,
            "complete camera controls remain usable");
        bool incompleteRejected = false;
        try { new AutomationReadResult(partialControls, "", "", false).RequireComplete(); }
        catch (IncompleteAutomationReadException) { incompleteRejected = true; }
        Check(incompleteRejected,
            "incomplete controls cannot authorize targets or permission fallback");
        Check(CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(
                "Turn camera on (Ctrl+Shift+O)")
            && CameraRecoveryPinnedTargets.IsTeamsTurnCameraOff(
                "Turn camera off (Ctrl+Shift+O)")
            && !CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(
                "Turn camera on and share the screen")
            && !CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(
                "Turn camera on (for everyone)"),
            "Teams camera shortcut suffix normalization");

        var pendingSensing = new PendingCameraRecoverySensing();
        var pendingResult = pendingSensing.ObserveTeamsAsync(
            new WindowChoice(0, 0, "Synthetic Teams"), CancellationToken.None).GetAwaiter().GetResult();
        Check(pendingSensing.Mode == CameraRecoverySensingMode.UnsupportedFallback
            && pendingResult.Finding == TeamsCameraFinding.Unsupported
            && pendingResult.Detail.Contains("did not take a screenshot", StringComparison.Ordinal),
            "explicit unsupported fallback");

        var cameraControlSession =
            new CameraRecoverySession(CameraRecoveryInteractionMode.Control);
        cameraControlSession.Start();
        cameraControlSession.ChooseTeamsWindow("teams-camera", "Meeting | Microsoft Teams");
        cameraControlSession.ApplyTeamsObservation(new(
            "teams-camera", TeamsCameraFinding.CameraOff, "",
            new CameraRecoveryTarget(
                "teams-camera-target", "Turn camera on (Ctrl+Shift+O)",
                "camera-button", CameraRecoveryTargetKind.TeamsCameraButton)));
        Check(cameraControlSession.State == CameraRecoveryState.VerifiedTarget
            && cameraControlSession.CanControlTarget
            && !cameraControlSession.CanCheckChangedSetting,
            "control mode gates exact Teams camera action");
        cameraControlSession.ApplyTeamsObservation(new(
            "teams-camera", TeamsCameraFinding.CameraOn, ""));
        Check(cameraControlSession.State == CameraRecoveryState.NeedsLocalVerification,
            "camera-on observation requires readiness verification");
        cameraControlSession.ApplyVerification(new(
            "teams-camera", CameraVerificationFinding.NeedsReinitialization, false, ""));
        Check(cameraControlSession.CanRestartTeams, "restart requires separate approval state");
        cameraControlSession.RecordTeamsRestart(new(
            TeamsRestartFinding.Restarted, "", new WindowChoice(0, 0, "Microsoft Teams")));
        Check(!cameraControlSession.CanRestartTeams
            && cameraControlSession.TeamsRestartAttempted,
            "restart approval is single-use");

        var session = new CameraRecoverySession();
        Check(session.State == CameraRecoveryState.Idle && !session.LocalVerifierPassed
            && session.CanStart && session.CanSelectMode, "idle mode selection");
        session.Start();
        Check(!session.CanStart && !session.CanSelectMode, "active recovery locks mode selection");
        session.ChooseTeamsWindow("teams-1", "Weekly meeting | Microsoft Teams");
        Check(session.State == CameraRecoveryState.NeedsTeamsObservation && session.CanInspectTeams, "choose Teams");
        session.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.PermissionMayBeOff, ""));
        Check(session.State == CameraRecoveryState.Diagnosis && session.CanOpenSettings, "diagnosis");
        session.PrepareToOpenSettings();
        session.MarkSettingsOpened();
        Check(session.State == CameraRecoveryState.NeedsSettingsObservation && session.CanInspectSettings, "settings opened");
        session.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOff, "",
            new CameraRecoveryTarget("settings-observation-1", "Microsoft Teams Currently in use",
                CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle,
                CameraRecoveryTargetKind.PackagedTeamsPermission),
            CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(session.State == CameraRecoveryState.VerifiedTarget && session.CanShowTarget
            && session.CanCheckChangedSetting
            && !session.CanControlTarget
            && session.Target?.AutomationId == CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle,
            "verified target");
        session.RecordTargetPresentation(new(true, ""));
        session.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOn, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(session.State == CameraRecoveryState.PermissionObservedOn && !session.LocalVerifierPassed
            && session.CanReturnToTeams, "permission alone not ready");
        session.MarkReturnedToTeams();
        session.ApplyVerification(new("teams-1", CameraVerificationFinding.Ready, true, ""));
        Check(session.State == CameraRecoveryState.NeedsCameraReinitialization
            && !session.LocalVerifierPassed, "first verification requires camera reinitialization");
        session.ApplyVerification(new("teams-1", CameraVerificationFinding.Ready, true, "",
            Reinitialized: true));
        Check(session.State == CameraRecoveryState.Ready && session.LocalVerifierPassed
            && session.CanSelectMode, "local verifier ready");

        var falseReady = PermissionOnSession();
        falseReady.ApplyVerification(new("teams-1", CameraVerificationFinding.NeedsReinitialization,
            false, ""));
        falseReady.ApplyVerification(new("teams-1", CameraVerificationFinding.Ready, false, "",
            Reinitialized: true));
        Check(falseReady.State == CameraRecoveryState.UnresolvedAfterPermission
            && !falseReady.LocalVerifierPassed, "false verifier cannot claim ready");

        var unresolved = PermissionOnSession();
        unresolved.ApplyVerification(new("teams-1", CameraVerificationFinding.Unresolved, false, ""));
        Check(unresolved.State == CameraRecoveryState.UnresolvedAfterPermission, "unresolved after permission");

        var reinitialize = PermissionOnSession();
        reinitialize.ApplyVerification(new("teams-1",
            CameraVerificationFinding.NeedsReinitialization, false, ""));
        Check(reinitialize.State == CameraRecoveryState.NeedsCameraReinitialization
            && reinitialize.CanVerifyTeams && !reinitialize.LocalVerifierPassed,
            "live camera session requires reinitialization");
        reinitialize.ApplyVerification(new("teams-1", CameraVerificationFinding.Ready, true, "",
            Reinitialized: true));
        Check(reinitialize.State == CameraRecoveryState.Ready, "ready after explicit reinitialization");

        var alreadyOn = StartedSession();
        alreadyOn.ApplyTeamsObservation(new("teams-1",
            TeamsCameraFinding.PermissionAlreadyOnOrDifferentCause, "Camera ready"));
        Check(alreadyOn.State == CameraRecoveryState.AlreadyOnOrWrongCause
            && !alreadyOn.Detail.Contains("Camera ready", StringComparison.OrdinalIgnoreCase),
            "already on wrong cause cannot inject ready claim");

        var managed = StartedSession();
        managed.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.ManagedOrDisabled, ""));
        Check(managed.State == CameraRecoveryState.ManagedOrDisabled, "managed");

        var stale = StartedSession();
        stale.ApplyTeamsObservation(new("other-window", TeamsCameraFinding.PermissionMayBeOff, ""));
        Check(stale.State == CameraRecoveryState.StaleOrMoved, "stale target");

        var unsupported = StartedSession();
        unsupported.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.Unsupported, ""));
        Check(unsupported.State == CameraRecoveryState.Unsupported
            && unsupported.Detail.Contains("No camera-recovery screenshot", StringComparison.Ordinal),
            "unsupported");
        var unsupportedDetail = StartedSession();
        unsupportedDetail.ApplyTeamsObservation(new(
            "teams-1", TeamsCameraFinding.Unsupported,
            "Open the selected Teams prejoin before inspecting again."));
        Check(unsupportedDetail.Detail == "Open the selected Teams prejoin before inspecting again.",
            "unsupported observation detail remains visible");

        var settingsManaged = SettingsSession();
        settingsManaged.ApplySettingsObservation(new(CameraSettingsFinding.ManagedOrDisabled, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(settingsManaged.State == CameraRecoveryState.ManagedOrDisabled, "settings managed");

        var settingsStale = SettingsSession();
        settingsStale.ApplySettingsObservation(new(CameraSettingsFinding.StaleOrMoved, ""));
        Check(settingsStale.State == CameraRecoveryState.StaleOrMoved, "settings stale");

        var unprovenSettings = SettingsSession();
        unprovenSettings.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOn, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: false,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(unprovenSettings.State == CameraRecoveryState.Unsupported
            && unprovenSettings.Detail.Contains("successfully probed method", StringComparison.Ordinal),
            "unproven Settings UIA rejected");

        var wrongSettingsPage = SettingsSession();
        wrongSettingsPage.ApplySettingsObservation(new(CameraSettingsFinding.WrongPage, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.Other));
        Check(wrongSettingsPage.State == CameraRecoveryState.WrongSettingsPage
            && !wrongSettingsPage.LocalVerifierPassed, "Settings URI landing verified");

        var wrongPinnedTarget = SettingsSession();
        wrongPinnedTarget.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOff, "",
            new CameraRecoveryTarget("wrong", "Another app", "wrong-toggle"),
            CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(wrongPinnedTarget.State == CameraRecoveryState.Unsupported,
            "wrong pinned target rejected");

        var alreadyOnInSettings = SettingsSession();
        alreadyOnInSettings.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOn, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(alreadyOnInSettings.State == CameraRecoveryState.AlreadyOnOrWrongCause
            && !alreadyOnInSettings.CanReturnToTeams, "initial Settings permission already on");

        var cancelled = StartedSession();
        cancelled.Cancel();
        Check(cancelled.State == CameraRecoveryState.Cancelled && !cancelled.LocalVerifierPassed
            && cancelled.CanSelectMode, "cancelled mode selection");
        cancelled.ChooseTeamsWindow("teams-2", "New Microsoft Teams window");
        Check(cancelled.State == CameraRecoveryState.NeedsTeamsObservation
            && cancelled.TeamsWindowId == "teams-2" && !cancelled.CanSelectMode,
            "terminal window selection resets before restarting");

        var rebind = PermissionOnSession();
        rebind.ApplyVerification(new("teams-1", CameraVerificationFinding.NeedsReinitialization,
            false, ""));
        rebind.ChooseTeamsWindow("teams-2", "Reopened Microsoft Teams");
        Check(rebind.State == CameraRecoveryState.NeedsCameraReinitialization
            && rebind.TeamsWindowId == "teams-2" && rebind.CanVerifyTeams,
            "reopened Teams window can be rebound without losing permission evidence");

        bool invalidTransitionRejected = false;
        try { new CameraRecoverySession().MarkReturnedToTeams(); }
        catch (InvalidOperationException) { invalidTransitionRejected = true; }
        Check(invalidTransitionRejected, "invalid transition");

        var fixture = new FixtureCameraRecoverySensing();
        var fixtureWindow = new WindowChoice(0, 0, "Synthetic Teams fixture");
        var fixtureSession =
            new CameraRecoverySession(CameraRecoveryInteractionMode.Control);
        fixtureSession.Start();
        fixtureSession.ChooseTeamsWindow(fixtureWindow.Id, fixtureWindow.Title);
        fixtureSession.ApplyTeamsObservation(
            fixture.ObserveTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        Check(fixtureSession.CanControlTarget
            && fixtureSession.Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton,
            "fixture starts with Teams camera off");
        Check(fixture.ActivateTargetAsync(
                fixtureSession.Target!, CancellationToken.None).GetAwaiter().GetResult().Invoked,
            "fixture control action");
        fixtureSession.ApplyTeamsObservation(
            fixture.ObserveTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        fixtureSession.ApplyVerification(
            fixture.VerifyTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        Check(fixture.Mode == CameraRecoverySensingMode.Fixture
            && fixtureSession.State == CameraRecoveryState.FixtureComplete
            && fixtureSession.CanSelectMode
            && fixtureSession.Detail.Contains("not claimed", StringComparison.OrdinalIgnoreCase),
            "deterministic camera-button fixture path");
        fixtureSession.Reset(CameraRecoveryInteractionMode.Guide);
        Check(fixtureSession.State == CameraRecoveryState.Idle
            && fixtureSession.Mode == CameraRecoveryInteractionMode.Guide
            && fixtureSession.CanStart && fixtureSession.CanSelectMode
            && fixtureSession.TeamsWindowId is null && fixtureSession.Target is null
            && !fixtureSession.LocalVerifierPassed && !fixtureSession.TeamsRestartAttempted,
            "start over returns to editable mode state");

        fixture.Reset();
        var permissionFixtureSession = new CameraRecoverySession();
        permissionFixtureSession.Start();
        permissionFixtureSession.ChooseTeamsWindow(fixtureWindow.Id, fixtureWindow.Title);
        permissionFixtureSession.ApplyTeamsObservation(new(
            fixtureWindow.Id, TeamsCameraFinding.PermissionMayBeOff, ""));
        permissionFixtureSession.PrepareToOpenSettings();
        permissionFixtureSession.MarkSettingsOpened();
        permissionFixtureSession.ApplySettingsObservation(
            fixture.ObserveSettingsAsync(CancellationToken.None).GetAwaiter().GetResult());
        permissionFixtureSession.ApplySettingsObservation(
            fixture.ObserveSettingsAsync(CancellationToken.None).GetAwaiter().GetResult());
        permissionFixtureSession.MarkReturnedToTeams();
        permissionFixtureSession.ApplyVerification(
            fixture.VerifyTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        Check(permissionFixtureSession.State == CameraRecoveryState.NeedsCameraReinitialization,
            "permission fixture still requires camera reinitialization");

        fixture.Reset();
        Check(fixture.ObserveTeamsAsync(
                fixtureWindow, CancellationToken.None).GetAwaiter().GetResult().Finding
            == TeamsCameraFinding.CameraOff, "fixture restarts from Teams camera off");
    }

    private static void RunCurrentStateChecks()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Current camera state test failed: {name}.");
        }
        var now = DateTimeOffset.UtcNow;
        var already = LiveCameraRecoverySensing.MatchActiveCameraUse("teams-1", null, now.AddMinutes(-5));
        Check(already is { Finding: CameraVerificationFinding.AlreadyReady, LocalVerifierPassed: true, Reinitialized: false },
            "current active use does not require this session to turn the camera on");
        Check(LiveCameraRecoverySensing.MatchActiveCameraUse("teams-1", null, null) is null,
            "camera-on without active-use evidence is not ready");
        Check(LiveCameraRecoverySensing.MatchActiveCameraUse("teams-1", now, now.AddMinutes(-5)) is null,
            "a new repair still requires camera use after that repair");
        Check(LiveCameraRecoverySensing.MatchActiveCameraUse("teams-1", now, now)
                is { Finding: CameraVerificationFinding.Ready, Reinitialized: true },
            "fresh repaired camera use remains supported");

        var falseReady = StartedSession();
        falseReady.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.CameraOn, ""));
        falseReady.ApplyVerification(already! with { LocalVerifierPassed = false });
        Check(!falseReady.LocalVerifierPassed && falseReady.State != CameraRecoveryState.Ready,
            "already-ready still requires a passed current verifier");

        var window = new WindowChoice(0, 0, "Synthetic current Teams camera");
        foreach (var mode in new[] { CameraRecoveryInteractionMode.Guide, CameraRecoveryInteractionMode.Control })
        {
            var sensing = new CurrentStateSensing();
            var session = new CameraRecoverySession(mode);
            for (int round = 0; round < 2; round++)
            {
                session.Reset(mode);
                var assessment = CameraRecoverySession.AssessCurrentAsync(
                    sensing, window, mode, CancellationToken.None).GetAwaiter().GetResult();
                session.ApplyReadOnlyAssessment(assessment);
                Check(session.State == CameraRecoveryState.Ready && session.CanSelectMode
                    && !session.CanControlTarget && !session.CanRestartTeams
                    && session.Detail.StartsWith("Already working:", StringComparison.Ordinal),
                    "start over recognizes current readiness without authorizing a change");
            }
            Check(sensing.Observations == 2 && sensing.Verifications == 2,
                "every reassessment obtains fresh observations and verification");
            sensing.CameraOn = false;
            session.Reset(mode);
            session.ApplyReadOnlyAssessment(CameraRecoverySession.AssessCurrentAsync(
                sensing, window, mode, CancellationToken.None).GetAwaiter().GetResult());
            Check(session.State == CameraRecoveryState.ReadOnlyAssessment && session.CanSelectMode
                && session.Target is null && !session.CanControlTarget && !session.CanShowTarget
                && !session.LocalVerifierPassed,
                "an unresolved reassessment keeps a visible terminal result, editable modes, and no action authority");
        }
    }

    private sealed class CurrentStateSensing : ICameraRecoverySensing
    {
        public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.Connected;
        public bool CameraOn { get; set; } = true;
        public int Observations { get; private set; }
        public int Verifications { get; private set; }
        public void Reset() { }

        public Task<TeamsCameraObservation> ObserveTeamsAsync(WindowChoice window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Observations++;
            return Task.FromResult(CameraOn
                ? new TeamsCameraObservation(window.Id, TeamsCameraFinding.CameraOn, "")
                : new TeamsCameraObservation(window.Id, TeamsCameraFinding.CameraOff, "",
                    new("current-camera", "Turn camera on", "camera", CameraRecoveryTargetKind.TeamsCameraButton)));
        }

        public Task<CameraVerificationResult> VerifyTeamsAsync(WindowChoice window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Verifications++;
            return Task.FromResult(new CameraVerificationResult(
                window.Id, CameraVerificationFinding.AlreadyReady, true, ""));
        }

        public Task<CameraSettingsObservation> ObserveSettingsAsync(CancellationToken token) =>
            throw new InvalidOperationException("Current camera reassessment must not navigate to Settings.");

        public Task<CameraTargetPresentation> ShowTargetAsync(CameraRecoveryTarget target, CancellationToken token) =>
            throw new InvalidOperationException("Current camera reassessment must not present an actionable target.");
    }

    private static void RunPermissionChecks()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Camera permission test failed: {name}.");
        }

        var deviceBlock = CameraPermissionEvidence.FromConsent(false, "Deny", "Allow", "Allow");
        Check(deviceBlock.State == CameraPermissionState.Off
            && deviceBlock.Scope == CameraRecoveryTargetKind.DeviceCameraPermission,
            "device-wide denial overrides Teams Allow");
        Check(CameraPermissionEvidence.FromConsent(true, "Allow", "Allow", "Allow").State
                == CameraPermissionState.Managed,
            "policy denial is never offered as a fix");
        Check(CameraPermissionEvidence.FromConsent(false, null, "Allow", "Allow").State
                == CameraPermissionState.Unknown,
            "missing device evidence is not permission-on");
        Check(CameraPermissionEvidence.FromConsent(false, "Allow", "Deny", "Allow").Scope
                == CameraRecoveryTargetKind.AppCameraPermission,
            "app-level denial overrides Teams Allow");
        Check(CameraPermissionEvidence.FromConsent(false, "Allow", "Allow", "Allow").State
                == CameraPermissionState.On,
            "all permission levels must allow access");

        var device = new ElementInfo("button", "Camera access", [0.1, 0.1, 0.2, 0.1],
            TargetId: "device-off", AutomationId: CameraRecoveryPinnedTargets.DeviceCameraToggle,
            ToggleState: "off");
        var apps = new ElementInfo("button", "Let apps access your camera", [0.1, 0.3, 0.2, 0.1],
            TargetId: "apps-off", AutomationId: CameraRecoveryPinnedTargets.AppCameraToggle,
            IsEnabled: false, Targetable: false, ToggleState: "off");
        var teams = new ElementInfo("button", "Microsoft Teams", [0.1, 0.5, 0.2, 0.1],
            TargetId: "teams-off", AutomationId: CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle,
            IsEnabled: false, Targetable: false, ToggleState: "off");
        var deviceObservation = LiveCameraRecoverySensing.AssessSettingsPermissions([device, apps, teams], false);
        Check(deviceObservation.Target?.Kind == CameraRecoveryTargetKind.DeviceCameraPermission,
            "disabled child toggles do not masquerade as policy when device access is off");
        Check(LiveCameraRecoverySensing.AssessSettingsPermissions([device, apps, teams], true)
                .Finding == CameraSettingsFinding.ManagedOrDisabled,
            "policy disables permission approvals");
        Check(LiveCameraRecoverySensing.AssessSettingsPermissions(
                [device with { IsEnabled = false }, apps, teams], false).Target is null,
            "disabled global toggle is never offered for execution");

        device = device with { ToggleState = "on" };
        apps = apps with { IsEnabled = true, Targetable = true };
        var appsObservation = LiveCameraRecoverySensing.AssessSettingsPermissions([device, apps, teams], false);
        Check(appsObservation.Target?.Kind == CameraRecoveryTargetKind.AppCameraPermission,
            "app access is a distinct next scope");
        apps = apps with { ToggleState = "on" };
        teams = teams with { IsEnabled = true, Targetable = true };
        var teamsObservation = LiveCameraRecoverySensing.AssessSettingsPermissions([device, apps, teams], false);
        Check(teamsObservation.Target?.Kind == CameraRecoveryTargetKind.PackagedTeamsPermission,
            "Teams permission is a distinct next scope");
        teams = teams with { ToggleState = "on" };
        var allowedObservation = LiveCameraRecoverySensing.AssessSettingsPermissions([device, apps, teams], false);
        Check(allowedObservation.Finding == CameraSettingsFinding.PermissionOn, "all visible permissions are on");

        foreach (var mode in new[] { CameraRecoveryInteractionMode.Guide, CameraRecoveryInteractionMode.Control })
        {
            var session = new CameraRecoverySession(mode);
            session.Start();
            session.ChooseTeamsWindow("teams-global", "Synthetic Teams");
            session.ApplyTeamsObservation(new("teams-global", TeamsCameraFinding.PermissionMayBeOff,
                deviceBlock.Detail, PermissionScope: deviceBlock.Scope));
            Check(session.CanOpenSettings && !session.CanControlTarget && session.Detail == deviceBlock.Detail,
                "preflight explains the blocker before exposing an action");
            session.PrepareToOpenSettings();
            session.MarkSettingsOpened();
            foreach (var observation in new[] { deviceObservation, appsObservation, teamsObservation })
            {
                session.ApplySettingsObservation(observation);
                Check(session.Target?.Kind == observation.Target?.Kind
                    && session.CanControlTarget == (mode == CameraRecoveryInteractionMode.Control)
                    && !session.CanVerifyTeams && !session.CanRestartTeams,
                    "each permission scope requires its own action and cannot claim readiness");
            }
            session.ApplySettingsObservation(allowedObservation);
            Check(session.CanReturnToTeams && !session.LocalVerifierPassed, "permissions are not camera readiness");
            session.MarkReturnedToTeams();
            Check(session.CanInspectTeams && !session.CanControlTarget && !session.CanVerifyTeams,
                "global permission recovery requires fresh Teams inspection");
            session.ApplyTeamsObservation(new("teams-global", TeamsCameraFinding.CameraOff, "",
                new("camera-fresh", "Turn camera on (Ctrl+Shift+O)", "camera", CameraRecoveryTargetKind.TeamsCameraButton)));
            Check(session.CanControlTarget == (mode == CameraRecoveryInteractionMode.Control)
                && session.Target?.Kind == CameraRecoveryTargetKind.TeamsCameraButton,
                "camera-on is a separate action after permission recovery");
        }

        var wrongScope = SettingsSession();
        wrongScope.ApplySettingsObservation(deviceObservation with
        {
            Target = deviceObservation.Target! with { Kind = CameraRecoveryTargetKind.PackagedTeamsPermission }
        });
        Check(wrongScope.State == CameraRecoveryState.Unsupported, "permission ID cannot authorize another scope");

        var restoredBeforeInspection = new CameraRecoverySession(CameraRecoveryInteractionMode.Control);
        restoredBeforeInspection.Start();
        restoredBeforeInspection.ChooseTeamsWindow("teams-devices", "Synthetic Teams Devices");
        restoredBeforeInspection.ApplyTeamsObservation(new("teams-devices", TeamsCameraFinding.PermissionMayBeOff,
            deviceBlock.Detail, PermissionScope: deviceBlock.Scope));
        restoredBeforeInspection.PrepareToOpenSettings();
        restoredBeforeInspection.MarkSettingsOpened();
        restoredBeforeInspection.ApplySettingsObservation(allowedObservation);
        Check(restoredBeforeInspection.CanReturnToTeams && !restoredBeforeInspection.CanControlTarget,
            "manually restored global permission has a read-only continuation");
        restoredBeforeInspection.MarkReturnedToTeams();
        restoredBeforeInspection.ApplyTeamsObservation(new("teams-devices",
            TeamsCameraFinding.PermissionAlreadyOnOrDifferentCause, "All permissions are on."));
        Check(restoredBeforeInspection.CanVerifyTeams && !restoredBeforeInspection.IsTerminal
            && !restoredBeforeInspection.LocalVerifierPassed && !restoredBeforeInspection.CanControlTarget,
            "post-restoration Devices surface continues to verification rather than wrong-cause terminal");
    }

    private static CameraRecoverySession StartedSession()
    {
        var session = new CameraRecoverySession();
        session.Start();
        session.ChooseTeamsWindow("teams-1", "Microsoft Teams");
        return session;
    }

    private static CameraRecoverySession PermissionOnSession()
    {
        var session = SettingsSession();
        session.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOff, "",
            new CameraRecoveryTarget("settings-observation-2", "Microsoft Teams Currently in use",
                CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle,
                CameraRecoveryTargetKind.PackagedTeamsPermission),
            CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        session.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOn, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        session.MarkReturnedToTeams();
        return session;
    }

    private static CameraRecoverySession SettingsSession()
    {
        var session = StartedSession();
        session.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.PermissionMayBeOff, ""));
        session.PrepareToOpenSettings();
        session.MarkSettingsOpened();
        return session;
    }
}

public partial class App
{
    static App()
    {
        if (Environment.GetEnvironmentVariable("MSGUIDE_CAMERA_RECOVERY_TESTS") == "1")
            CameraRecoveryTests.Run();
    }
}
