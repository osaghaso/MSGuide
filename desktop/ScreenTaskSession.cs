using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MSGuide.Desktop;

/// <summary>One local task checkpoint. Observations are fresh; history never grants action authority.</summary>
internal sealed class ScreenTaskSession(string prompt, string windowId)
{
    internal const int ActionBudget = 8;
    internal const int HistoryLimit = 16;
    internal const int ObservationAttempts = 6;
    internal static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(5);
    private readonly List<TaskStep> history = [];
    private readonly Queue<string> attemptedStates = new();
    private int version;
    private bool actionInFlight;
    private TaskStep? pendingAction;
    private string? unchangedState;
    private readonly Stopwatch lifetime = new();

    internal string Id { get; } = Guid.NewGuid().ToString();
    internal string Prompt { get; } = prompt;
    internal string WindowId { get; } = windowId;
    internal int Step { get; private set; } = 1;
    internal int ActionsTaken { get; private set; }
    internal string Status { get; private set; } = "checkpoint";
    internal string Detail { get; private set; } = "Ready for a fresh task observation.";
    internal string RemainingWork { get; private set; } = "";
    internal string UserInput { get; set; } = "";
    internal IReadOnlyList<TaskStep> History => history;
    internal bool Running => Status == "running";
    internal bool AwaitingActionEvidence => pendingAction is not null;
    internal bool CanContinue => !Running && Status != "unknown" && Step < 10000;
    internal TaskProgress Progress => new(Id, Step, Status, history.ToArray(), RemainingWork, UserInput);

    internal void Stop()
    {
        if (!Running) return;
        bool uncertain = actionInFlight;
        if (pendingAction is { } pending) Record(pending);
        version++;
        SetStatus(uncertain ? "unknown" : "cancelled", uncertain
            ? "Stopped before the action's effect was verified. It may have executed; do not retry. Check the app manually."
            : "Cancelled. No further actions will start. The checkpoint is retained for explicit review.");
        DiagnosticLog.Record("screen_task_stopped", new
        {
            taskId = Id, step = Step, status = Status, actions = ActionsTaken,
            elapsedMs = lifetime.ElapsedMilliseconds
        });
    }

    private void SetStatus(string status, string detail)
    {
        Status = status;
        Detail = detail;
        DiagnosticLog.Record("screen_task_state", new { taskId = Id, step = Step, status, actions = ActionsTaken });
    }

    private void Record(Observation before, TargetInfo target, string outcome, Observation? after = null)
        => Record(new(Step, before.Id, after?.Id, target.TargetId!, target.Label, target.Action!, outcome));

    private void Record(TaskStep entry)
    {
        history.Add(entry);
        if (history.Count > HistoryLimit) history.RemoveAt(0);
        Step++;
        actionInFlight = false;
        pendingAction = null;
    }

