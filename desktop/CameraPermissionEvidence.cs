namespace MSGuide.Desktop;

internal enum CameraPermissionState { Unknown, Off, On, Managed }

internal sealed record CameraPermissionEvidence(
    CameraPermissionState State, CameraRecoveryTargetKind Scope, string Detail)
{
    public static CameraPermissionEvidence FromConsent(
        bool policyDenied, string? device, string? apps, string? teams)
    {
        if (policyDenied)
            return new(CameraPermissionState.Managed, CameraRecoveryTargetKind.Unknown,
                "Camera access is blocked by policy. MSGuide will not override it.");

        foreach (var (value, scope) in new[]
        {
            (device, CameraRecoveryTargetKind.DeviceCameraPermission),
            (apps, CameraRecoveryTargetKind.AppCameraPermission),
            (teams, CameraRecoveryTargetKind.PackagedTeamsPermission)
        })
        {
            if (value == "Deny")
                return new(CameraPermissionState.Off, scope, DescribeBlock(scope));
            if (value != "Allow")
                return new(CameraPermissionState.Unknown, scope,
                    "Windows camera permissions could not all be established. Inspect Camera settings before attempting a camera action.");
        }
        return new(CameraPermissionState.On, CameraRecoveryTargetKind.Unknown,
            "Device, app, and Microsoft Teams camera permissions are on.");
    }

    public static string DescribeBlock(CameraRecoveryTargetKind scope) => scope switch
    {
        CameraRecoveryTargetKind.DeviceCameraPermission =>
            "Windows device-wide Camera access is off. This blocks Teams even when its own permission is on. Enabling it permits camera access for other apps that already have permission; separate approval is required.",
        CameraRecoveryTargetKind.AppCameraPermission =>
            "Let apps access your camera is off. Enabling it affects other permitted apps for this user, not only Teams; separate approval is required.",
        CameraRecoveryTargetKind.PackagedTeamsPermission =>
            "The individual Microsoft Teams camera permission is off.",
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };
}
