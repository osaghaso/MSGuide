namespace MSGuide.Desktop;

internal static class CameraRecoveryTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Camera recovery test failed: {name}.");
        }

        Check(CameraRecoverySession.IsCameraHelpIntent("Help me fix my camera in Teams"), "typed intent");
        Check(CameraRecoverySession.IsCameraHelpIntent("My meeting video is not working"), "meeting video intent");
        Check(!CameraRecoverySession.IsCameraHelpIntent("Help me find the build error"), "unrelated intent");

        var pendingSensing = new PendingCameraRecoverySensing();
        var pendingResult = pendingSensing.ObserveTeamsAsync(
            new WindowChoice(0, 0, "Synthetic Teams"), CancellationToken.None).GetAwaiter().GetResult();
        Check(pendingSensing.Mode == CameraRecoverySensingMode.UnsupportedFallback
            && pendingResult.Finding == TeamsCameraFinding.Unsupported
            && pendingResult.Detail.Contains("did not take a screenshot", StringComparison.Ordinal),
            "explicit unsupported fallback");

        var session = new CameraRecoverySession();
        Check(session.State == CameraRecoveryState.Idle && !session.LocalVerifierPassed, "idle");
        session.Start();
        session.ChooseTeamsWindow("teams-1", "Weekly meeting | Microsoft Teams");
        Check(session.State == CameraRecoveryState.NeedsTeamsObservation && session.CanInspectTeams, "choose Teams");
        session.ApplyTeamsObservation(new("teams-1", TeamsCameraFinding.PermissionMayBeOff, ""));
        Check(session.State == CameraRecoveryState.Diagnosis && session.CanOpenSettings, "diagnosis");
        session.PrepareToOpenSettings();
        session.MarkSettingsOpened();
        Check(session.State == CameraRecoveryState.NeedsSettingsObservation && session.CanInspectSettings, "settings opened");
        session.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOff, "",
            new CameraRecoveryTarget("settings-observation-1", "Microsoft Teams Currently in use",
                CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle),
            CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(session.State == CameraRecoveryState.VerifiedTarget && session.CanShowTarget
            && session.CanCheckChangedSetting
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
        Check(session.State == CameraRecoveryState.Ready && session.LocalVerifierPassed, "local verifier ready");

        var falseReady = PermissionOnSession();
        falseReady.ApplyVerification(new("teams-1", CameraVerificationFinding.Ready, false, ""));
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
        reinitialize.ApplyVerification(new("teams-1", CameraVerificationFinding.Ready, true, ""));
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
        wrongSettingsPage.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOn, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.Other));
        Check(wrongSettingsPage.State == CameraRecoveryState.WrongSettingsPage
            && !wrongSettingsPage.LocalVerifierPassed, "Settings URI landing verified");

        var alreadyOnInSettings = SettingsSession();
        alreadyOnInSettings.ApplySettingsObservation(new(CameraSettingsFinding.PermissionOn, "",
            Source: CameraSettingsObservationSource.ControlsOnly, ProbeValidated: true,
            Page: CameraSettingsPage.CameraPrivacy));
        Check(alreadyOnInSettings.State == CameraRecoveryState.AlreadyOnOrWrongCause
            && !alreadyOnInSettings.CanReturnToTeams, "initial Settings permission already on");

        var cancelled = StartedSession();
        cancelled.Cancel();
        Check(cancelled.State == CameraRecoveryState.Cancelled && !cancelled.LocalVerifierPassed, "cancelled");

        bool invalidTransitionRejected = false;
        try { new CameraRecoverySession().MarkReturnedToTeams(); }
        catch (InvalidOperationException) { invalidTransitionRejected = true; }
        Check(invalidTransitionRejected, "invalid transition");

        var fixture = new FixtureCameraRecoverySensing();
        var fixtureWindow = new WindowChoice(0, 0, "Synthetic Teams fixture");
        var fixtureSession = new CameraRecoverySession();
        fixtureSession.Start();
        fixtureSession.ChooseTeamsWindow(fixtureWindow.Id, fixtureWindow.Title);
        fixtureSession.ApplyTeamsObservation(
            fixture.ObserveTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        fixtureSession.PrepareToOpenSettings();
        fixtureSession.MarkSettingsOpened();
        fixtureSession.ApplySettingsObservation(
            fixture.ObserveSettingsAsync(CancellationToken.None).GetAwaiter().GetResult());
        fixtureSession.RecordTargetPresentation(
            fixture.ShowTargetAsync(fixtureSession.Target!, CancellationToken.None).GetAwaiter().GetResult());
        fixtureSession.ApplySettingsObservation(
            fixture.ObserveSettingsAsync(CancellationToken.None).GetAwaiter().GetResult());
        fixtureSession.MarkReturnedToTeams();
        fixtureSession.ApplyVerification(
            fixture.VerifyTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        Check(fixtureSession.State == CameraRecoveryState.NeedsCameraReinitialization
            && fixtureSession.CanVerifyTeams, "fixture requires camera reinitialization");
        fixtureSession.ApplyVerification(
            fixture.VerifyTeamsAsync(fixtureWindow, CancellationToken.None).GetAwaiter().GetResult());
        Check(fixture.Mode == CameraRecoverySensingMode.Fixture
            && fixtureSession.State == CameraRecoveryState.FixtureComplete
            && fixtureSession.Detail.Contains("not claimed", StringComparison.OrdinalIgnoreCase),
            "deterministic fixture path");
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
                CameraRecoveryPinnedTargets.PackagedTeamsCameraToggle),
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