    internal static string Fingerprint(Observation observation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            observation.WindowId, observation.OcrText, observation.Elements
        }))));

    private static bool EffectObserved(TargetInfo target, Observation before, Observation after)
    {
        var prior = before.Elements.SingleOrDefault(e => e.TargetId == target.TargetId);
        var current = after.Elements.SingleOrDefault(e => e.TargetId == target.TargetId);
        if (prior is null || current is null) return false;
        return target.Action switch
        {
            "set_value" => prior.ValueHash != current.ValueHash
                && current.ValueHash == AutomationEvidence.ValueDigest(target.Value!),
            "toggle" => current.ToggleState == (target.ToggleState == "off" ? "on" : "off"),
            "select" => prior.IsSelected is false && current.IsSelected is true,
            "expand" => current.Action == "collapse",
            "collapse" => current.Action == "expand",
            "scroll" => target.ScrollDirection switch
            {
                "up" => current.VerticalScrollPercent < prior.VerticalScrollPercent,
                "down" => current.VerticalScrollPercent > prior.VerticalScrollPercent,
                "left" => current.HorizontalScrollPercent < prior.HorizontalScrollPercent,
                "right" => current.HorizontalScrollPercent > prior.HorizontalScrollPercent,
                _ => false
            },
            _ => false
        };
    }

    internal async Task RunAsync(
        Func<bool, CancellationToken, Task<Observation>> capture,
        Func<Observation, TaskProgress, CancellationToken, Task<Guidance>> guide,
        Func<Observation, TargetInfo, CancellationToken, Task<DesktopActionResult>> execute,
        bool allowExecution, Action changed, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null)
    {
        if (!CanContinue) throw new InvalidOperationException("This task cannot continue without manual review or a new request.");
        if (UserInput.Length > 1000) throw new InvalidOperationException("Continuation input exceeds 1000 characters.");
        int mine = ++version;
        int budgetUsed = 0;
        var runClock = Stopwatch.StartNew();
        lifetime.Restart();
        var delay = delayForTest ?? Task.Delay;
        SetStatus("running", "Capturing fresh evidence for this task.");
        changed();
        void CheckCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mine != version) throw new OperationCanceledException();
        }
        async Task<Observation> Observe(bool image, CancellationToken token)
        {
            CheckCurrent();
            var clock = Stopwatch.StartNew();
            Observation result;
            try { result = await capture(image, token); }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                || ex is System.Runtime.InteropServices.COMException && ex.HResult == unchecked((int)0x80070005))
            {
                CheckCurrent();
                DiagnosticLog.Record("screen_task_capture_failed", new
                { taskId = Id, step = Step, errorType = ex.GetType().Name });
                throw new InvalidOperationException(
                    "Capture access was denied for the selected window. No observation was accepted. Review the app and its permissions before continuing.", ex);
            }
            CheckCurrent();
            token.ThrowIfCancellationRequested();
            if (result.WindowId != WindowId || !Safety.Fresh(result.CapturedAt, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("The observation is stale or belongs to another window.");
            DiagnosticLog.Record("screen_task_capture", new
            {
                taskId = Id, step = Step, elapsedMs = clock.ElapsedMilliseconds,
                imageShared = result.ImageBase64 is not null, elements = result.Elements.Length
            });
            return result;
        }
        try
        {
            var observation = await Observe(Step == 1, cancellationToken);
            if (!observation.AutomationComplete)
            {
                SetStatus("blocked", "The accessible controls inspection was incomplete. Use manual screen guidance or a supported surface; no task action was accepted.");
                return;
            }
            if (unchangedState == Fingerprint(observation))
            {
                SetStatus("no_progress", "The screen still matches the unverified action state. No retry was made. Make/check the change manually before continuing.");
                return;
            }
            unchangedState = null;
            while (budgetUsed < ActionBudget && Step < 10000)
            {
                CheckCurrent();
                Detail = $"Considering step {Step} from fresh evidence ({budgetUsed}/{ActionBudget} actions in this batch).";
                changed();
                var guideClock = Stopwatch.StartNew();
                var response = await guide(observation, Progress, cancellationToken);
                CheckCurrent();
                if (!Safety.Fresh(observation.CapturedAt, DateTimeOffset.UtcNow)
                    || !Safety.Matches(response, observation.Id, WindowId)
                    || response.TaskId != Id || response.Step != Step)
                    throw new InvalidOperationException("Stale or mismatched task response. No action was accepted.");
                DiagnosticLog.Record("screen_task_guidance", new
                {
                    taskId = Id, step = Step, response.CorrelationId, status = response.Status,
                    elapsedMs = guideClock.ElapsedMilliseconds
                });
                RemainingWork = response.RemainingWork ?? RemainingWork;
                if (response.Status != "next_step" || response.Target is null)
                {
                    Step++;
                    SetStatus(response.Status switch
                    {
                        "completed" or "completion_candidate" => "review_required",
                        "clarification" or "needs_input" => "needs_input",
                        _ => "blocked"
                    }, response.Status is "completed" or "completion_candidate"
                        ? "Completion suggested, not independently verified. Review the current app before continuing.\n" + response.Instruction
                        : "Task paused; completion was not verified.\n" + response.Instruction);
                    return;
                }
                var target = response.Target;
                if (!Safety.ObservedTarget(target, observation.Elements))
                    throw new InvalidOperationException("The action target or its inputs do not match the fresh observation.");
                if (!allowExecution || target.Action is null || string.IsNullOrEmpty(target.TargetId))
                {
                    Step++;
                    SetStatus("needs_input", "No action executed. Perform the guided step, then choose Review & continue for fresh evidence.\n" + response.Instruction);
                    return;
                }
                string before = Fingerprint(observation);
                string attemptKey = before + ":" + target.TargetId + ":" + target.Action + ":"
                    + target.ScrollDirection + ":" + (target.Value is null ? "" : AutomationEvidence.ValueDigest(target.Value));
                if (attemptedStates.Contains(attemptKey)
                    || target.Action == "set_value" && target.ValueHash == AutomationEvidence.ValueDigest(target.Value!))
                {
                    unchangedState = before;
                    SetStatus("no_progress", "This action has already been attempted on the same observed state, or the field already has the requested value. No retry was made.");
                    return;
                }
                Detail = $"Invoking step {Step} once. Invocation alone is not verified progress.";
                changed();
                var actionClock = Stopwatch.StartNew();
                DiagnosticLog.Record("screen_task_action_started", new
                { taskId = Id, step = Step, action = target.Action });
                actionInFlight = true;
                pendingAction = new(Step, observation.Id, null, target.TargetId!, target.Label, target.Action!, "unknown");
                var result = await execute(observation, target, cancellationToken);
                CheckCurrent();
                DiagnosticLog.Record("screen_task_action", new
                {
                    taskId = Id, step = Step, action = target.Action,
                    result.Invoked, result.OutcomeKnown, elapsedMs = actionClock.ElapsedMilliseconds
                });
                if (result.Invoked)
                {
                    budgetUsed++;
                    ActionsTaken++;
                    attemptedStates.Enqueue(attemptKey);
                    if (attemptedStates.Count > HistoryLimit) attemptedStates.Dequeue();
                }
                if (!result.OutcomeKnown || !result.Invoked)
                {
                    Record(observation, target, result.OutcomeKnown ? "not_invoked" : "unknown");
                    SetStatus(result.OutcomeKnown ? "blocked" : "unknown", result.Detail);
                    return;
                }
                Detail = "Invocation returned. Checking its effect with fresh local observations...";
                changed();
                var verifyClock = Stopwatch.StartNew();
                using var verification = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                verification.CancelAfter(VerificationTimeout);
                Observation? after = null;
                string? candidate = null;
                bool requiresSemanticEffect = target.Action != "invoke";
                string outcome = requiresSemanticEffect ? "unknown" : "no_progress";
                try
                {
                    for (int attempt = 0; attempt < ObservationAttempts; attempt++)
                    {
                        if (attempt > 0) await delay(TimeSpan.FromMilliseconds(250), verification.Token);
                        var next = await Observe(false, verification.Token);
                        if (!next.AutomationComplete || next.Id == observation.Id || next.Id == after?.Id)
                            throw new InvalidOperationException("Post-action verification was incomplete or reused an observation.");
                        after = next;
                        string fingerprint = Fingerprint(after);
                        if (EffectObserved(target, observation, after))
                        { outcome = "effect_observed"; break; }
                        if (!requiresSemanticEffect && fingerprint != before && fingerprint == candidate)
                        { outcome = "screen_changed"; break; }
                        candidate = fingerprint != before ? fingerprint : null;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    CheckCurrent();
                    Record(observation, target, "unknown", after);
                    DiagnosticLog.Record("screen_task_verification", new
                    { taskId = Id, step = Step - 1, outcome = "unknown", elapsedMs = verifyClock.ElapsedMilliseconds });
                    SetStatus("unknown", "The invocation returned but its effect could not be checked within the bounded observation window. Do not retry; review the app manually.");
                    return;
                }
                CheckCurrent();
                Record(observation, target, outcome, after);
                DiagnosticLog.Record("screen_task_verification", new
                {
                    taskId = Id, step = Step - 1, outcome, elapsedMs = verifyClock.ElapsedMilliseconds
                });
                if (outcome == "unknown")
                {
                    SetStatus("unknown", "The expected control effect was not observed within the bounded observation window. Unrelated screen changes do not verify it. Do not retry; review the app manually.");
                    return;
                }
                if (outcome == "no_progress")
                {
                    unchangedState = Fingerprint(after!);
                    SetStatus("no_progress", "No stable progress was observed after the action. It was not retried. Review the app before continuing.");
                    return;
                }
                observation = after!;
                Detail = outcome == "effect_observed"
                    ? "The control effect was observed; the overall task goal is still unverified."
                    : "A stable screen change was observed, not proof of the action's effect or task completion.";
                changed();
            }
            if (Step >= 10000)
                SetStatus("blocked", "The task reached its 10,000-decision ceiling. Its checkpoint is retained for review, but continuing requires a new request. Goal completion is unverified.");
            else
                SetStatus("checkpoint", $"Paused after {budgetUsed} actions; the last action received a fresh verification check. Review & continue starts another bounded batch, not a new task. Goal completion is unverified.");
        }
        catch (OperationCanceledException)
        {
            if (mine == version)
            {
                bool uncertain = actionInFlight;
                if (pendingAction is { } pending) Record(pending);
                SetStatus(uncertain ? "unknown" : "cancelled", uncertain
                    ? "Cancelled before the action's effect was verified. Its outcome is unknown; do not retry."
                    : "Task cancelled. No further action will start; the checkpoint is retained.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or JsonException)
        {
            if (mine == version)
            {
                bool uncertain = actionInFlight;
                if (pendingAction is { } pending) Record(pending);
                SetStatus(uncertain ? "unknown" : "failed", uncertain
                    ? "An action may have started. Its outcome is unknown; no retry is allowed."
                    : ex is InvalidOperationException ? ex.Message
                    : "Guidance failed. No new action was accepted; review the checkpoint before continuing.");
            }
        }
        finally
        {
            if (mine == version)
            {
                actionInFlight = false;
                DiagnosticLog.Record("screen_task_stopped", new
                {
                    taskId = Id, step = Step, status = Status, actions = ActionsTaken,
                    elapsedMs = runClock.ElapsedMilliseconds
                });
                changed();
            }
        }
    }
}
