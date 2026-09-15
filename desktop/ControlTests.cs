using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class ControlTests
{
    internal sealed class AssertionFailure(int line) : InvalidOperationException($"Control assertion at source line {line}.");
    private static void Check(bool value, [CallerLineNumber] int line = 0)
    { if (!value) throw new AssertionFailure(line); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected);
    }

    internal static async Task Run(List<string> checks, Action<string> stage, bool component = false)
    {
        // Own windows only; no API, model, screenshots, global input or user applications.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var host = new Window { Title = "MSGuide control test — click here to start", Width = 400, Height = 240,
            Content = new TextBlock { Text = "Click this window within 30 seconds to start the owned-demo tests. Keep focus here until it closes.", Margin = new Thickness(24) } };
        var demo = new DemoWindow();
        var other = new DemoWindow(); // A matching title must not confer authority.
        try
        {
            stage("owned-windows");
            host.Show(); demo.Show(); other.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            host.Activate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var hostHandle = new WindowInteropHelper(host).Handle;
            stage("foreground-required");
            if (!component)
            {
                await NativeTests.RequireForeground(host);
                Check(Native.GetForegroundWindow() == hostHandle);
            }
            checks.Add(component ? "SIMULATED-focus-component-test-not-interactive-acceptance" : "owned-host-foreground");

            nint simulatedFocus = hostHandle;
            var now = DateTimeOffset.UtcNow;
            DemoTaskSession Session(InteractionMode mode = InteractionMode.Control) => component
                ? new(demo, hostHandle, mode, () => simulatedFocus, () => now)
                : new(demo, hostHandle, mode);
            void Reset()
            {
                // Fixture reset is intentionally separate from execution authority.
                var page = (StackPanel)((ScrollViewer)demo.Content).Content;
                var reset = page.Children.OfType<Button>().Single();
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                demo.UpdateLayout();
            }

            stage("consent-and-mode");
            var task = Session();
            Reject(() => task.ExecuteNext());
            Check(demo.WorkflowState == 0);
            task.Stop();
            Reject(task.Approve);
            var guide = Session(InteractionMode.Guide);
            guide.Approve();
            Reject(() => guide.ExecuteNext());
            Check(demo.WorkflowState == 0 && guide.Observe() is not null);
            guide.Stop();
            checks.Add("unapproved-stopped-and-guide-execution-rejected");

            stage("control-three-runs");
            for (int i = 0; i < 3; i++)
            {
                Reset();
                task = Session();
                task.Approve();
                Reject(task.Approve);
                Check(task.CurrentStep?.Label == "View logs");
                task.ExecuteNext(); demo.UpdateLayout();
                Check(demo.WorkflowState == 1 && task.CurrentStep?.Label == "Open troubleshooting");
                task.ExecuteNext(); demo.UpdateLayout();
                Check(task.Finished && task.Observe() is null && demo.WorkflowState == 2);
                Reject(() => task.ExecuteNext());
                Check(demo.WorkflowState == 2 && other.WorkflowState == 0);
                task.Stop();
                checks.Add($"verified-two-actions-run-{i + 1}");
            }

            stage("stop-between-actions");
            Reset(); task = Session(); task.Approve(); task.ExecuteNext(); demo.UpdateLayout(); task.Stop();
            Reject(() => task.ExecuteNext());
            Check(demo.WorkflowState == 1);
            checks.Add("stop-prevents-next-action");

            stage("guide-shared-plan");
            Reset(); guide = Session(InteractionMode.Guide); guide.Approve();
            Check(guide.CurrentStep == DemoTaskSession.Next(0));
            demo.InvokeStep(demo.StepButton!, demo.Revision); demo.UpdateLayout(); // Simulate the user's click.
            guide.Observe();
            Check(guide.CurrentStep == DemoTaskSession.Next(1));
            demo.InvokeStep(demo.StepButton!, demo.Revision); demo.UpdateLayout();
            Check(guide.Observe() is null && guide.Finished);
            guide.Stop();
            checks.Add("guide-and-control-share-plan");

            stage("unexpected-change");
            Reset(); task = Session(); task.Approve(); Reset();
            Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0); task.Stop();
            task = Session(); Reset(); Reject(task.Approve); task.Stop();
            guide = Session(InteractionMode.Guide); guide.Approve(); Reset();
            Reject(() => guide.Observe()); guide.Stop();
            checks.Add("reset-invalidates-plan-and-run");

            stage("target-tamper");
            Reset(); task = Session(); task.Approve();
            AutomationProperties.SetName(demo.StepButton!, "Unexpected");
            Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0); task.Stop();
            Reset(); task = Session(); task.Approve(); demo.StepButton!.IsEnabled = false;
            Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0); task.Stop();
            checks.Add("renamed-and-disabled-target-rejected");

            if (component)
            {
                stage("focus-and-expiry-policy");
                Reset(); task = Session(); task.Approve();
                simulatedFocus = new WindowInteropHelper(other).Handle;
                Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0); task.Stop();
                simulatedFocus = hostHandle;
                task = Session(); task.Approve(); now = now.AddSeconds(60);
                Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0); task.Stop();
                checks.Add("simulated-focus-loss-and-expiry-rejected");

                stage("companion-interruption-controls");
                var companion = new MainWindow(); // No Loaded: no service, capture, chooser or hotkeys.
                try
                {
                    foreach (string reason in new[] { "stop", "takeover", "consent", "mode", "pause", "prompt" })
                    {
                        Reset(); task = Session(); task.Approve();
                        Check(companion.CheckTaskInterruption(task, reason));
                        Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0);
                    }
                }
                catch (Exception ex)
                {
                    var site = new System.Diagnostics.StackTrace(ex).GetFrames()?
                        .Select(f => f.GetMethod())
                        .Where(m => m?.DeclaringType?.Namespace == "MSGuide.Desktop")
                        .Select(m => m!.DeclaringType!.Name + "." + m.Name);
                    stage("companion-interruption-controls:" + string.Join("/", site ?? []));
                    throw;
                }
                finally { companion.Close(); }
                checks.Add("companion-stop-takeover-consent-mode-pause-prompt-revoke");
            }

            stage("window-movement");
            Reset(); task = Session(); task.Approve(); demo.Left += 16;
            Reject(() => task.ExecuteNext()); Check(demo.WorkflowState == 0); task.Stop();
            checks.Add("moved-window-rejected");

            stage("closed-window");
            task = Session(); task.Approve(); demo.Close();
            Reject(() => task.ExecuteNext()); task.Stop();
            checks.Add("closed-window-rejected");
            stage("complete");
        }
        finally { other.Close(); demo.Close(); host.Close(); }
    }
}

public partial class MainWindow
{
    internal bool CheckTaskInterruption(DemoTaskSession session, string reason)
    {
        loaded = false;
        ControlMode.IsChecked = true;
        TaskConsent.IsChecked = true;
        demoTask = session;
        taskRunning = true;
        taskTimer.Tick += ExecuteTask_Tick;
        taskTimer.Start();
        loaded = true;
        try
        {
            switch (reason)
            {
                case "stop": StopTask_Click(this, new RoutedEventArgs()); break;
                case "takeover": TakeOver_Click(this, new RoutedEventArgs()); break;
                case "consent": TaskConsent.IsChecked = false; break;
                case "mode": GuideMode.IsChecked = true; break;
                case "pause": Pause(); break;
                case "prompt": PromptBox.Text = "A different task"; break;
            }
            ExecuteTask_Tick(this, EventArgs.Empty); // Simulate a late tick after revocation.
            return demoTask is null && !taskRunning && !taskTimer.IsEnabled
                && TaskConsent.IsChecked == false && !StartTaskButton.IsEnabled
                && !ObserveTaskButton.IsEnabled && highlight is null;
        }
        finally { loaded = false; }
    }
}