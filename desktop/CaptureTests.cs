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
                    && evidence.Png.Length is > 8 and <= 2_000_000
                    && evidence.Png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                var lines = evidence.Text.Split('\n');
                Require(headings.Where((heading, index) => lines.Contains(heading) != (index == state)).Count() == 0);
                Require(labels.Where((label, index) => lines.Contains(label) != (index == state)).Count() == 0);
                Require(evidence.Elements.Length <= 200 && evidence.Elements.All(e => e.Label.Length <= 256
                    && e.TargetId.StartsWith("uia-") && e.TargetId.Length == 28 && e.Targetable == e.IsEnabled
                    && e.AutomationId.Length <= 128 && e.FrameworkId.Length <= 64 && Safety.ValidBox(e.Box)));
                foreach (var label in labels)
                    Require(evidence.Elements.Count(e => e.Role == "button" && e.Label == label)
                        == (state < 3 && label == labels[state] ? 1 : 0));
                checks.Add($"evidence-{state}");

                stage($"guidance-{state}");
                var observation = evidence.Observation(false);
                Require(observation.ImageBase64 is null);
                var response = await api.Guide(observation, "Help me find the build error", ct);
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
            stage("complete");
        }
        finally { demo.Hide(); demo.Close(); }
    }
}