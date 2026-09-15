using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class NativeTests
{
    // No unrelated window titles, pixels or process details enter the report.
    internal static async Task RequireForeground(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        window.Activate();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (Native.GetForegroundWindow() == handle) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Activated(object? sender, EventArgs args)
        { if (Native.GetForegroundWindow() == handle) ready.TrySetResult(); }
        window.Activated += Activated;
        try
        {
            if (Native.GetForegroundWindow() == handle) return;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (Native.GetForegroundWindow() != handle) throw new InvalidOperationException("Foreground changed.");
        }
        finally { window.Activated -= Activated; }
    }

    internal static async Task Run(List<string> checks, Action<string> stage)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (bool software in new[] { false, true })
        {
            string mode = software ? "software" : "default";
            var demo = new DemoWindow { ShowActivated = false };
            try
            {
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                demo.ContentRendered += (_, _) => rendered.TrySetResult();
                demo.SourceInitialized += (_, _) =>
                {
                    if (software) HwndSource.FromHwnd(new WindowInteropHelper(demo).Handle)!.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                };
                stage(mode + "-render");
                demo.Show();
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var handle = new WindowInteropHelper(demo).Handle;
                checks.Add(mode + "-rendered");
                stage(mode + "-capture");
                try
                {
                    using var image = await CaptureService.Capture(new(handle, (uint)Environment.ProcessId, demo.Title), CancellationToken.None);
                    if (!image.Valid() || !image.Elements.Any(e => e.Label == "View logs"))
                        throw new InvalidOperationException("Evidence mismatch.");
                    checks.Add(mode + "-capture-and-target-pass");
                }
                catch (InvalidOperationException ex)
                { checks.Add(mode + "-capture-" + new CaptureTests.CaptureFailure(ex).Message); }
                await CaptureService.WhenIdle.WaitAsync(TimeSpan.FromSeconds(35));
            }
            finally { demo.Close(); }
        }
        stage("diagnostic-complete-not-interactive-acceptance");
    }
}