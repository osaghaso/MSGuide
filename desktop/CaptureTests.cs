using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class CaptureTests
{
    internal sealed class AssertionFailure(int line) : InvalidOperationException($"Capture assertion at source line {line}.");

    // Exact CaptureService literals only: never retain an external message or inner exception.
    internal sealed class CaptureFailure(InvalidOperationException error) : InvalidOperationException(error.Message switch
    {
        "A previous window capture is still returning. Use another application after it finishes, or restart MSGuide." => "provider-busy",
        "Window capture timed out. No snapshot was sent. This application may not support Windows Graphics Capture/UI Automation." => "timeout",
        "Selected window disappeared, changed, is minimized, or is not responding. Refresh the chooser." => "window-unavailable",
        "Unsupported window dimensions. Resize the selected window and retry." => "unsupported-dimensions",
        "PNG exceeds the 2 MB limit; select a smaller window." => "image-too-large",
        "Window changed during capture. Capture and review again." => "window-changed",
        "The selected resource changed during capture. Review it before continuing." => "resource-changed",
        "Cannot allocate a window capture context." => "context-allocation",
        "Cannot allocate a window bitmap." => "bitmap-allocation",
        "This window does not support PrintWindow capture. No desktop fallback is used." => "unsupported",
        "Windows Graphics Capture is unavailable. No desktop or PrintWindow fallback is used." => "wgc-unavailable",
        "Windows Graphics Capture did not return a frame. Nothing was sent." => "wgc-frame",
        "Capture appears blank, protected, or unsupported. Nothing was sent. Try the built-in demo; there is no desktop fallback." => "blank",
        _ => "unclassified"
    });

    private static async Task<Snapshot> Capture(WindowChoice window, CancellationToken ct)
    {
        try { return await CaptureService.Capture(window, ct); }
        catch (InvalidOperationException ex) { throw new CaptureFailure(ex); }
    }

    private static void Require(bool condition, [CallerLineNumber] int line = 0)
    {
        if (!condition) throw new AssertionFailure(line);
    }

    private static async Task Idle(CancellationToken ct)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ct.ThrowIfCancellationRequested();
    }

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    internal static void RunEvidenceChecks()
    {
        Require(AutomationEvidence.CaptureScanMilliseconds > AutomationEvidence.ScanMilliseconds
            && AutomationEvidence.CaptureScanMilliseconds < 30_000);
        Require(TreeWalker.ControlViewWalker is not null);
        var context = Enumerable.Range(0, 250).Select(index =>
            new ElementInfo("text", $"Synthetic text {index}", [0, 0, 0.1, 0.1],
                Targetable: false)).ToArray();
        var button = new ElementInfo("button", "Synthetic action", [0.1, 0.2, 0.3, 0.1],
            TargetId: "uia-action", Action: "invoke", ControlId: "control-action");
        var document = new ElementInfo("document", "Synthetic document", [0, 0, 1, 1], Targetable: false);
        var bounded = CaptureService.BoundEvidence([.. context, document, button], "", "", true);
        Require(bounded.Complete && bounded.ContextTruncated && bounded.Elements.Length == 200
            && bounded.Elements.Contains(button) && bounded.Elements.Contains(document));
        bool rejected = false;
        try { bounded.RequireComplete(); } catch (IncompleteAutomationReadException) { rejected = true; }
        Require(rejected); // Camera diagnoses still require complete context, not only retained controls.
        var controls = Enumerable.Range(0, 201).Select(index => button with
        { TargetId = $"uia-{index}", ControlId = $"control-{index}" }).ToArray();
        Require(!CaptureService.BoundEvidence(controls, "", "", true).Complete);
        Require(!CaptureService.BoundEvidence([button], "", "", false).Complete);
        var shortenedText = CaptureService.BoundEvidence([button], "", "", true, textTruncated: true);
        Require(shortenedText.Complete && shortenedText.ContextTruncated);
        Require(CaptureService.BoundEvidence([button], "", "", true).RequireComplete().Single() == button);

        var window = new WindowChoice(new nint(0x1234), 42, "Synthetic browser", "Chrome_WidgetWin_1");
        string documentId = AutomationEvidence.ControlId(window, 42, [1, 2, 3])!;
        string? first = AutomationEvidence.BrowserResourceId(window, "https://example.test/#first", documentId);
        Require(first is not null && first.StartsWith("browser-", StringComparison.Ordinal)
            && !first.Contains("example", StringComparison.Ordinal)
            && first == AutomationEvidence.BrowserResourceId(window, "https://example.test/#first", documentId)
            && first == AutomationEvidence.BrowserResourceId(window, "example.test/#first", documentId)
            && first != AutomationEvidence.BrowserResourceId(window, "https://example.test/#second", documentId)
            && first != AutomationEvidence.BrowserResourceId(window, "https://example.test/#first",
                AutomationEvidence.ControlId(window, 42, [1, 2, 4])!)
            && first != AutomationEvidence.BrowserResourceId(window with { Handle = new nint(0x5678) },
                "https://example.test/#first", documentId));
        Require(AutomationEvidence.BrowserAddressKey("http://127.0.0.1:8877/task?run=1")
            == AutomationEvidence.BrowserAddressKey("127.0.0.1:8877/task?run=1"));
        Require(AutomationEvidence.BrowserAddressKey("http://example.test:443/path")
            == AutomationEvidence.BrowserAddressKey("example.test:443/path")
            && AutomationEvidence.BrowserAddressKey("example.test:443/path")
                != AutomationEvidence.BrowserAddressKey("example.test/path"));
        Require(AutomationEvidence.BrowserResourceId(window, "https://example.test/", "") is null);
        foreach (string unsupported in new[] { "", "not a web address", "file:///C:/test", "javascript:alert(1)",
                     "https://user:password@example.test/", new string('x', 2049) })
            Require(AutomationEvidence.BrowserResourceId(window, unsupported, documentId) is null);
        Require(AutomationEvidence.IsBrowserAddressControl("edit", "Address and search bar", false));
        Require(!AutomationEvidence.IsBrowserAddressControl("edit", "Address and search bar", true));
        Require(!AutomationEvidence.IsBrowserAddressControl("text", "Address and search bar", false));
        Require(!AutomationEvidence.IsBrowserAddressControl("edit", "Untrusted address label", false));
    }

    public static async Task Run(List<string> checks, Action<string> stage)
    {
        Application.Current.Dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        stage("demo-backend");
        Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSGUIDE_API_URL"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSGUIDE_LOCAL_TOKEN")));
        using var api = new ApiClient();
        Require(await api.Health(ct) is { Status: "ok", Mode: "demo", Version: "0.2.0" });
        checks.Add("demo-backend");

        stage("demo-dispatcher-ready");
        await Idle(ct);
        // ponytail: reuse production capture/API/demo; foreground/overlay coverage stays in IntegrationTests.
        var demo = new DemoWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRendered(object? sender, EventArgs args) => rendered.TrySetResult();
            demo.ContentRendered += OnRendered;
            try
            {
                stage("demo-show");
                demo.Show();
                stage("demo-content-rendered");
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            finally { demo.ContentRendered -= OnRendered; }
            stage("own-demo-visible");
            var handle = new WindowInteropHelper(demo).Handle;
            Native.GetWindowThreadProcessId(handle, out var pid);
            Require(pid == Environment.ProcessId && demo.IsLoaded && demo.IsVisible
                && demo.ActualWidth > 0 && demo.ActualHeight > 0 && Native.IsWindowVisible(handle));
            var window = new WindowChoice(handle, pid, "MSGuide Demo");
            Require(window.Matches()); // Exact owned HWND, never a title search or foreground selection.
            checks.Add("own-demo-visible");

            string[] labels = ["View logs", "Open troubleshooting", "Mark resolved"];
            string[] headings = ["Build pipeline needs attention", "Build failed: exit code 1",
                "Check compiler errors and missing dependencies", "Issue resolved"];
            for (int state = 0; state < 4; state++)
            {
                stage($"capture-{state}");
                await Idle(ct);
                Require(window.Matches());
                using var evidence = await Capture(window, ct);
                Require(evidence.Valid() && evidence.Window == window && evidence.Preview is { IsFrozen: true }
                    && evidence.Preview.PixelWidth > 0 && evidence.Preview.PixelHeight > 0
                    && Math.Max(evidence.Preview.PixelWidth, evidence.Preview.PixelHeight) <= 1280
                    && evidence.Png.Length is > 8 and <= 2_000_000
                    && evidence.Png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                var lines = evidence.Text.Split('\n');
                Require(headings.Where((heading, index) => lines.Contains(heading) != (index == state)).Count() == 0);
                Require(labels.Where((label, index) => lines.Contains(label) != (index == state)).Count() == 0);
                Require(evidence.Elements.Length <= 200 && evidence.Elements.All(e => e.Label.Length <= 256
                    && e.TargetId is { Length: 28 } targetId && targetId.StartsWith("uia-") && e.Targetable == e.IsEnabled
                    && e.AutomationId.Length <= 128 && e.FrameworkId.Length <= 64 && Safety.ValidBox(e.Box)));
                foreach (var label in labels)
                    Require(evidence.Elements.Count(e => e.Role == "button" && e.Label == label)
                        == (state < 3 && label == labels[state] ? 1 : 0));
                checks.Add($"evidence-{state}");

                stage($"guidance-{state}");
                var observation = evidence.Observation(false);
                Require(observation.ImageBase64 is null);
                var response = await api.Guide(observation, "Help me find the build error", ct, planSegments: false);
                Require(evidence.Valid() && Safety.Matches(response, evidence.Id, window.Id)
                    && response.Mode == "demo" && response.Status == (state < 3 ? "next_step" : "completed"));
                if (state < 3)
                {
                    var target = response.Target;
                    Require(target is not null && target.Label == labels[state] && Safety.ObservedTarget(target, evidence.Elements));
                    checks.Add($"target-{state}");
                    stage($"invoke-own-demo-{state}");
                    demo.Dispatcher.VerifyAccess();
                    var button = Buttons(demo).Single(b => AutomationProperties.GetAutomationId(b) == "DemoStep" + state);
                    Require(Window.GetWindow(button) == demo && button.IsVisible && button.IsEnabled
                        && AutomationProperties.GetName(button) == labels[state]);
                    var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    Require(Safety.PhysicalTarget(evidence.Rect, target!.Box).Contains(center));
                    var invoke = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                    Require(invoke is not null);
                    // Test-only invocation of this instance's button, not a mouse click or production action path.
                    invoke!.Invoke();
                    await Idle(ct);
                    Require(!Buttons(demo).Any(b => AutomationProperties.GetAutomationId(b) == "DemoStep" + state));
                    checks.Add($"own-demo-peer-invoke-{state}");
                }
                else
                {
                    Require(response.Target is null);
                    checks.Add("completed");
                    stage("bounds-invalidation");
                    var before = evidence.Rect;
                    Require(Native.SetWindowPos(handle, 0, before.Left + 8, before.Top, 0, 0, 0x15));
                    await Idle(ct);
                    Require(Native.GetWindowRect(handle, out var moved) && !before.Same(moved) && !evidence.Valid());
                    checks.Add("bounds-invalidated");
                }

                stage($"dispose-evidence-{state}");
                var bytes = evidence.Png;
                evidence.Dispose();
                Require(!evidence.Valid() && evidence.Preview is null && evidence.Png.Length == 0
                    && evidence.Elements.Length == 0 && evidence.Text.Length == 0 && bytes.All(b => b == 0));
                bool rejected = false;
                try { evidence.Observation(false); } catch (ObjectDisposedException) { rejected = true; }
                Require(rejected);
                checks.Add($"evidence-disposed-{state}");
            }
            stage("persistent-companion-feedback");
            await CheckFeedbackVisibility(ct);
            checks.Add("dpi-stable-feedback-persists-after-six-seconds-and-prompt-dismissal");
            stage("dense-visible-controls");
            await CheckDenseControls(ct);
            checks.Add("dense-context-does-not-hide-action-controls-or-enable-background-input");
            stage("complete");
        }
        finally { demo.Hide(); demo.Close(); }
    }

    internal static async Task CheckFeedbackVisibility(CancellationToken ct)
    {
        var shell = new CompanionShell(_ => Task.CompletedTask, () => { },
            _ => Task.CompletedTask, () => { }, () => { }, _ => { });
        try
        {
            nint foreground = Native.GetForegroundWindow();
            var task = new ScreenTaskSession("Synthetic feedback request", "synthetic-window");
            await task.RunAsync((_, _) => Task.FromResult(new Observation("synthetic-observation",
                    "synthetic-window", "Synthetic feedback", DateTimeOffset.UtcNow, 800, 600, "", [], null)),
                (observation, progress, _) => Task.FromResult(new Guidance("synthetic-correlation",
                    observation.Id, observation.WindowId, "Select a synthetic account and provide a synthetic queue name.",
                    "needs_input", null, [], "model", TaskId: progress.TaskId, Step: progress.Step)),
                (_, _, _) => throw new InvalidOperationException("Feedback fixture must not execute."),
                false, () => { }, ct);
            shell.Start();
            shell.BeginTask();
            shell.ShowTaskStatus("Working on a synthetic task.");
            shell.FinishTask(task);
            await Task.Delay(650, ct);
            void CheckSizeAndText(string expected)
            {
                var cursor = shell.Cursor;
                var size = cursor.ExpectedPhysicalSize;
                Require(cursor.IsVisible && cursor.HasVisibleFeedback
                    && cursor.FeedbackText.Contains(expected, StringComparison.Ordinal)
                    && Native.GetWindowRect(new WindowInteropHelper(cursor).Handle, out var bounds)
                    && Math.Abs(bounds.Width - size.Width) <= 1 && Math.Abs(bounds.Height - size.Height) <= 1
                    && bounds.Height >= 100);
            }
            CheckSizeAndText("Needs your input");
            shell.Cursor.Hide();
            shell.Prompt.ShowActivated = false;
            shell.Prompt.Show();
            await Idle(ct);
            Require(!shell.Cursor.IsVisible);
            shell.Prompt.Hide();
            await Idle(ct);
            CheckSizeAndText("Actions invoked: 0");
            var failed = new ScreenTaskSession("Synthetic failed feedback", "synthetic-window");
            await failed.RunAsync((_, _) => Task.FromResult(new Observation("synthetic-failure",
                    "synthetic-window", "Synthetic failure", DateTimeOffset.UtcNow, 800, 600, "", [], null)),
                (_, _, _) => throw new InvalidOperationException("Synthetic provider failure detail must stay out of logs."),
                (_, _, _) => throw new InvalidOperationException("A failed request cannot execute."),
                false, () => { }, ct);
            shell.FinishTask(failed);
            await Task.Delay(350, ct);
            CheckSizeAndText("Task failed");
            shell.ShowResponse("Synthetic failure response that must remain available until cleared.");
            await Task.Delay(TimeSpan.FromSeconds(6.5), ct);
            CheckSizeAndText("Synthetic failure response");
            var completed = new ScreenTaskSession("Synthetic completed feedback", "synthetic-window");
            await completed.RunAsync((_, _) => Task.FromResult(new Observation("synthetic-completion",
                    "synthetic-window", "Synthetic completion", DateTimeOffset.UtcNow, 800, 600, "", [], null)),
                (observation, progress, _) => Task.FromResult(new Guidance("synthetic-correlation",
                    observation.Id, observation.WindowId, "Synthetic goal is visibly complete.",
                    "completion_candidate", null, [], "model", TaskId: progress.TaskId, Step: progress.Step)),
                (_, _, _) => throw new InvalidOperationException("Feedback fixture must not execute."),
                false, () => { }, ct);
            shell.FinishTask(completed);
            await Task.Delay(TimeSpan.FromSeconds(4.2), ct);
            CheckSizeAndText("Completion needs review");
            shell.Cursor.Hide();
            shell.Prompt.Show();
            await Task.Delay(TimeSpan.FromSeconds(1.2), ct);
            Require(!shell.Cursor.IsVisible && !shell.Cursor.HasVisibleFeedback
                && shell.Cursor.FeedbackText.Length == 0 && completed.Status == "review_required"
                && completed.Detail.Contains("Synthetic goal", StringComparison.Ordinal));
            shell.Prompt.Hide();
            await Idle(ct);
            Require(shell.Cursor.IsVisible && !shell.Cursor.HasVisibleFeedback
                && shell.Cursor.ExpectedPhysicalSize == CompanionPlacement.PhysicalSize(new Size(48, 48),
                    System.Windows.Media.VisualTreeHelper.GetDpi(shell.Cursor)));
            shell.FinishTask(completed);
            shell.ShowTaskStatus("Synthetic new task is still running.");
            await Task.Delay(TimeSpan.FromSeconds(5.2), ct);
            CheckSizeAndText("Synthetic new task is still running.");
            shell.FinishTask(completed);
            shell.ShowResponse("Synthetic newer error must not expire with the previous completion.");
            await Task.Delay(TimeSpan.FromSeconds(5.2), ct);
            CheckSizeAndText("Synthetic newer error");
            Require(Native.GetForegroundWindow() == foreground);
            shell.ClearFeedback();
            Require(shell.Cursor.IsVisible && !shell.Cursor.HasVisibleFeedback && shell.Cursor.FeedbackText.Length == 0);
        }
        finally { shell.Stop(); }
    }

    private sealed class LookupContext : TextBlock
    {
        internal int HelpTextReads;
        protected override AutomationPeer OnCreateAutomationPeer() => new LookupPeer(this);
        private sealed class LookupPeer(LookupContext owner) : TextBlockAutomationPeer(owner)
        {
            protected override string GetHelpTextCore()
            {
                owner.HelpTextReads++;
                return "Synthetic lookup context.";
            }
        }
    }

    private static async Task CheckDenseControls(CancellationToken ct)
    {
        var panel = new Grid();
        var lookupContext = new LookupContext { Text = "Synthetic lookup context" };
        panel.Children.Add(lookupContext);
        for (int index = 0; index < 250; index++)
            panel.Children.Add(new TextBlock
            {
                Text = $"Synthetic context {index}",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            });
        var button = new Button
        {
            Content = "Synthetic foreground action", Width = 220, Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, "Synthetic foreground action");
        AutomationProperties.SetAutomationId(button, "dense-action");
        int invocations = 0;
        button.Click += (_, _) => invocations++;
        panel.Children.Add(button);
        UIElement content = panel;
        for (int depth = 0; depth < 20; depth++)
        {
            var group = new GroupBox { Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(group, $"Synthetic group {depth}");
            content = group;
        }
        var fixture = new Window
        {
            Title = "MSGuide owned inspection fixture", Width = 640, Height = 420,
            ShowActivated = false, Content = content
        };
        try
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.ContentRendered += (_, _) => rendered.TrySetResult();
            fixture.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            await Idle(ct);
            var handle = new WindowInteropHelper(fixture).Handle;
            var window = new WindowChoice(handle, (uint)Environment.ProcessId, fixture.Title);
            using var snapshot = await CaptureService.Capture(window, ct, includeImage: false);
            Require(snapshot.Valid() && snapshot.AutomationComplete && snapshot.Elements.Length == 200
                && snapshot.Note.Contains("Non-action context", StringComparison.Ordinal));
            var element = snapshot.Elements.Single(item => item.AutomationId == "dense-action");
            Require(element.Targetable && element.ControlId is not null && element.Action == "invoke");
            var target = new TargetInfo(element.Label, element.Box, element.Confidence,
                element.TargetId, element.AutomationId, element.FrameworkId, Action: element.Action,
                ControlId: element.ControlId);
            Require(lookupContext.HelpTextReads > 0);
            lookupContext.HelpTextReads = 0;
            var rebound = await Task.Run(() => AutomationEvidence.FindUniqueTarget(window, snapshot.Rect,
                element.TargetId!, element.Label, element.AutomationId, ct, snapshot.ResourceId), ct);
            Require(rebound is not null);
            var byName = await Task.Run(() => AutomationEvidence.FindUniqueTarget(window, snapshot.Rect,
                element.TargetId!, element.Label, null, ct, snapshot.ResourceId), ct);
            Require(byName is not null);
            var wrongTarget = await Task.Run(() => AutomationEvidence.FindUniqueTarget(window, snapshot.Rect,
                "uia-not-the-reviewed-target", element.Label, element.AutomationId, ct, snapshot.ResourceId), ct);
            Require(wrongTarget is null && invocations == 0 && lookupContext.HelpTextReads == 0);
            var marker = new CursorCompanionWindow();
            var outline = new OverlayWindow();
            try
            {
                nint foreground = Native.GetForegroundWindow();
                outline.PointAt(snapshot.Rect, element.Box, showBadge: false);
                Require(marker.ShowActionTarget(snapshot.Rect, element.Box));
                await Idle(ct);
                var targetRect = Safety.PhysicalTarget(snapshot.Rect, element.Box);
                Require(outline.IsVisible && marker.IsVisible && !marker.IsHitTestVisible
                    && Native.GetForegroundWindow() == foreground
                    && Native.GetWindowRect(new WindowInteropHelper(marker).Handle, out var markerRect)
                    && markerRect.Left <= targetRect.X + targetRect.Width / 2
                    && markerRect.Right >= targetRect.X + targetRect.Width / 2
                    && markerRect.Top <= targetRect.Y + targetRect.Height / 2
                    && markerRect.Bottom >= targetRect.Y + targetRect.Height / 2);
            }
            finally { marker.Stop(); outline.Close(); }
            Require(Native.GetForegroundWindow() != handle);
            var result = await DesktopAction.ExecuteAsync(window, snapshot.Rect, snapshot.CapturedAt,
                target, ct, snapshot.ResourceId);
            Require(!result.Invoked && result.OutcomeKnown && invocations == 0);
        }
        finally { fixture.Close(); }
    }
}