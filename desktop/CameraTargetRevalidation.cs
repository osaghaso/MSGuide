namespace MSGuide.Desktop;

internal enum CameraTargetFinding { Ready, AlreadyEnabled, WindowChanged, Expired, IdentityChanged, Unavailable }

internal sealed record CameraTargetMatch(CameraTargetFinding Finding, ElementInfo? Element, string Detail);

internal static class CameraTargetRevalidation
{
    internal static CameraTargetMatch Match(
        WindowChoice approvedWindow, Native.RECT approvedBounds, ElementInfo approved,
        CameraRecoveryTargetKind scope, DateTimeOffset observedAt,
        WindowChoice currentWindow, Native.RECT currentBounds, IReadOnlyList<ElementInfo> elements,
        DateTimeOffset now)
    {
        if (approvedWindow.Id != currentWindow.Id || approvedWindow.ClassName != currentWindow.ClassName
            || approvedWindow.Title != currentWindow.Title || !approvedBounds.Same(currentBounds))
            return new(CameraTargetFinding.WindowChanged, null,
                "The camera window's identity, title, or bounds no longer match the observation. No action was started.");
        if (!Safety.Fresh(observedAt, now))
            return new(CameraTargetFinding.Expired, null,
                "The camera observation expired before approval. No action was started; check the camera again.");
        if (string.IsNullOrWhiteSpace(approved.ControlId))
            return new(CameraTargetFinding.IdentityChanged, null,
                "The camera control has no stable accessibility identity. No action was started.");
        var matches = elements.Where(element => element.ControlId == approved.ControlId).ToArray();
        if (matches.Length != 1)
            return new(CameraTargetFinding.IdentityChanged, null,
                "Teams or Windows replaced the camera control, or its identity is ambiguous. No action was started.");
        var current = matches[0];
        if (current.Role != approved.Role || current.AutomationId != approved.AutomationId
            || current.FrameworkId != approved.FrameworkId || string.IsNullOrWhiteSpace(current.TargetId)
            || !Safety.ValidBox(current.Box) || current.IsPassword || current.IsOffscreen
            || !MatchesScope(current, scope))
            return new(CameraTargetFinding.IdentityChanged, null,
                "The accessible control no longer matches the approved camera scope. No action was started.");
        if (IsOn(current, scope))
            return new(CameraTargetFinding.AlreadyEnabled, current,
                "The approved camera control is already on. No toggle was sent; checking the current camera state.");
        if (!current.IsEnabled || !current.Targetable || !IsOff(current, scope))
            return new(CameraTargetFinding.Unavailable, null,
                "The approved camera control is not currently enabled, targetable, and off. No action was started.");
        return new(CameraTargetFinding.Ready, current,
            "The same approved camera control was freshly revalidated.");
    }

    internal static bool MatchesScope(ElementInfo element, CameraRecoveryTargetKind scope) =>
        scope == CameraRecoveryTargetKind.TeamsCameraButton
            ? CameraRecoveryPinnedTargets.IsTeamsCameraElement(element)
            : CameraRecoveryPinnedTargets.IsPermission(scope)
                && CameraRecoveryPinnedTargets.IsPermissionTarget(element.AutomationId, scope)
                && element.Role is "button" or "checkbox";

    internal static bool IsOff(ElementInfo element, CameraRecoveryTargetKind scope) =>
        MatchesScope(element, scope) && (CameraRecoveryPinnedTargets.IsPermission(scope)
            ? element.ToggleState == "off"
            : CameraRecoveryPinnedTargets.IsTeamsTurnCameraOn(element.Label)
                || CameraRecoveryPinnedTargets.IsTeamsCameraToggle(element.Label) && element.ToggleState == "off");

    internal static bool IsOn(ElementInfo element, CameraRecoveryTargetKind scope) =>
        MatchesScope(element, scope) && (CameraRecoveryPinnedTargets.IsPermission(scope)
            ? element.ToggleState == "on"
            : CameraRecoveryPinnedTargets.IsTeamsTurnCameraOff(element.Label)
                || CameraRecoveryPinnedTargets.IsTeamsCameraToggle(element.Label) && element.ToggleState == "on");
}
