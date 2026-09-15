using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.CompilerServices;

namespace MSGuide.Desktop;

internal static class IntegrationTests
{
    internal sealed class AssertionFailure(int line) : InvalidOperationException($"Integration assertion at source line {line}.");

    private static readonly string[] Labels = ["View logs", "Open troubleshooting", "Mark resolved"];
    private static readonly string[] Headings = ["Build pipeline needs attention", "Build failed: exit code 1",
        "Check compiler errors and missing dependencies", "Issue resolved"];

    internal static void Require(bool condition, [CallerLineNumber] int line = 0)
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

    public static async Task Run(List<string> checks, Action<string> stage)
    {
        Application.Current.Dispatcher.VerifyAccess();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        DemoWindow? demo = null;
        OverlayWindow? overlay = null;
        Snapshot? evidence = null;
        try
        {
            stage("interactive-desktop");
            Require(Environment.UserInteractive);
            stage("demo-backend");
            Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSGUIDE_API_URL"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSGUIDE_LOCAL_TOKEN")));
            using var api = new ApiClient(); // Loopback only; never launch/configure a backend here.
            var health = await api.Health(ct);
            Require(health is { Status: "ok", Mode: "demo", Version: "0.2.0" });
            checks.Add("demo-backend");

            // Do not rely on the health request yielding: startup must return to WPF's message loop.
            stage("demo-dispatcher-ready");
            await Idle(ct);
            stage("demo-create");
            demo = new DemoWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            stage("overlay-create");
            overlay = new OverlayWindow();
            int overlayActivations = 0;
            overlay.Activated += (_, _) => overlayActivations++;
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
            stage("demo-layout-visible");
            Require(demo.IsLoaded && demo.IsVisible && demo.ActualWidth > 0 && demo.ActualHeight > 0
                && Native.IsWindowVisible(new WindowInteropHelper(demo).Handle));
            stage("demo-activate");
            await NativeTests.RequireForeground(demo);
            await Idle(ct);
            stage("demo-foreground");
            var handle = new WindowInteropHelper(demo).Handle;
            Native.GetWindowThreadProcessId(handle, out var pid);
            Require(pid == Environment.ProcessId && Native.GetForegroundWindow() == handle);
            // Never enumerate or choose windows by title: this exact instance is the only capture target.
            var window = new WindowChoice(handle, pid, "MSGuide Demo");
            stage("demo-window-identity");
            Require(window.Matches());
            checks.Add("own-demo-visible");

            stage("capture-cancellation");
            using (var cancelled = new CancellationTokenSource())
            {
                var pending = CaptureService.Capture(window, cancelled.Token);
                cancelled.Cancel(); // Cancel with the dispatcher still occupied, before native WPF work can finish.
                bool rejected = false;
                try { using var unexpected = await pending; }
                catch (OperationCanceledException) { rejected = true; }
                Require(rejected);
                await CaptureService.WhenIdle.WaitAsync(TimeSpan.FromSeconds(35), ct);
            }
            checks.Add("cancelled-capture-drained");

            for (int state = 0; state < 4; state++)
            {
                stage($"capture-{state}");
                overlay.Hide();
                await Idle(ct);
                Require(window.Matches() && Native.GetForegroundWindow() == handle);
                evidence = await CaptureService.Capture(window, ct);
                Require(evidence.Valid() && evidence.Preview is not null && evidence.Png.Length > 0);
                var lines = evidence.Text.Split('\n');
                Require(Headings.Where((heading, index) => lines.Contains(heading) != (index == state)).Count() == 0);
                Require(Labels.Where((label, index) => lines.Contains(label) != (index == state)).Count() == 0);
                Require(evidence.Elements.Length <= 200 && evidence.Elements.All(e => e.Label.Length <= 256 && Safety.ValidBox(e.Box)));
                foreach (var label in Labels)
                    Require(evidence.Elements.Count(e => e.Role == "button" && e.Label == label) == (state < 3 && label == Labels[state] ? 1 : 0));
                checks.Add($"evidence-{state}");

                stage($"guidance-{state}");
                // Consent is explicit in ApiClient's GuidanceRequest. Backend demo accepts UIA only.
                var observation = evidence.Observation(false);
                Require(observation.ImageBase64 is null);
                var response = await api.Guide(observation, "Help me find the build error", ct);
                Require(evidence.Valid() && Safety.Matches(response, evidence.Id, window.Id) && response.Mode == "demo");
                Require(response.Status == (state < 3 ? "next_step" : "completed"));
                if (state == 3)
                {
                    Require(response.Target is null);
                    checks.Add("completed");
                    break;
                }

                var target = response.Target;
                Require(target is not null && target.Label == Labels[state] && Safety.ObservedTarget(target, evidence.Elements));
                checks.Add($"target-{state}");
                stage($"overlay-{state}");
                Require(Native.GetForegroundWindow() == handle);
                overlay.PointAt(evidence.Rect, target!.Box);
                stage($"overlay-{state}-synchronous-foreground");
                Require(Native.GetForegroundWindow() == handle && overlayActivations == 0);
                await Idle(ct);
                stage($"overlay-{state}-wpf-visible");
                Require(overlay.IsVisible);
                stage($"overlay-{state}-native-visible");
                Require(Native.IsWindowVisible(overlay.Handle));
                stage($"overlay-{state}-foreground-preserved");
                if (Native.GetForegroundWindow() != handle)
                {
                    var focus = Native.GetForegroundWindow();
                    checks.Add(focus == overlay.Handle ? "unexpected-overlay-foreground" : focus == 0 ? "unexpected-no-foreground" : "unexpected-other-foreground");
                    checks.Add($"overlay-noactivate-style-{(Native.GetWindowLong(overlay.Handle, -20) & 0x08000000) != 0}");
                }
                Require(Native.GetForegroundWindow() == handle && overlayActivations == 0);
                const int styles = 0x20 | 0x80 | 0x08000000;
                Require((Native.GetWindowLong(overlay.Handle, -20) & styles) == styles && !overlay.IsHitTestVisible && !overlay.ShowActivated);
                var physical = Safety.PhysicalTarget(evidence.Rect, target.Box);
                Require(Native.GetWindowRect(overlay.Handle, out var outline)
                    && outline.Left == (int)Math.Floor(physical.Left) && outline.Top == (int)Math.Floor(physical.Top)
                    && outline.Width == (int)Math.Ceiling(physical.Width) && outline.Height == (int)Math.Ceiling(physical.Height));
                Require(Native.SendMessage(overlay.Handle, 0x84, 0, 0) == new nint(-1));
                Require(Native.SendMessage(overlay.Handle, 0x21, 0, 0) == new nint(3));
                // Reposition while visible, then hide/re-show without borrowing focus.
                overlay.PointAt(evidence.Rect, target.Box);
                overlay.Hide();
                overlay.PointAt(evidence.Rect, target.Box);
                await Idle(ct);
                Require(overlayActivations == 0 && Native.GetForegroundWindow() == handle
                    && overlay.IsVisible && Native.IsWindowVisible(overlay.Handle)
                    && Native.GetWindowRect(overlay.Handle, out var reshown) && outline.Same(reshown));
                checks.Add($"overlay-focus-preserved-{state}");

                stage($"invoke-own-demo-{state}");
                var button = Buttons(demo).Single(b => AutomationProperties.GetAutomationId(b) == "DemoStep" + state);
                Require(Window.GetWindow(button) == demo && button.IsVisible && button.IsEnabled
                    && AutomationProperties.GetName(button) == Labels[state]);
                var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                Require(physical.Contains(center) && Native.WindowFromPoint(new Native.POINT { X = (int)center.X, Y = (int)center.Y }) == handle);
                var invoke = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                Require(invoke is not null);
                // Harness-only invocation of a button found under our own DemoWindow. No production action path.
                invoke!.Invoke();
                await Idle(ct);
                Require(!Buttons(demo).Any(b => AutomationProperties.GetAutomationId(b) == "DemoStep" + state));
                checks.Add($"overlay-click-through-and-invoke-{state}");
                evidence.Dispose(); evidence = null;
            }

            stage("bounds-invalidation");
            Require(evidence is not null && evidence.Valid());
            var before = evidence!.Rect;
            Require(Native.SetWindowPos(handle, 0, before.Left + 8, before.Top, 0, 0, 0x15));
            await Idle(ct);
            Require(Native.GetWindowRect(handle, out var moved) && !before.Same(moved) && !evidence.Valid());
            checks.Add("bounds-invalidated");

            stage("pause-local-evidence");
            var companion = new MainWindow(); // Never shown/loaded: no chooser, health call, capture or hotkey.
            try { companion.CheckPauseForIntegration(evidence); }
            finally { companion.Close(); }
            evidence = null;
            checks.Add("pause-cleared-and-stale-work-rejected");
            stage("complete");
        }
        finally
        {
            evidence?.Dispose();
            try { overlay?.Hide(); overlay?.Close(); }
            finally { demo?.Hide(); demo?.Close(); }
        }
    }
}

