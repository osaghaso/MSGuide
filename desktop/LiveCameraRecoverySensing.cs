using Microsoft.Win32;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace MSGuide.Desktop;

internal sealed class LiveCameraRecoverySensing(OverlayWindow overlay) : ICameraRecoverySensing
{
    private const string TeamsPermissionId = "MSTeams_8wekyb3d8bbwe_ToggleSwitch";
    private const string SystemPermissionId =
        "SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch";
    private const string AppPermissionId =
        "SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch";
    private const string TeamsConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\MSTeams_8wekyb3d8bbwe";
    private const string CameraPolicyKey = @"SOFTWARE\Policies\Microsoft\Camera";
    private const string AppPrivacyPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";
    private readonly object gate = new();
    private TargetCache? target;
    private bool permissionOffObserved;
    private DateTimeOffset? permissionRestoredAt;

    private sealed record ControlRead(WindowChoice Window, Native.RECT Rect, ElementInfo[] Elements);
    private sealed record TargetCache(string Id, WindowChoice Window, Native.RECT Rect, ElementInfo Element);

    public CameraRecoverySensingMode Mode => CameraRecoverySensingMode.Connected;

    public Task<TeamsCameraObservation> ObserveTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTeams(window))
                return new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.Unsupported,
                    "Choose the Microsoft Teams desktop window.");

            var read = ReadControls(window, cancellationToken);
            if (!Safety.VerifiedTeamsDevicesPage(read.Elements))
                return new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.Unsupported,
                    "Open Teams Settings > Devices so MSGuide can verify the camera surface.");

            var permission = ReadPermission();
            return permission switch
            {
                PermissionState.Managed => new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.ManagedOrDisabled,
                    "Camera access is controlled by policy."),
                PermissionState.Off => new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.PermissionMayBeOff,
                    "The packaged Microsoft Teams camera permission is off."),
                PermissionState.On => new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.PermissionAlreadyOnOrDifferentCause,
                    "The packaged Microsoft Teams camera permission is already on."),
                _ => new TeamsCameraObservation(
                    window.Id, TeamsCameraFinding.Unsupported,
                    "The packaged Microsoft Teams camera permission could not be read safely.")
            };
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

            var system = Find(read.Elements, SystemPermissionId);
            var apps = Find(read.Elements, AppPermissionId);
            var teams = Find(read.Elements, TeamsPermissionId);
            if (system is null || apps is null || teams is null)
                return new CameraSettingsObservation(
                    CameraSettingsFinding.Unsupported,
                    "The pinned Camera Settings controls were incomplete.",
                    Source: CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.CameraPrivacy);
            if (ReadPermission() == PermissionState.Managed
                || !system.IsEnabled || !apps.IsEnabled || !teams.IsEnabled)
                return new CameraSettingsObservation(
                    CameraSettingsFinding.ManagedOrDisabled,
                    "One or more camera permissions are managed or disabled.",
                    Source: CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.CameraPrivacy);
            if (system.ToggleState != "on" || apps.ToggleState != "on")
                return new CameraSettingsObservation(
                    CameraSettingsFinding.Unsupported,
                    "This prototype supports only the individual Microsoft Teams permission branch.",
                    Source: CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.CameraPrivacy);

            if (teams.ToggleState == "off")
            {
                var id = teams.TargetId;
                lock (gate)
                {
                    target = new(id, read.Window, read.Rect, teams);
                    permissionOffObserved = true;
                    permissionRestoredAt = null;
                }
                return new CameraSettingsObservation(
                    CameraSettingsFinding.PermissionOff,
                    "The individual Microsoft Teams camera permission is off.",
                    new CameraRecoveryTarget(id, teams.Label, teams.AutomationId),
                    CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.CameraPrivacy);
            }

            lock (gate)
            {
                target = null;
                if (teams.ToggleState == "on" && permissionOffObserved)
                    permissionRestoredAt ??= DateTimeOffset.UtcNow;
            }
            return teams.ToggleState == "on"
                ? new CameraSettingsObservation(
                    CameraSettingsFinding.PermissionOn,
                    "The individual Microsoft Teams camera permission is on.",
                    Source: CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.CameraPrivacy)
                : new CameraSettingsObservation(
                    CameraSettingsFinding.Unsupported,
                    "The Microsoft Teams camera permission state is unavailable.",
                    Source: CameraSettingsObservationSource.ControlsOnly,
                    ProbeValidated: true,
                    Page: CameraSettingsPage.CameraPrivacy);
        }, cancellationToken);

    public async Task<CameraVerificationResult> VerifyTeamsAsync(
        WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTeams(window))
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.Unsupported, false,
                "Choose the reopened Microsoft Teams desktop window.");
        if (ReadPermission() != PermissionState.On)
            return new CameraVerificationResult(
                window.Id, CameraVerificationFinding.Unresolved, false,
                "The packaged Microsoft Teams camera permission is not on.");

        var read = await Task.Run(() => ReadControls(window, cancellationToken), cancellationToken);
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

    public Task<CameraTargetPresentation> ShowTargetAsync(
        CameraRecoveryTarget requested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TargetCache? current;
        lock (gate) current = target;
        if (current is null
            || requested.ObservationId != current.Id
            || requested.AutomationId != TeamsPermissionId
            || !current.Window.Matches())
            return Task.FromResult(new CameraTargetPresentation(
                false, "The Camera Settings target changed. Inspect the page again."));

        ControlRead? refreshed;
        try { refreshed = FindCameraSettings(cancellationToken); }
        catch (InvalidOperationException) { refreshed = null; }
        var element = refreshed is null ? null : Find(refreshed.Elements, TeamsPermissionId);
        if (refreshed is null || element is null
            || element.TargetId != current.Id
            || element.ToggleState != "off"
            || !element.IsEnabled
            || !element.Targetable)
        {
            lock (gate) target = null;
            return Task.FromResult(new CameraTargetPresentation(
                false, "The Camera Settings target changed. Inspect the page again."));
        }

        overlay.PointAt(refreshed.Rect, element.Box);
        return Task.FromResult(new CameraTargetPresentation(
            overlay.IsVisible, overlay.IsVisible
                ? "The current Microsoft Teams permission is outlined."
                : "The verified target could not be outlined."));
    }

    private static bool IsTeams(WindowChoice window) =>
        window.Matches()
        && window.Title.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase);

    private static ControlRead ReadControls(WindowChoice window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!window.Matches()
            || Native.IsHungAppWindow(window.Handle)
            || !Native.GetWindowRect(window.Handle, out var rect))
            throw new InvalidOperationException("The selected window is unavailable.");
        var (elements, _, _) = CaptureService.ReadAutomation(window, rect, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!window.Matches()
            || !Native.GetWindowRect(window.Handle, out var after)
            || !rect.Same(after))
            throw new InvalidOperationException("The selected window changed during inspection.");
        return new(window, rect, elements);
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
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private static ElementInfo? Find(IEnumerable<ElementInfo> elements, string automationId) =>
        elements.SingleOrDefault(element =>
            element.AutomationId.Equals(automationId, StringComparison.Ordinal));

    private static PermissionState ReadPermission()
    {
        using var cameraPolicy = Registry.LocalMachine.OpenSubKey(CameraPolicyKey);
        if (cameraPolicy?.GetValue("AllowCamera") is int allowed && allowed == 0)
            return PermissionState.Managed;
        using var appPrivacy = Registry.LocalMachine.OpenSubKey(AppPrivacyPolicyKey);
        if (appPrivacy?.GetValue("LetAppsAccessCamera") is int access && access == 2)
            return PermissionState.Managed;
        using var consent = Registry.CurrentUser.OpenSubKey(TeamsConsentKey);
        return (consent?.GetValue("Value") as string) switch
        {
            "Allow" => PermissionState.On,
            "Deny" => PermissionState.Off,
            _ => PermissionState.Unknown
        };
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

    private enum PermissionState
    {
        Unknown,
        Off,
        On,
        Managed
    }
}
