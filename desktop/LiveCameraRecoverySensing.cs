using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace MSGuide.Desktop;

internal sealed class LiveCameraRecoverySensing(OverlayWindow overlay)
    : ICameraRecoverySensing, ICameraRecoveryControl
{
    private const string TeamsPermissionId = "MSTeams_8wekyb3d8bbwe_ToggleSwitch";
    private const string SystemPermissionId = CameraRecoveryPinnedTargets.DeviceCameraToggle;
    private const string AppPermissionId = CameraRecoveryPinnedTargets.AppCameraToggle;
    private const string CameraConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    private const string TeamsConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\MSTeams_8wekyb3d8bbwe";
    private const string CameraPolicyKey = @"SOFTWARE\Policies\Microsoft\Camera";
    private const string AppPrivacyPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private readonly object gate = new();
    private TargetCache? target;
    private bool permissionOffObserved;
    private DateTimeOffset? permissionRestoredAt;
    private bool teamsCameraOffObserved;
    private DateTimeOffset? teamsCameraEnabledAt;

    private sealed record ControlRead(WindowChoice Window, Native.RECT Rect, ElementInfo[] Elements);
    private sealed record TargetCache(
        string Id, WindowChoice Window, Native.RECT Rect, ElementInfo Element,
        CameraRecoveryTargetKind Kind);
    private sealed record TeamsCameraControl(
        TeamsCameraControlState State, ElementInfo Element);

    public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.Connected;

    public void Reset()
    {
        lock (gate)
        {
            target = null;
            permissionOffObserved = false;
            permissionRestoredAt = null;
            teamsCameraOffObserved = false;
            teamsCameraEnabledAt = null;
        }
    }

    public Task<TeamsCameraObservation> ObserveTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTeams(window))
                return new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.Unsupported,
                    "Choose the Microsoft Teams desktop window.");

            var permission = ReadPermission();
            if (permission.State != CameraPermissionState.On)
            {
                lock (gate)
                {
                    target = null;
                    if (permission.State == CameraPermissionState.Off)
                    {
                        permissionOffObserved = true;
                        permissionRestoredAt = null;
                    }
                }
                return new TeamsCameraObservation(window.Id, permission.State switch
                {
                    CameraPermissionState.Off => TeamsCameraFinding.PermissionMayBeOff,
                    CameraPermissionState.Managed => TeamsCameraFinding.ManagedOrDisabled,
                    _ => TeamsCameraFinding.Unsupported
                }, permission.Detail, PermissionScope: permission.Scope);
            }
            var read = ReadControls(window, cancellationToken, maxDepth: 32);
            var cameraControl = FindTeamsCameraControl(read.Elements);
            if (cameraControl is { State: TeamsCameraControlState.Off, Element: { } offCamera })
            {
                lock (gate)
                {
                    target = new(
                        offCamera.TargetId, read.Window, read.Rect, offCamera,
                        CameraRecoveryTargetKind.TeamsCameraButton);
                    teamsCameraOffObserved = true;
                    teamsCameraEnabledAt = null;
                }
                return new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.CameraOff,
                    "The visible Teams camera control is off.",
                    new CameraRecoveryTarget(
                        offCamera.TargetId, offCamera.Label, offCamera.AutomationId,
                        CameraRecoveryTargetKind.TeamsCameraButton));
            }
            if (cameraControl is { State: TeamsCameraControlState.On })
            {
                lock (gate)
                {
                    target = null;
                    if (teamsCameraOffObserved)
                        teamsCameraEnabledAt ??= DateTimeOffset.UtcNow;
                }
                return new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.CameraOn,
                    "The visible Teams camera control is on.");
            }
            if (!Safety.VerifiedTeamsDevicesPage(read.Elements))
                return new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.Unsupported,
                    "Open Teams Settings > Devices so MSGuide can verify the camera surface.");

            return new TeamsCameraObservation(
                window.Id, TeamsCameraFinding.PermissionAlreadyOnOrDifferentCause,
                permission.Detail);
        }, cancellationToken);

    public Task<CameraSettingsObservation> ObserveSettingsAsync(
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = FindCameraSettings(cancellationToken);
            if (read is null)
                return new CameraSettingsObservation(
                    CameraSettingsFinding.WrongPage,
                    "Open Windows Privacy & security > Camera, then inspect again.",
                    Source: CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.Other);

            var observation = AssessSettingsPermissions(
                read.Elements, ReadPermission().State == CameraPermissionState.Managed);
            if (observation.Target is { } blocked)
            {
                var element = read.Elements.Single(item => item.TargetId == blocked.ObservationId);
                lock (gate)
                {
                    target = new(
                        blocked.ObservationId, read.Window, read.Rect, element, blocked.Kind);
                    permissionOffObserved = true;
                    permissionRestoredAt = null;
                }
                return observation;
            }

            lock (gate)
            {
                target = null;
                if (observation.Finding == CameraSettingsFinding.PermissionOn && permissionOffObserved)
                    permissionRestoredAt ??= DateTimeOffset.UtcNow;
            }
            return observation;
        }, cancellationToken);

    internal static CameraSettingsObservation AssessSettingsPermissions(ElementInfo[] elements, bool policyManaged)
    {
        CameraSettingsObservation Result(CameraSettingsFinding finding, string detail, CameraRecoveryTarget? target = null) =>
            new(finding, detail, target, CameraSettingsObservationSource.ControlsOnly,
                ProbeValidated: true, Page: CameraSettingsPage.CameraPrivacy);
        if (!Safety.VerifiedCameraSettingsPage(elements))
            return new(CameraSettingsFinding.WrongPage, "The Camera privacy page could not be verified.",
                Source: CameraSettingsObservationSource.ControlsOnly,
                ProbeValidated: true, Page: CameraSettingsPage.Other);
        if (policyManaged)
            return Result(CameraSettingsFinding.ManagedOrDisabled,
                "Camera access is blocked by policy. MSGuide will not override it.");
        foreach (var (id, kind) in new[]
        {
            (SystemPermissionId, CameraRecoveryTargetKind.DeviceCameraPermission),
            (AppPermissionId, CameraRecoveryTargetKind.AppCameraPermission),
            (TeamsPermissionId, CameraRecoveryTargetKind.PackagedTeamsPermission)
        })
        {
            var element = Find(elements, id);
            if (element is null)
                return Result(CameraSettingsFinding.Unsupported, "A required camera permission control is missing.");
            if (!element.IsEnabled || !element.Targetable)
                return Result(CameraSettingsFinding.ManagedOrDisabled,
                    "The required camera setting is disabled. MSGuide will not override it.");
            if (element.ToggleState == "off")
                return Result(CameraSettingsFinding.PermissionOff, CameraPermissionEvidence.DescribeBlock(kind),
                    new(element.TargetId, element.Label, element.AutomationId, kind));
            if (element.ToggleState != "on")
                return Result(CameraSettingsFinding.Unsupported, "The camera permission toggle state is unknown.");
        }
        return Result(CameraSettingsFinding.PermissionOn, "Device, app, and Teams camera permissions are on.");
    }

    public async Task<CameraVerificationResult> VerifyTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTeams(window))
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.Unsupported, false,
                "Choose the reopened Microsoft Teams desktop window.");
        var permission = ReadPermission();
        if (permission.State != CameraPermissionState.On)
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.Unresolved, false,
                permission.Detail);

        var read = await Task.Run(
            () => ReadControls(window, cancellationToken, maxDepth: 32), cancellationToken);
        var cameraControl = FindTeamsCameraControl(read.Elements);
        if (cameraControl is { State: TeamsCameraControlState.Off })
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.Unresolved, false,
                "The visible Teams camera control is still off.");
        if (cameraControl is { State: TeamsCameraControlState.On })
        {
            DateTimeOffset? enabledAt;
            lock (gate) enabledAt = teamsCameraEnabledAt;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (TryGetCameraUseStart(out var cameraUseForControl)
                    && MatchActiveCameraUse(window.Id, enabledAt, cameraUseForControl) is { } result)
                    return result;
                if (attempt < 7)
                    await Task.Delay(300, cancellationToken);
            }
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.NeedsReinitialization, false,
                "Teams reports the camera control on, but Windows has not confirmed active camera use.");
        }
        if (!Safety.VerifiedTeamsDevicesPage(read.Elements))
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.NeedsReinitialization, false,
                "Open Teams Settings > Devices or reopen the meeting prejoin screen.");
        var camera = read.Elements.SingleOrDefault(element =>
            element.Role == "combobox"
            && element.Label == "Camera"
            && element.IsEnabled
            && element.Targetable);
        if (camera is null)
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.NeedsReinitialization, false,
                "Teams has not exposed an enabled Camera selector yet.");

        DateTimeOffset? restoredAt;
        lock (gate) restoredAt = permissionRestoredAt;
        if (!await HasPreviewMotion(window, cancellationToken)
            || !TryGetCameraUseStart(out var cameraUseStarted)
            || restoredAt is null
            || cameraUseStarted < restoredAt.Value.AddSeconds(-2))
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.NeedsReinitialization, false,
                "No active changing Teams camera preview was verified. Reopen the camera surface or relaunch Teams.");

        return new CameraVerificationResult(
            window.Id, CameraVerificationFinding.Ready, true,
            "A changing preview was observed locally after Teams reinitialized its camera.",
            Reinitialized: true);
    }

    internal static CameraVerificationResult? MatchActiveCameraUse(
        string windowId, DateTimeOffset? enabledAt, DateTimeOffset? activeUseStartedAt)
    {
        if (activeUseStartedAt is null
            || enabledAt is not null && activeUseStartedAt.Value < enabledAt.Value.AddSeconds(-2))
            return null;
        return new(windowId,
            enabledAt is null ? CameraVerificationFinding.AlreadyReady : CameraVerificationFinding.Ready,
            true, "The Teams camera control is on and Windows reports active camera use.",
            Reinitialized: enabledAt is not null);
    }

    public Task<CameraTargetPresentation> ShowTargetAsync(
        CameraRecoveryTarget requested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TargetCache? current;
        lock (gate) current = target;
        if (current is null
            || requested.ObservationId != current.Id
            || requested.Kind != current.Kind
            || !current.Window.Matches())
            return Task.FromResult(new CameraTargetPresentation(
                false, "The camera target changed. Inspect the current Teams or Settings window again."));

        ControlRead? refreshed;
        try
        {
            refreshed = CameraRecoveryPinnedTargets.IsPermission(current.Kind)
                ? FindCameraSettings(cancellationToken)
                : ReadControls(current.Window, cancellationToken, maxDepth: 32);
        }
        catch (InvalidOperationException) { refreshed = null; }
        var element = refreshed?.Elements.SingleOrDefault(
            candidate => candidate.TargetId == current.Id);
        if (refreshed is null || element is null
            || element.TargetId != current.Id
            || !TargetIsOff(element, current.Kind)
            || !element.IsEnabled
            || !element.Targetable)
        {
            lock (gate) target = null;
            return Task.FromResult(new CameraTargetPresentation(
                false, "The camera target changed. Inspect the current Teams or Settings window again."));
        }

        overlay.PointAt(refreshed.Rect, element.Box);
        return Task.FromResult(new CameraTargetPresentation(
            overlay.IsVisible, overlay.IsVisible
                ? current.Kind == CameraRecoveryTargetKind.TeamsCameraButton
                    ? "The current Teams camera-on control is outlined."
                    : "The current camera permission setting is outlined."
                : "The verified target could not be outlined."));
    }

    public async Task<CameraTargetControlResult> ActivateTargetAsync(
        CameraRecoveryTarget requested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TargetCache? current;
        lock (gate)
        {
            current = target;
            target = null;
        }
        if (current is null
            || requested.ObservationId != current.Id
            || requested.Kind != current.Kind
            || !current.Window.Matches())
            return new CameraTargetControlResult(
                false, true, "The verified camera target changed before approval.");

        var result = await DesktopAction.RunBounded((token, beginInvocation) =>
        {
            var action = ActivateTarget(current, token, beginInvocation);
            return new(action.Invoked, action.OutcomeKnown, action.Detail);
        }, cancellationToken);
        if (result is { Invoked: true, OutcomeKnown: true } && !cancellationToken.IsCancellationRequested
            && current.Kind == CameraRecoveryTargetKind.TeamsCameraButton)
        {
            lock (gate) teamsCameraEnabledAt = DateTimeOffset.UtcNow;
        }
        return new(result.Invoked, result.OutcomeKnown, result.Detail);
    }

    public async Task<TeamsRestartResult> RestartTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DesktopAction.IsBusy)
            return new(TeamsRestartFinding.Failed,
                "A native action is still returning. Teams restart was not started.");
        if (!IsTeams(window))
            return new TeamsRestartResult(
                TeamsRestartFinding.StaleOrMoved,
                "The selected Teams window changed before restart approval.");

        Process process;
        try
        {
            process = Process.GetProcessById((int)window.ProcessId);
            string executable = process.MainModule?.FileName ?? "";
            if (!Path.GetFileName(executable).Equals("ms-teams.exe", StringComparison.OrdinalIgnoreCase)
                || !executable.Contains(@"\WindowsApps\MSTeams_", StringComparison.OrdinalIgnoreCase)
                || !executable.Contains("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return new TeamsRestartResult(
                    TeamsRestartFinding.Unsupported,
                    "The selected window was not the pinned packaged Microsoft Teams process.");
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or Win32Exception
                or NotSupportedException)
        {
            return new TeamsRestartResult(
                TeamsRestartFinding.Unsupported,
                "The selected Teams process could not be verified for restart.");
        }

        using (process)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.CloseMainWindow();
                try
                {
                    await process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync()
                            .WaitAsync(TimeSpan.FromSeconds(8));
                    }
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Win32Exception or NotSupportedException
                    or TimeoutException)
            {
                return new TeamsRestartResult(
                    TeamsRestartFinding.Failed,
                    "Teams did not exit after the separately approved restart.");
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("msteams:") { UseShellExecute = true });
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new TeamsRestartResult(
                TeamsRestartFinding.Failed,
                "Teams exited, but Windows could not launch it again.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
                return new TeamsRestartResult(
                    TeamsRestartFinding.Restarted,
                    "Teams restart was launched before the operation was cancelled.");
            await Task.Delay(300);
            var candidates = WindowChoice.List(0, 0)
                .Where(candidate => candidate.IsMicrosoftTeamsWindow
                    && candidate.Id != window.Id)
                .ToArray();
            var reopened = candidates.FirstOrDefault(candidate =>
                    candidate.Title.Equals(window.Title, StringComparison.Ordinal))
                ?? (candidates.Length == 1 ? candidates[0] : null);
            if (reopened is not null)
                return new TeamsRestartResult(
                    TeamsRestartFinding.Restarted,
                    "Teams restarted after separate approval.", reopened);
        }
        return new TeamsRestartResult(
            TeamsRestartFinding.Restarted,
            "Teams launch was requested. Choose its reopened window when it appears.");
    }

    private static CameraTargetControlResult ActivateTarget(
        TargetCache current, CancellationToken cancellationToken, Func<bool> beginInvocation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permission = ReadPermission();
        if (permission.State == CameraPermissionState.Managed
            || current.Kind == CameraRecoveryTargetKind.TeamsCameraButton
                && permission.State != CameraPermissionState.On)
            return new(false, true, permission.Detail);
        ControlRead? refreshed = CameraRecoveryPinnedTargets.IsPermission(current.Kind)
            ? FindCameraSettings(cancellationToken)
            : ReadControls(current.Window, cancellationToken, maxDepth: 32);
        var expected = refreshed?.Elements.SingleOrDefault(
            element => element.TargetId == current.Id);
        if (refreshed is null || expected is null
            || !TargetIsOff(expected, current.Kind)
            || !expected.IsEnabled || !expected.Targetable)
            return new CameraTargetControlResult(
                false, true, "The verified camera target changed before the approved action.");
        if (CameraRecoveryPinnedTargets.IsPermission(current.Kind))
        {
            var next = AssessSettingsPermissions(refreshed.Elements, policyManaged: false);
            if (next.Target?.Kind != current.Kind || next.Target.ObservationId != current.Id)
                return new(false, true, "The permission scope changed. Inspect settings and approve the new scope separately.");
        }

        var raw = AutomationEvidence.FindUniqueTarget(
            refreshed.Window, refreshed.Rect, expected.TargetId, expected.Label,
            expected.AutomationId, cancellationToken);
        if (raw is null)
            return new CameraTargetControlResult(
                false, true, "The exact accessible camera control could not be reacquired.");

        try
        {
            if (raw.Current.IsPassword || raw.Current.IsOffscreen || !raw.Current.IsEnabled
                || !refreshed.Window.Matches()
                || !Native.GetWindowRect(refreshed.Window.Handle, out var finalRect)
                || !refreshed.Rect.Same(finalRect))
                return new(false, true, "The exact camera window or control changed before invocation.");
            if (raw.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern)
                && togglePattern is TogglePattern toggle
                && toggle.Current.ToggleState == ToggleState.Off)
            {
                if (!beginInvocation()) return new(false, true, "The camera action was cancelled before invocation.");
                toggle.Toggle();
                return new CameraTargetControlResult(
                    true, true, "The approved camera toggle action was invoked once.");
            }
            if (current.Kind == CameraRecoveryTargetKind.TeamsCameraButton
                && CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(expected.Label)
                && raw.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern)
                && invokePattern is InvokePattern invoke)
            {
                if (!beginInvocation()) return new(false, true, "The camera action was cancelled before invocation.");
                invoke.Invoke();
                return new CameraTargetControlResult(
                    true, true, "The approved Teams camera-on action was invoked once.");
            }
            return new CameraTargetControlResult(
                false, true, "The exact camera control no longer exposed its approved action pattern.");
        }
        catch (Exception ex) when (
            ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return new CameraTargetControlResult(
                true, false,
                "The approved action returned an unknown outcome. MSGuide will not retry it.");
        }
    }

    private static bool IsTeams(WindowChoice window) =>
        window.Matches() && window.IsMicrosoftTeamsWindow;

    private static ControlRead ReadControls(
        WindowChoice window, CancellationToken cancellationToken, int maxDepth = 18)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!window.Matches()
            || Native.IsHungAppWindow(window.Handle)
            || !Native.GetWindowRect(window.Handle, out var rect))
            throw new InvalidOperationException("The selected window is unavailable.");
        var read = CaptureService.ReadAutomation(
            window, rect, cancellationToken, maxDepth);
        cancellationToken.ThrowIfCancellationRequested();
        if (!window.Matches()
            || !Native.GetWindowRect(window.Handle, out var after)
            || !rect.Same(after))
            throw new InvalidOperationException("The selected window changed during inspection.");
        return new(window, rect, read.RequireComplete());
    }

    private static ControlRead? FindCameraSettings(CancellationToken cancellationToken)
    {
        var candidates = new List<WindowChoice>();
        Native.EnumWindows((handle, _) =>
        {
            if (Native.NormalWindow(handle)
                && Native.Title(handle).Equals("Settings", StringComparison.OrdinalIgnoreCase))
            {
                Native.GetWindowThreadProcessId(handle, out var processId);
                candidates.Add(new(handle, processId, Native.Title(handle), Native.WindowClass(handle)));
            }
            return true;
        }, 0);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var read = ReadControls(candidate, cancellationToken);
                if (Safety.VerifiedCameraSettingsPage(read.Elements)) return read;
            }
            catch (InvalidOperationException ex) when (ex is not IncompleteAutomationReadException) { }
        }
        return null;
    }

    private static ElementInfo? Find(IEnumerable<ElementInfo> elements, string automationId) =>
        elements.SingleOrDefault(element =>
            element.AutomationId.Equals(automationId, StringComparison.Ordinal));

    private static TeamsCameraControl? FindTeamsCameraControl(
        IEnumerable<ElementInfo> elements)
    {
        var controls = elements.Where(element =>
                element.Role is "button" or "checkbox"
                && element.IsEnabled
                && element.Targetable
                && (CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(element.Label)
                    || CameraRecoveryPinnedTargets.IsTeamsTurnCameraOff(element.Label)
                    || CameraRecoveryPinnedTargets.IsTeamsCameraToggle(element.Label)
                        && element.ToggleState is "off" or "on"))
            .ToArray();
        if (controls.Length > 1)
            throw new InvalidOperationException(
                "Multiple visible Teams camera controls matched the pinned state.");
        if (controls.Length == 0) return null;
        var control = controls[0];
        bool off = CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(control.Label)
            || CameraRecoveryPinnedTargets.IsTeamsCameraToggle(control.Label)
                && control.ToggleState == "off";
        return new TeamsCameraControl(
            off ? TeamsCameraControlState.Off : TeamsCameraControlState.On, control);
    }

    private static bool TargetIsOff(
        ElementInfo element, CameraRecoveryTargetKind kind) =>
        kind switch
        {
            CameraRecoveryTargetKind.PackagedTeamsPermission
                or CameraRecoveryTargetKind.DeviceCameraPermission
                or CameraRecoveryTargetKind.AppCameraPermission =>
                CameraRecoveryPinnedTargets.IsPermissionTarget(element.AutomationId, kind)
                && element.ToggleState == "off",
            CameraRecoveryTargetKind.TeamsCameraButton =>
                CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(element.Label)
                || CameraRecoveryPinnedTargets.IsTeamsCameraToggle(element.Label)
                    && element.ToggleState == "off",
            _ => false
        };

    private static CameraPermissionEvidence ReadPermission()
    {
        using var cameraPolicy = Registry.LocalMachine.OpenSubKey(CameraPolicyKey);
        bool policyDenied = cameraPolicy?.GetValue("AllowCamera") is int allowed && allowed == 0;
        using var appPrivacy = Registry.LocalMachine.OpenSubKey(AppPrivacyPolicyKey);
        policyDenied |= appPrivacy?.GetValue("LetAppsAccessCamera") is int access && access == 2;
        using var deviceConsent = Registry.LocalMachine.OpenSubKey(CameraConsentKey);
        using var appConsent = Registry.CurrentUser.OpenSubKey(CameraConsentKey);
        using var consent = Registry.CurrentUser.OpenSubKey(TeamsConsentKey);
        return CameraPermissionEvidence.FromConsent(policyDenied,
            deviceConsent?.GetValue("Value") as string,
            appConsent?.GetValue("Value") as string,
            consent?.GetValue("Value") as string);
    }

    private static bool TryGetCameraUseStart(out DateTimeOffset startedAt)
    {
        startedAt = default;
        using var consent = Registry.CurrentUser.OpenSubKey(TeamsConsentKey);
        long start = ToInt64(consent?.GetValue("LastUsedTimeStart"));
        long stop = ToInt64(consent?.GetValue("LastUsedTimeStop"));
        if (start <= 0 || (stop != 0 && start <= stop)) return false;
        try
        {
            startedAt = DateTimeOffset.FromFileTime(start);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static async Task<bool> HasPreviewMotion(
        WindowChoice window, CancellationToken cancellationToken)
    {
        using var first = await CaptureService.Capture(window, cancellationToken);
        await Task.Delay(400, cancellationToken);
        using var second = await CaptureService.Capture(window, cancellationToken);
        if (first.Preview is null || second.Preview is null
            || first.Preview.PixelWidth != second.Preview.PixelWidth
            || first.Preview.PixelHeight != second.Preview.PixelHeight)
            return false;

        var group = second.Elements.SingleOrDefault(element =>
            element.AutomationId == CameraRecoveryPinnedTargets.TeamsVideoSettings);
        var camera = second.Elements.SingleOrDefault(element =>
            element.Role == "combobox" && element.Label == "Camera");
        var preview = second.Elements.SingleOrDefault(element =>
            element.Role == "text" && element.Label == "Preview");
        if (group is null || camera is null || preview is null) return false;

        double left = group.Box[0] + Math.Min(0.02, group.Box[2] / 10);
        double top = preview.Box[1] + preview.Box[3] + 0.005;
        double right = Math.Min(group.Box[0] + group.Box[2] * 0.52,
            camera.Box[0] - 0.01);
        double bottom = camera.Box[1] - 0.01;
        if (right - left < 0.05 || bottom - top < 0.05) return false;
        return FramesDiffer(first.Preview, second.Preview, left, top, right, bottom);
    }

    private static bool FramesDiffer(BitmapSource first, BitmapSource second,
        double left, double top, double right, double bottom)
    {
        int width = first.PixelWidth, height = first.PixelHeight, stride = checked(width * 4);
        byte[] before = new byte[checked(stride * height)];
        byte[] after = new byte[before.Length];
        try
        {
            first.CopyPixels(before, stride, 0);
            second.CopyPixels(after, stride, 0);
            int x0 = Math.Clamp((int)(left * width), 0, width - 1);
            int y0 = Math.Clamp((int)(top * height), 0, height - 1);
            int x1 = Math.Clamp((int)Math.Ceiling(right * width), x0 + 1, width);
            int y1 = Math.Clamp((int)Math.Ceiling(bottom * height), y0 + 1, height);
            int stepX = Math.Max(1, (x1 - x0) / 40);
            int stepY = Math.Max(1, (y1 - y0) / 30);
            int samples = 0, changed = 0;
            long totalDifference = 0;
            for (int y = y0; y < y1; y += stepY)
            for (int x = x0; x < x1; x += stepX)
            {
                int offset = y * stride + x * 4;
                int difference = Math.Abs(before[offset] - after[offset])
                    + Math.Abs(before[offset + 1] - after[offset + 1])
                    + Math.Abs(before[offset + 2] - after[offset + 2]);
                totalDifference += difference;
                if (difference >= 24) changed++;
                samples++;
            }
            return samples > 0
                && changed >= Math.Max(8, samples / 50)
                && totalDifference / samples >= 8;
        }
        finally
        {
            Array.Clear(before);
            Array.Clear(after);
        }
    }

    private static long ToInt64(object? value) => value switch
    {
        long number => number,
        int number => number,
        _ => 0
    };


    private enum TeamsCameraControlState
    {
        Off,
        On
    }
}
