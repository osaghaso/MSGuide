using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class CameraTargetRevalidationTests
{
    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new WindowChoice((nint)1234, 42, "Synthetic Camera settings", "SyntheticClass");
        var bounds = new Native.RECT { Left = -900, Top = 20, Right = 100, Bottom = 720 };
        var original = new ElementInfo("checkbox", "Camera access", [0.1, 0.2, 0.2, 0.1],
            TargetId: "old-fingerprint", AutomationId: CameraRecoveryPinnedTargets.DeviceCameraToggle,
            FrameworkId: "WPF", ToggleState: "off", ControlId: "same-control");
        CameraTargetMatch Match(params ElementInfo[] current) => CameraTargetRevalidation.Match(
            window, bounds, original, CameraRecoveryTargetKind.DeviceCameraPermission, now,
            window, bounds, current, now);

        var redrawn = original with
        {
            Label = "Camera access - available to permitted apps",
            Box = [0.12, 0.24, 0.2, 0.1], TargetId = "fresh-fingerprint"
        };
        IntegrationTests.Require(Match(original).Finding == CameraTargetFinding.Ready
            && Match(redrawn) is { Finding: CameraTargetFinding.Ready, Element.TargetId: "fresh-fingerprint" }
            && redrawn.ControlId == original.ControlId && redrawn.TargetId != original.TargetId);
        IntegrationTests.Require(Match(redrawn with { ToggleState = "on" }).Finding == CameraTargetFinding.AlreadyEnabled);
        foreach (var invalid in new[]
        {
            redrawn with { ControlId = "replacement-control" },
            redrawn with { ControlId = null }, redrawn with { TargetId = null },
            redrawn with { AutomationId = CameraRecoveryPinnedTargets.AppCameraToggle },
            redrawn with { Role = "edit" }, redrawn with { FrameworkId = "other-provider" },
            redrawn with { IsEnabled = false }, redrawn with { Targetable = false },
            redrawn with { IsPassword = true }, redrawn with { IsOffscreen = true },
            redrawn with { ToggleState = "unknown" }, redrawn with { Box = [double.NaN, 0, 1, 1] }
        })
            IntegrationTests.Require(Match(invalid).Finding is not (CameraTargetFinding.Ready or CameraTargetFinding.AlreadyEnabled));
        IntegrationTests.Require(Match().Finding == CameraTargetFinding.IdentityChanged
            && Match(redrawn, redrawn).Finding == CameraTargetFinding.IdentityChanged);
        foreach (var changedWindow in new[]
        {
            window with { Handle = (nint)2222 }, window with { ProcessId = 43 },
            window with { ClassName = "replaced" }, window with { Title = "Another resource" }
        })
            IntegrationTests.Require(CameraTargetRevalidation.Match(window, bounds, original,
                CameraRecoveryTargetKind.DeviceCameraPermission, now, changedWindow, bounds, [redrawn], now)
                .Finding == CameraTargetFinding.WindowChanged);
        var movedBounds = bounds;
        movedBounds.Left++;
        IntegrationTests.Require(CameraTargetRevalidation.Match(window, bounds, original,
            CameraRecoveryTargetKind.DeviceCameraPermission, now, window, movedBounds, [redrawn], now)
            .Finding == CameraTargetFinding.WindowChanged);
        IntegrationTests.Require(CameraTargetRevalidation.Match(window, bounds, original,
            CameraRecoveryTargetKind.DeviceCameraPermission, now.AddSeconds(-60), window, bounds, [redrawn], now)
            .Finding == CameraTargetFinding.Expired);
        var camera = original with
        {
            AutomationId = "camera-toggle", Label = "Turn camera on (Ctrl+Shift+O)", Role = "button"
        };
        var enabled = camera with { Label = "Turn camera off (Ctrl+Shift+O)", TargetId = "camera-now-on" };
        IntegrationTests.Require(CameraTargetRevalidation.Match(window, bounds, camera,
            CameraRecoveryTargetKind.TeamsCameraButton, now, window, bounds, [enabled], now)
            .Finding == CameraTargetFinding.AlreadyEnabled);
        IntegrationTests.Require(CameraTargetRevalidation.Match(window, bounds, camera,
            CameraRecoveryTargetKind.TeamsCameraButton, now, window, bounds,
            [camera with { Label = "Turn microphone on" }], now).Finding == CameraTargetFinding.IdentityChanged);
        var satisfied = await DesktopAction.RunBounded((_, _) =>
            new(false, true, "Synthetic already-enabled observation.", StateAlreadySatisfied: true), CancellationToken.None);
        IntegrationTests.Require(!satisfied.Invoked && satisfied.OutcomeKnown && satisfied.StateAlreadySatisfied);
    }

    internal static async Task RunNativeAsync(CancellationToken ct)
    {
        var toggle = new CheckBox
        {
            Content = "Camera access", IsChecked = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetAutomationId(toggle, CameraRecoveryPinnedTargets.DeviceCameraToggle);
        var panel = new Grid();
        panel.Children.Add(toggle);
        var fixture = new Window
        {
            Title = "MSGuide owned revalidation fixture", Width = 520, Height = 360,
            Content = panel, ShowActivated = false
        };
        int toggles = 0;
        toggle.Checked += (_, _) => toggles++;
        toggle.Unchecked += (_, _) => toggles++;
        try
        {
            fixture.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var window = new WindowChoice(new WindowInteropHelper(fixture).Handle,
                (uint)Environment.ProcessId, fixture.Title);
            using var observed = await CaptureService.InspectCameraControls(window, ct);
            var original = observed.Elements.Single(element =>
                element.AutomationId == CameraRecoveryPinnedTargets.DeviceCameraToggle);
            toggle.Margin = new Thickness(45, 25, 0, 0);
            toggle.Content = "Camera access - synthetic description updated";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            using var refreshed = await CaptureService.InspectCameraControls(window, ct);
            var fresh = refreshed.Elements.Single(element => element.AutomationId == original.AutomationId);
            IntegrationTests.Require(observed.Rect.Same(refreshed.Rect) && original.ControlId == fresh.ControlId
                && original.TargetId != fresh.TargetId
                && !refreshed.Elements.Any(element => element.TargetId == original.TargetId));
            var match = CameraTargetRevalidation.Match(window, observed.Rect, original,
                CameraRecoveryTargetKind.DeviceCameraPermission, observed.CapturedAt, window, refreshed.Rect,
                refreshed.Elements, DateTimeOffset.UtcNow);
            IntegrationTests.Require(match.Finding == CameraTargetFinding.Ready && match.Element == fresh);
            var result = await DesktopAction.RunBounded((token, beginInvocation) =>
            {
                var raw = AutomationEvidence.FindUniqueTarget(window, refreshed.Rect, fresh.TargetId!,
                    fresh.Label, fresh.AutomationId, token);
                IntegrationTests.Require(raw is not null && window.ProcessId == Environment.ProcessId
                    && raw.Current.ProcessId == Environment.ProcessId);
                var pattern = raw!.GetCurrentPattern(TogglePattern.Pattern) as TogglePattern;
                IntegrationTests.Require(pattern is not null && pattern.Current.ToggleState == ToggleState.Off);
                if (!beginInvocation()) return new(false, true, "Synthetic toggle cancelled.");
                pattern!.Toggle();
                return new(true, true, "Owned synthetic control toggled once.");
            }, ct);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            IntegrationTests.Require(result.Invoked && result.OutcomeKnown && toggles == 1 && toggle.IsChecked == true);
            using var enabled = await CaptureService.InspectCameraControls(window, ct);
            IntegrationTests.Require(CameraTargetRevalidation.Match(window, observed.Rect, original,
                CameraRecoveryTargetKind.DeviceCameraPermission, observed.CapturedAt, window, enabled.Rect,
                enabled.Elements, DateTimeOffset.UtcNow).Finding == CameraTargetFinding.AlreadyEnabled);
            panel.Children.Clear();
            var replacement = new CheckBox { Content = "Camera access", IsChecked = false };
            AutomationProperties.SetAutomationId(replacement, CameraRecoveryPinnedTargets.DeviceCameraToggle);
            panel.Children.Add(replacement);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            using var replaced = await CaptureService.InspectCameraControls(window, ct);
            IntegrationTests.Require(CameraTargetRevalidation.Match(window, observed.Rect, original,
                CameraRecoveryTargetKind.DeviceCameraPermission, observed.CapturedAt, window, replaced.Rect,
                replaced.Elements, DateTimeOffset.UtcNow).Finding == CameraTargetFinding.IdentityChanged
                && toggles == 1 && replacement.IsChecked == false);
        }
        finally { fixture.Close(); }
    }
}
