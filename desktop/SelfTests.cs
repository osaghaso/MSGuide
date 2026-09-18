namespace MSGuide.Desktop;

internal static class SelfTests
{
    public static void Run()
    {
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Desktop safety self-test failed."); }
        Check(DemoTaskSession.Next(0)?.Label == "View logs");
        Check(DemoTaskSession.Next(1)?.Label == "Open troubleshooting");
        Check(DemoTaskSession.Next(2) is null && DemoTaskSession.Next(3) is null && DemoTaskSession.Next(-1) is null);
        string? previousShareable = Environment.GetEnvironmentVariable("MSGUIDE_ALLOW_SCREEN_SHARE");
        try
        {
            Environment.SetEnvironmentVariable("MSGUIDE_ALLOW_SCREEN_SHARE", null);
            Check(!Native.ShareableDemo
                && Native.MSGuideDisplayAffinity == Native.DisplayAffinityExcludeFromCapture);
            Environment.SetEnvironmentVariable("MSGUIDE_ALLOW_SCREEN_SHARE", "1");
            Check(Native.ShareableDemo
                && Native.MSGuideDisplayAffinity == Native.DisplayAffinityNone);
            Environment.SetEnvironmentVariable("MSGUIDE_ALLOW_SCREEN_SHARE", "true");
            Check(!Native.ShareableDemo
                && Native.MSGuideDisplayAffinity == Native.DisplayAffinityExcludeFromCapture);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSGUIDE_ALLOW_SCREEN_SHARE", previousShareable);
        }
        (string Message, string Code)[] captureFailures =
        [
            ("A previous window capture is still returning. Use another application after it finishes, or restart MSGuide.", "provider-busy"),
            ("Window capture timed out. No snapshot was sent. This application may not support Windows Graphics Capture/UI Automation.", "timeout"),
            ("Selected window disappeared, changed, is minimized, or is not responding. Refresh the chooser.", "window-unavailable"),
            ("Unsupported window dimensions. Resize the selected window and retry.", "unsupported-dimensions"),
            ("PNG exceeds the 2 MB limit; select a smaller window.", "image-too-large"),
            ("Window changed during capture. Capture and review again.", "window-changed"),
            ("Cannot allocate a window capture context.", "context-allocation"),
            ("Cannot allocate a window bitmap.", "bitmap-allocation"),
            ("This window does not support PrintWindow capture. No desktop fallback is used.", "unsupported"),
            ("Windows Graphics Capture is unavailable. No desktop or PrintWindow fallback is used.", "wgc-unavailable"),
            ("Windows Graphics Capture did not return a frame. Nothing was sent.", "wgc-frame"),
            ("Capture appears blank, protected, or unsupported. Nothing was sent. Try the built-in demo; there is no desktop fallback.", "blank")
        ];
        foreach (var (message, code) in captureFailures)
        {
            var failure = new CaptureTests.CaptureFailure(new InvalidOperationException(message));
            Check(failure.Message == code && failure.InnerException is null);
            Check(new CaptureTests.CaptureFailure(new InvalidOperationException(message + " synthetic external text")).Message == "unclassified");
        }
        Check(new CaptureTests.CaptureFailure(new InvalidOperationException("synthetic external text")).Message == "unclassified");
        foreach (string url in new[] { "http://127.0.0.1:8000", "http://localhost:8000", "http://[::1]:8000" })
            Check(Safety.ApiUri(url).IsLoopback);
        foreach (string url in new[] { "https://127.0.0.1", "http://example.com", "http://127.0.0.1.evil.test", "http://user:secret@127.0.0.1", "http://127.0.0.1/api", "file:///tmp", "http://127.0.0.1/?token=x" })
        {
            bool rejected = false;
            try { Safety.ApiUri(url); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
        }
        Check(Safety.ValidBox([0, 0, 1, 1]));
        foreach (double[] b in new double[][] { [0.8, 0, 0.3, 1], [-0.1, 0, 0.2, 1], [0, 0, 0, 1], [double.NaN, 0, 1, 1], [0, 0, double.PositiveInfinity, 1], [0, 1] })
            Check(!Safety.ValidBox(b));
        // Negative desktop origin, then mixed-DPI-sized physical rectangles. No primary DPI assumption.
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
        {
            var rect = new Native.RECT { Left = -1920, Top = -200, Right = -1920 + (int)(1200 * dpi), Bottom = -200 + (int)(800 * dpi) };
            var target = Safety.PhysicalTarget(rect, [0.25, 0.5, 0.1, 0.2]);
            Check(Math.Abs(target.X - (-1920 + 300 * dpi)) < 0.001);
            Check(Math.Abs(target.Y - (-200 + 400 * dpi)) < 0.001);
            Check(Math.Abs(target.Width - 120 * dpi) < 0.001);
            Check(Math.Abs(target.Height - 160 * dpi) < 0.001);
        }
        var now = DateTimeOffset.UtcNow;
        Check(Safety.Fresh(now.AddSeconds(-59), now));
        Check(!Safety.Fresh(now.AddSeconds(-60), now));
        Check(!Safety.Fresh(now.AddSeconds(1), now));
        Check(Safety.CitationUri("https://learn.microsoft.com/") is not null);
        foreach (string source in new[] { "javascript:alert(1)", "file:///C:/Windows", "http://example.com", "https://user:pass@example.com" })
            Check(Safety.CitationUri(source) is null);
        var good = new Guidance("correlation", "observation", "window", "View logs", "next_step", null, [], "demo");
        Check(Safety.Matches(good, "observation", "window"));
        Check(!Safety.Matches(good, "old", "window"));
        Check(!Safety.Matches(good, "observation", "other"));
        Check(!Safety.Matches(good with { Mode = "unknown" }, "observation", "window"));
        Check(!Safety.Matches(good with { Status = "execute" }, "observation", "window"));
        var workArea = new Native.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1040 };
        var placed = CompanionPlacement.NearCursor(
            new Native.POINT { X = -20, Y = 1020 }, workArea, 390, 154);
        Check(placed.Left == -432 && placed.Top == 854 && placed.Right == -42 && placed.Bottom == 1008);
        placed = CompanionPlacement.NearCursor(
            new Native.POINT { X = -1910, Y = 10 }, workArea, 390, 154);
        Check(placed.Left == -1888 && placed.Top == 22);
        Check(CursorCompanionWindow.TaskText(
            ["✓ 1. invoke “Open”", "✓ 2. select “Details”"],
            "Thinking about action 3…") ==
            "Thinking about action 3…\n✓ 1. invoke “Open”\n✓ 2. select “Details”");
        var element = new ElementInfo("button", "View logs", [0.1, 0.2, 0.3, 0.1]);
        var targetInfo = new TargetInfo(element.Label, element.Box, 0.95, Action: null);
        Check(Safety.ObservedTarget(targetInfo, [element]));
        Check(!Safety.ObservedTarget(targetInfo, [element with { IsEnabled = false, Targetable = false }]));
        Check(!Safety.ObservedTarget(targetInfo with { Label = "Unobserved" }, [element]));
        Check(!Safety.ObservedTarget(targetInfo with { Box = [0.2, 0.2, 0.3, 0.1] }, [element]));
        Check(!Safety.ObservedTarget(targetInfo with { Confidence = 0.79 }, [element]));
        Check(!Safety.Matches(good with { Status = "completed", Target = targetInfo }, "observation", "window"));
        Check(AutomationEvidence.ActionName(true, true, true, true, "off") == "toggle");
        Check(AutomationEvidence.ActionName(false, true, true, true, null) == "invoke");
        Check(AutomationEvidence.ActionName(false, false, true, true, null) == "select");
        Check(AutomationEvidence.ActionName(false, false, false, true, "collapsed") == "expand");
        Check(AutomationEvidence.ActionName(false, false, false, true, "expanded") == "collapse");
        Check(AutomationEvidence.ActionName(false, false, false, false, null) is null);
        var actionableElement = element with { TargetId = "uia-action", Action = "invoke" };
        var actionableTarget = targetInfo with { TargetId = "uia-action", Action = "invoke" };
        Check(Safety.ObservedTarget(actionableTarget, [actionableElement]));
        Check(!Safety.ObservedTarget(actionableTarget with { TargetId = "uia-other" }, [actionableElement]));
        Check(!Safety.ObservedTarget(actionableTarget with { Action = "toggle" }, [actionableElement]));
        Check(MainWindow.CanAutoExecuteScreenAction(true, true, actionableTarget, CameraRecoveryInteractionMode.Control));
        Check(!MainWindow.CanAutoExecuteScreenAction(false, true, actionableTarget, CameraRecoveryInteractionMode.Control));
        Check(!MainWindow.CanAutoExecuteScreenAction(true, false, actionableTarget, CameraRecoveryInteractionMode.Control));
        Check(!MainWindow.CanAutoExecuteScreenAction(true, true, targetInfo, CameraRecoveryInteractionMode.Control));
        Check(!MainWindow.CanAutoExecuteScreenAction(true, true, actionableTarget, CameraRecoveryInteractionMode.Guide));
        Check(!Safety.ObservedTarget(actionableTarget, [actionableElement, actionableElement]));
        Check(!Safety.ObservedTarget(actionableTarget, [actionableElement with { IsPassword = true }]));
        Check(!Safety.ObservedTarget(actionableTarget, [actionableElement with { IsOffscreen = true }]));
        string valueHash = AutomationEvidence.ValueDigest("");
        var writable = actionableElement with
        { Action = "set_value", IsReadOnly = false, ValueHash = valueHash, ValueLength = 0 };
        var write = actionableTarget with { Action = "set_value", Value = "Synthetic", ValueHash = valueHash };
        Check(Safety.ObservedTarget(write, [writable]));
        Check(Safety.ObservedTarget(write with { Value = "" }, [writable]));
        Check(!Safety.ObservedTarget(write, [writable with { IsReadOnly = true }]));
        Check(!Safety.ObservedTarget(write, [writable with { ValueHash = AutomationEvidence.ValueDigest("changed") }]));
        Check(!Safety.ObservedTarget(write with { Value = new string('x', 1001) }, [writable]));
        Check(!Safety.ObservedTarget(write with { Value = "\0" }, [writable]));
        var scrolling = actionableElement with { Action = "scroll", ScrollDirections = ["down"] };
        var scroll = actionableTarget with { Action = "scroll", ScrollDirection = "down" };
        Check(Safety.ObservedTarget(scroll, [scrolling]));
        Check(!Safety.ObservedTarget(scroll with { ScrollDirection = "left" }, [scrolling]));
        Check(!Safety.ObservedTarget(scroll with { Value = "unexpected" }, [scrolling]));
        Check(AutomationEvidence.ScrollDirections(-1, 0).SequenceEqual(["down"]));
        Check(AutomationEvidence.ScrollDirections(100, 100).SequenceEqual(["left", "up"]));
        Check(AutomationEvidence.ScrollDirections(double.NaN, -1).Length == 0);
        Check(Safety.GuidanceBudget(now, now) == TimeSpan.FromSeconds(54));
        Check(Safety.GuidanceBudget(now.AddSeconds(-53.5), now) == TimeSpan.FromMilliseconds(500));
        Check(Safety.GuidanceBudget(now.AddSeconds(-54), now) == TimeSpan.Zero);
        Check(MainWindow.PreserveScreenActionApproval(1, 1, true,
            CameraRecoveryInteractionMode.Control, actionableTarget));
        Check(!MainWindow.PreserveScreenActionApproval(1, 1, true,
            CameraRecoveryInteractionMode.Guide, actionableTarget));
        Check(!MainWindow.PreserveScreenActionApproval(2, 1, true,
            CameraRecoveryInteractionMode.Control, actionableTarget));
        var identity = new WindowChoice(new nint(0x1234), 42, "Original title", "Chrome_WidgetWin_1");
        Check(identity.SameIdentity(42, "Chrome_WidgetWin_1"));
        Check(!identity.SameIdentity(43, "Chrome_WidgetWin_1"));
        Check(!identity.SameIdentity(42, "ApplicationFrameWindow"));
        string stableTarget = AutomationEvidence.TargetId(identity, "button", "Camera",
            [0.1, 0.2, 0.3, 0.1], "camera-toggle", "Chrome", 16836, [1, 2, 3]);
        Check(stableTarget == AutomationEvidence.TargetId(identity, "button", "Camera",
            [0.1, 0.2, 0.3, 0.1], "camera-toggle", "Chrome", 16836, [1, 2, 3]));
        Check(stableTarget != AutomationEvidence.TargetId(identity, "button", "Camera",
            [0.1, 0.2, 0.3, 0.1], "camera-toggle", "Chrome", 16836, [1, 2, 4]));
        Check(stableTarget != AutomationEvidence.TargetId(identity, "button", "Camera",
            [0.1, 0.2, 0.3, 0.1], "camera-toggle", "Chrome", 4444, [1, 2, 3]));
        Check(stableTarget.StartsWith("uia-") && stableTarget.Length == 28
            && !stableTarget.Contains("camera", StringComparison.OrdinalIgnoreCase));
        var cameraGlobal = new ElementInfo("button", "Camera access", [0.1, 0.1, 0.3, 0.1],
            AutomationId: "SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch",
            ToggleState: "on");
        var appGlobal = new ElementInfo("button", "Let apps access your camera", [0.1, 0.3, 0.3, 0.1],
            AutomationId: "SystemSettings_CapabilityAccess_Camera_UserGlobal_ToggleSwitch",
            ToggleState: "on");
        var teamsPermission = new ElementInfo("button", "Microsoft Teams", [0.1, 0.5, 0.3, 0.1],
            AutomationId: "MSTeams_8wekyb3d8bbwe_ToggleSwitch", ToggleState: "off");
        Check(Safety.VerifiedCameraSettingsPage([cameraGlobal, appGlobal, teamsPermission]));
        Check(!Safety.VerifiedCameraSettingsPage([cameraGlobal, teamsPermission]));
        Check(Safety.PackagedTeamsCameraPermission([cameraGlobal, teamsPermission]) == teamsPermission);
        Check(Safety.PackagedTeamsCameraPermission(
            [teamsPermission with { IsEnabled = false, Targetable = false }]) is null);
        Check(Safety.VerifiedTeamsDevicesPage(
            [new("group", "Video settings", [0.1, 0.1, 0.3, 0.1], AutomationId: "VideoSettings")]));
        Check(!Safety.VerifiedTeamsDevicesPage(
            [new("group", "Audio settings", [0.1, 0.1, 0.3, 0.1], AutomationId: "AudioSettings")]));
        var windowBounds = new Native.RECT { Left = -100, Top = 20, Right = 700, Bottom = 620 };
        Check(Safety.AutomationBox(new System.Windows.Rect(800, 20, 10, 10), windowBounds) is null);
        Check(Safety.AutomationBox(System.Windows.Rect.Empty, windowBounds) is null);
        Check(Safety.AutomationBox(new System.Windows.Rect(-110, 10, 810, 610), windowBounds)!.SequenceEqual(new double[] { 0, 0, 1, 1 }));
        Check(Safety.AutomationBox(new System.Windows.Rect(700, 20, 10, 10), windowBounds) is null);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool rejected = false;
            try { CaptureService.Capture(new WindowChoice(0, 0, "Synthetic"), cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected);
        }
        byte[] syntheticBytes = [1, 2, 3];
        using (var disposed = new Snapshot(new WindowChoice(0, 0, "Synthetic"), windowBounds, now,
            syntheticBytes, null!, [element], "Synthetic", ""))
        {
            disposed.Dispose();
            Check(!disposed.Valid() && syntheticBytes.All(b => b == 0) && disposed.Text.Length == 0 && disposed.Elements.Length == 0);
            bool rejected = false;
            try { disposed.Observation(false); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected);
        }
        var observationElement = new ElementInfo("button", "View logs", [0.1, 0.2, 0.3, 0.1],
            TargetId: stableTarget, AutomationId: "DemoStep0", FrameworkId: "WPF",
            IsEnabled: false, Targetable: false, ToggleState: "off", HelpText: "Synthetic help");
        var observation = new Observation(Guid.NewGuid().ToString(), "42:ABCD", "MSGuide Demo", now,
            800, 600, "View logs", [observationElement], null);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        string payload = System.Text.Json.JsonSerializer.Serialize(new GuidanceRequest("session", "Help", true, observation), options);
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var root = json.RootElement;
        Check(root.GetProperty("consent").GetBoolean());
        Check(root.GetProperty("sessionId").GetString() == "session");
        var wireObservation = root.GetProperty("observation");
        Check(!wireObservation.TryGetProperty("imageBase64", out _));
        Check(wireObservation.GetProperty("application").GetString() == "MSGuide Demo");
        Check(wireObservation.GetProperty("elements")[0].GetProperty("role").GetString() == "button");
        Check(wireObservation.GetProperty("elements")[0].GetProperty("confidence").GetDouble() == 0.95);
        Check(wireObservation.GetProperty("elements")[0].GetProperty("targetId").GetString() == stableTarget);
        Check(wireObservation.GetProperty("elements")[0].GetProperty("automationId").GetString() == "DemoStep0");
        Check(wireObservation.GetProperty("elements")[0].GetProperty("frameworkId").GetString() == "WPF");
        Check(!wireObservation.GetProperty("elements")[0].GetProperty("isEnabled").GetBoolean());
        Check(!wireObservation.GetProperty("elements")[0].GetProperty("targetable").GetBoolean());
        Check(wireObservation.GetProperty("elements")[0].GetProperty("toggleState").GetString() == "off");
        Check(wireObservation.GetProperty("elements")[0].GetProperty("helpText").GetString() == "Synthetic help");
        Check(!wireObservation.GetProperty("elements")[0].TryGetProperty("itemStatus", out _));
        Check(System.Text.Json.JsonSerializer.Serialize(observation with { ImageBase64 = "AQ==" }, options).Contains("\"imageBase64\":\"AQ==\""));
        var probe = new CaptureProbeReport(1, now, "42:ABCD", 42, "SyntheticClass", 800, 600,
            [new("PrintWindow(0)", 0, true, true, "accepted", 10, 240, 12)],
            new(true, 10, 8, 6, 2, 4, 1, false, "completed", "camera-privacy",
                new Dictionary<string, int> { ["button"] = 2 },
                [new("SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch",
                    true, false, "on")]));
        string probeJson = System.Text.Json.JsonSerializer.Serialize(probe, options);
        Check(probeJson.Contains("\"outcome\":\"accepted\"") && !probeJson.Contains("image", StringComparison.OrdinalIgnoreCase)
            && !probeJson.Contains("base64", StringComparison.OrdinalIgnoreCase)
            && !probeJson.Contains("pixel", StringComparison.OrdinalIgnoreCase));
        Check(probeJson.Contains("\"verifiedPage\":\"camera-privacy\"")
            && probeJson.Contains("SystemSettings_CapabilityAccess_Camera_SystemGlobal_ToggleSwitch"));
    }
}