public partial class MainWindow
{
    internal void CheckPauseForIntegration(Snapshot evidence)
    {
        Dispatcher.VerifyAccess();
        var ct = BeginWork();
        long mine = generation;
        snapshot = evidence;
        byte[] bytes = evidence.Png;
        PreviewImage.Source = FullPreviewImage.Source = evidence.Preview;
        MetadataText.Text = evidence.Text;
        ConsentBox.IsChecked = true;
        AnswerText.Text = "Synthetic old answer";
        SpeakButton.IsEnabled = true;
        sending = true;
        Pause();
        IntegrationTests.Require(ct.IsCancellationRequested && !CurrentWork(mine, ct)
            && snapshot is null && !sending && highlight is null && !overlay.IsVisible
            && PreviewImage.Source is null && FullPreviewImage.Source is null && MetadataText.Text.Length == 0
            && PromptBox.Text.Length == 0 && ConsentBox.IsChecked == false && ShareImage.IsChecked == false
            && !SendButton.IsEnabled && !SpeakButton.IsEnabled && CitationsPanel.Children.Count == 0
            && !speech.Listening && !evidence.Valid() && evidence.Preview is null
            && evidence.Elements.Length == 0 && evidence.Text.Length == 0 && evidence.Png.Length == 0
            && bytes.All(value => value == 0) && !AnswerText.Text.Contains("Synthetic old answer"));
        // Ordinary supersession must clear old guidance too, not just the explicit Pause path.
        var next = BeginWork();
        long nextGeneration = generation;
        IntegrationTests.Require(CurrentWork(nextGeneration, next) && !CurrentWork(mine, ct));
        AnswerText.Text = "Synthetic superseded answer";
        CitationsPanel.Children.Add(new TextBlock { Text = "Synthetic citation" });
        SpeakButton.IsEnabled = true;
        CancelWork();
        IntegrationTests.Require(!CurrentWork(nextGeneration, next) && next.IsCancellationRequested
            && AnswerText.Text.Length == 0 && CitationsPanel.Children.Count == 0 && !SpeakButton.IsEnabled);
    }
}