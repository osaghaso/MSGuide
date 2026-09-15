using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MSGuide.Desktop;

internal enum InteractionMode { Guide, Control }

/// <summary>A bounded, local demo adapter. It cannot address any external application.</summary>
internal sealed class DemoTaskSession
{
    internal sealed record Step(int State, string Label, string Explanation);
    internal static Step? Next(int state) => state switch
    {
        0 => new(0, "View logs", "Open the sample build output."),
        1 => new(1, "Open troubleshooting", "Open the sample investigation instructions."),
        _ => null
    };

    private readonly DemoWindow window;
    private readonly nint companion;
    private readonly WindowChoice identity;
    private readonly Native.RECT bounds;
    private readonly long initialRevision;
    private readonly Func<nint> foregroundWindow;
    private readonly Func<DateTimeOffset> clock;
    private readonly DateTimeOffset expires;
    private long revision;
    private int state;
    private bool approved, stopped, executing;
    internal InteractionMode Mode { get; }
    internal bool Executing => executing;
    internal bool Finished => state == 2;
    internal Step? CurrentStep => Next(state);
    internal string Plan { get; }

    internal DemoTaskSession(DemoWindow window, nint companion, InteractionMode mode)
        : this(window, companion, mode, Native.GetForegroundWindow, () => DateTimeOffset.UtcNow) { }

    // Component-test seam only. The desktop UI always uses the constructor above with native focus/time.
    internal DemoTaskSession(DemoWindow window, nint companion, InteractionMode mode,
        Func<nint> foregroundWindow, Func<DateTimeOffset> clock)
    {
        window.Dispatcher.VerifyAccess();
        this.window = window;
        this.companion = companion;
        this.foregroundWindow = foregroundWindow;
        this.clock = clock;
        expires = clock().AddSeconds(60);
        Mode = mode;
        var handle = new WindowInteropHelper(window).Handle;
        identity = new(handle, (uint)Environment.ProcessId, "MSGuide Demo");
        if (!identity.Matches() || !Native.GetWindowRect(handle, out bounds)
            || !window.IsVisible || window.WorkflowState is not (0 or 1))
            throw new InvalidOperationException("Open or reset this companion's demo before preparing the task.");
        state = window.WorkflowState;
        initialRevision = revision = window.Revision;
        Plan = (state == 0 ? "1. View logs\n2. Open troubleshooting" : "1. Open troubleshooting")
            + "\nStop at the troubleshooting instructions. Do not select Mark resolved."
            + "\nScope: this exact local demo window only. No files, requests, builds or external applications."
            + "\nLocal control state is checked between steps; no screenshots or model uploads. Approval expires after 60 seconds.";
    }

    internal void Approve()
    {
        EnsureWindow();
        if (approved || initialRevision != window.Revision)
            throw new InvalidOperationException("The plan changed or was already approved. Prepare a new task.");
        approved = true;
    }

    internal void Stop() { stopped = true; approved = false; }

    private void EnsureWindow()
    {
        window.Dispatcher.VerifyAccess();
        if (stopped || clock() >= expires || !identity.Matches()
            || !Native.GetWindowRect(identity.Handle, out var current) || !bounds.Same(current))
            throw new InvalidOperationException("Task stopped: approval expired or the scoped window moved, closed or changed.");
        nint foreground = foregroundWindow();
        if (foreground != companion && foreground != identity.Handle)
            throw new InvalidOperationException("Task stopped: focus moved outside the companion and its demo.");
    }

    internal void Validate()
    {
        EnsureWindow();
        if (Mode == InteractionMode.Control && (window.Revision != revision || window.WorkflowState != state))
            throw new InvalidOperationException("Task stopped: the demo changed outside the approved execution.");
    }

    internal (Native.RECT Bounds, double[] Box)? Observe()
    {
        Validate();
        if (!approved) throw new InvalidOperationException("Approve this task first.");
        if (Mode == InteractionMode.Guide)
        {
            // Only accept the expected single user transition. Reset/jump requires a new plan.
            if (window.Revision != revision)
            {
                if (window.Revision != revision + 1 || window.WorkflowState != state + 1 || state >= 2)
                    throw new InvalidOperationException("Demo changed unexpectedly. Prepare a new task.");
                revision = window.Revision;
                state = window.WorkflowState;
            }
        }
        if (Finished) return null;
        var button = Target();
        var origin = button.PointToScreen(new Point(0, 0));
        var box = Safety.AutomationBox(new Rect(origin, new Size(button.ActualWidth, button.ActualHeight)), bounds);
        if (box is null) throw new InvalidOperationException("Demo target has no usable bounds. Task stopped.");
        return (bounds, box);
    }

    private Button Target()
    {
        var step = CurrentStep;
        var button = window.StepButton;
        if (step is null || button is null || Window.GetWindow(button) != window
            || !button.IsEnabled || !button.IsVisible
            || AutomationProperties.GetName(button) != step.Label
            || AutomationProperties.GetAutomationId(button) != "DemoStep" + state)
            throw new InvalidOperationException("Expected demo control is unavailable. Task stopped.");
        return button;
    }

    internal string ExecuteNext()
    {
        if (Mode != InteractionMode.Control) throw new InvalidOperationException("Guide mode cannot execute actions.");
        Observe(); // Fresh identity, bounds, expiry, revision and target validation immediately before invocation.
        if (Finished) throw new InvalidOperationException("Task has already finished.");
        var step = CurrentStep!;
        var button = Target();
        executing = true;
        try { window.InvokeStep(button, revision); }
        finally { executing = false; }
        // Invocation is synchronous on the dispatcher: Stop cannot be bypassed by queued UIA input.
        if (window.WorkflowState != state + 1 || window.Revision != revision + 1)
        { Stop(); throw new InvalidOperationException("Action outcome was not verified. No retry will occur."); }
        state = window.WorkflowState;
        revision = window.Revision;
        return $"Verified: {step.Label}.";
    }
}