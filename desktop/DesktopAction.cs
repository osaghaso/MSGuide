using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MSGuide.Desktop;

internal sealed record DesktopActionResult(bool Invoked, bool OutcomeKnown, string Detail);

internal static class DesktopAction
{
    private static int busy;
    internal static bool IsBusy => Volatile.Read(ref busy) != 0;
    internal static Task WhenIdle { get; private set; } = Task.CompletedTask;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    internal static Task<DesktopActionResult> ExecuteAsync(
        WindowChoice window, Native.RECT reviewedRect, DateTimeOffset capturedAt, TargetInfo target,
        CancellationToken cancellationToken, string? resourceId = null) =>
        RunBounded((token, beginInvocation) => Execute(
            window, reviewedRect, capturedAt, target, token, beginInvocation, resourceId), cancellationToken);

    internal static bool CanPresentInForeground(nint foreground, nint target, nint companion, nint prompt) =>
        target != 0 && (foreground == target || foreground != 0 && (foreground == companion || foreground == prompt));

    internal static async Task<DesktopActionResult> RunVisible(
        Func<CancellationToken, Task<bool>> present,
        Func<CancellationToken, Task<DesktopActionResult>> invoke,
        Action clear, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!await present(token))
                return new(false, true, "The target could not be shown in the foreground. Select the approved app and continue; no background action was performed.");
            token.ThrowIfCancellationRequested();
            return await invoke(token);
        }
        finally { clear(); }
    }

    // ponytail: contain one native worker, not a queue. COM cannot be interrupted safely;
    // process isolation is needed to recover a permanently hung provider without restart.
    internal static async Task<DesktopActionResult> RunBounded(
        Func<CancellationToken, Func<bool>, DesktopActionResult> execute,
        CancellationToken cancellationToken, TimeSpan? timeoutForTest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(false, true, "A previous native action is still returning. No new action was started; wait or restart MSGuide.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutForTest ?? Timeout);
        var token = deadline.Token;
        int invocationState = 0;
        using var cancelled = token.Register(() => Interlocked.CompareExchange(ref invocationState, 2, 0));
        bool BeginInvocation() => !token.IsCancellationRequested
            && Interlocked.CompareExchange(ref invocationState, 1, 0) == 0;
        var task = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return execute(token, BeginInvocation);
            }
            catch (OperationCanceledException)
            {
                return new(Volatile.Read(ref invocationState) == 1,
                    Volatile.Read(ref invocationState) != 1, "Native work cancelled. Any started action must be checked manually; no retry.");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                or COMException or UnauthorizedAccessException or ArgumentException)
            {
                bool invoked = Volatile.Read(ref invocationState) == 1;
                DiagnosticLog.Record("native_action_failed", new { invoked, errorType = ex.GetType().Name });
                return new(invoked, !invoked, invoked
                    ? "The action returned an unknown outcome. No retry is allowed; check the screen."
                    : "The exact control could not be inspected safely. No action was started.");
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        WhenIdle = task.ContinueWith(done =>
        {
            if (done.IsFaulted) _ = done.Exception;
            if (token.IsCancellationRequested)
                DiagnosticLog.Record("native_action_late_return", new { invoked = Volatile.Read(ref invocationState) == 1 });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref invocationState, 2, 0);
            bool invoked = Volatile.Read(ref invocationState) == 1;
            return new(invoked, !invoked, invoked
                ? "Native action outcome is unknown after cancellation or the 8-second deadline. It may still finish. No retry; wait for native work to return before any new action."
                : "Control inspection stopped before invocation. A native call may still return; new actions are blocked until it finishes.");
        }
    }

    private static DesktopActionResult Execute(
        WindowChoice window, Native.RECT reviewedRect, DateTimeOffset capturedAt, TargetInfo target,
        CancellationToken cancellationToken, Func<bool> beginInvocation, string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(target.TargetId)
            || string.IsNullOrWhiteSpace(target.Action)
            || !Safety.ValidActionInput(target)
            || !Safety.Fresh(capturedAt, DateTimeOffset.UtcNow)
            || !window.Matches()
            || !Native.GetWindowRect(window.Handle, out var currentRect)
            || !reviewedRect.Same(currentRect))
            return new DesktopActionResult(false, true, "The reviewed target is stale. Check the screen again.");

        AutomationElement? element;
        try
        {
            element = AutomationEvidence.FindUniqueTarget(
                window, currentRect, target.TargetId, target.Label,
                target.AutomationId, cancellationToken, resourceId);
        }
        catch (Exception ex) when (
            ex is ElementNotAvailableException or InvalidOperationException or COMException
                or UnauthorizedAccessException or ArgumentException)
        {
            return new DesktopActionResult(false, true, "The exact reviewed control could not be reacquired. Check the screen again.");
        }
        if (element is null)
            return new DesktopActionResult(false, true, "The exact reviewed control could not be reacquired. Check the screen again.");

        bool invoked = false;
        try
        {
            var current = element.Current;
            if (!Safety.Fresh(capturedAt, DateTimeOffset.UtcNow)
                || !window.Matches()
                || !Native.GetWindowRect(window.Handle, out var finalRect)
                || !reviewedRect.Same(finalRect))
                return new DesktopActionResult(false, true, "The reviewed target expired or changed before invocation. Check the screen again.");
            if (!current.IsEnabled || current.IsOffscreen || current.IsPassword)
                return new DesktopActionResult(false, true, "The reviewed control is no longer available for the authorized action.");
            var metadata = AutomationEvidence.ReadAction(element);
            string? currentAction = metadata.Name;
            if (!string.Equals(currentAction, target.Action, StringComparison.Ordinal))
                return new DesktopActionResult(false, true, "The reviewed control no longer exposes the authorized action.");
            if (currentAction == "toggle"
                && !string.Equals(AutomationEvidence.ToggleState(element), target.ToggleState, StringComparison.Ordinal))
                return new DesktopActionResult(false, true, "The reviewed toggle state changed before approval. Check the screen again.");
            if (currentAction == "set_value" && (metadata.IsReadOnly is not false
                || metadata.ValueHash != target.ValueHash || metadata.ValueLength is not (>= 0 and <= 1000)))
                return new(false, true, "The writable field changed after review. No text was replaced.");
            if (currentAction == "scroll" && metadata.ScrollDirections?.Contains(target.ScrollDirection!) != true)
                return new(false, true, "The approved scroll direction is no longer available.");
            if (currentAction == "select" && target.IsSelected is not null && metadata.IsSelected != target.IsSelected)
                return new(false, true, "The reviewed selection state changed before invocation.");
            var pattern = element.GetCurrentPattern(currentAction switch
            {
                "toggle" => TogglePattern.Pattern,
                "invoke" => InvokePattern.Pattern,
                "select" => SelectionItemPattern.Pattern,
                "expand" or "collapse" => ExpandCollapsePattern.Pattern,
                "set_value" => ValuePattern.Pattern,
                "scroll" => ScrollPattern.Pattern,
                _ => throw new InvalidOperationException("Unsupported action")
            });
            nint foreground = Native.GetForegroundWindow();
            if (!Safety.Fresh(capturedAt, DateTimeOffset.UtcNow) || !window.Matches()
                || !Native.GetWindowRect(window.Handle, out var invocationRect) || !reviewedRect.Same(invocationRect)
                || foreground != window.Handle
                || resourceId is not null && !AutomationEvidence.ResourceMatches(window, resourceId, cancellationToken)
                || !AutomationEvidence.MatchesTargetId(window, invocationRect, element, target.TargetId))
                return new(false, true, "The exact window, focus, or target changed before invocation. No action was started.");
            if (Native.GetForegroundWindow() != window.Handle
                || !Safety.Fresh(capturedAt, DateTimeOffset.UtcNow))
                return new(false, true, "Focus or evidence freshness changed. No background action was started.");
            if (!(invoked = beginInvocation()))
                return new(false, true, "Cancelled before invocation. No action was started.");
            switch (pattern)
            {
                case TogglePattern toggle:
                    toggle.Toggle();
                    break;
                case InvokePattern invoke:
                    invoke.Invoke();
                    break;
                case SelectionItemPattern select:
                    select.Select();
                    break;
                case ExpandCollapsePattern expand when currentAction == "expand":
                    expand.Expand();
                    break;
                case ExpandCollapsePattern collapse when currentAction == "collapse":
                    collapse.Collapse();
                    break;
                case ValuePattern value:
                    value.SetValue(target.Value!);
                    break;
                case ScrollPattern scroll:
                    scroll.Scroll(
                        target.ScrollDirection switch { "left" => ScrollAmount.SmallDecrement, "right" => ScrollAmount.SmallIncrement, _ => ScrollAmount.NoAmount },
                        target.ScrollDirection switch { "up" => ScrollAmount.SmallDecrement, "down" => ScrollAmount.SmallIncrement, _ => ScrollAmount.NoAmount });
                    break;
                default:
                    return new DesktopActionResult(false, true, "The reviewed control no longer exposes the authorized action.");
            }
            return new DesktopActionResult(true, true, "The native invocation returned. Its effect and the task goal are not yet verified.");
        }
        catch (Exception ex) when (
            ex is ElementNotAvailableException or InvalidOperationException or COMException
                or UnauthorizedAccessException)
        {
            return new DesktopActionResult(invoked, !invoked, invoked
                ? "The action returned an unknown outcome. MSGuide will not retry it; check the screen."
                : "The reviewed control could not be revalidated. No action was invoked.");
        }
    }
}
