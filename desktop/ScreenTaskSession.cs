using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MSGuide.Desktop;

/// <summary>One local task checkpoint. Observations are fresh; history never grants action authority.</summary>
internal sealed class ScreenTaskSession(string prompt, string windowId)
{
    internal const int HistoryLimit = 16;
    internal const int ObservationAttempts = 6;
    // Two stable reads must fit the browser-before/UIA/browser-after inspection budgets.
    internal static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(30);
    private readonly List<TaskStep> history = [];
    private readonly Queue<string> attemptedStates = new();
    private int version;
    private bool actionInFlight;
    private TaskStep? pendingAction;
    private string? unchangedState;
    private readonly Stopwatch lifetime = new();
    private bool replanRequired;

    internal string Id { get; } = Guid.NewGuid().ToString();
    internal string Prompt { get; } = prompt;
    internal string WindowId { get; private set; } = windowId;
    internal int Step { get; private set; } = 1;
    internal int ActionsTaken { get; private set; }
    internal string Status { get; private set; } = "checkpoint";
    internal string Detail { get; private set; } = "Ready for a fresh task observation.";
    internal string RemainingWork { get; private set; } = "";
    internal string UserInput { get; set; } = "";
    internal PlanSegment? Plan { get; private set; }
    internal int PlanCursor { get; private set; }
    internal string ReplanReason { get; private set; } = "";
    internal bool ReplanRequired => replanRequired;
    internal IReadOnlyList<TaskStep> History => history;
    internal bool Running => Status == "running";
    internal bool AwaitingActionEvidence => pendingAction is not null;
    internal bool CanContinue => !Running && Status is not ("unknown" or "cancelled") && Step < 10000;
    internal bool CanApproveResourceHandoff(string windowId) =>
        CanContinue && ReplanReason == "resource" && WindowId != windowId && !string.IsNullOrWhiteSpace(windowId);
    internal TaskProgress Progress => new(Id, Step, Status, history.ToArray(), RemainingWork, UserInput,
        Plan, PlanCursor, ReplanReason);

    internal void Stop()
    {
        if (Status is "unknown" or "cancelled" || !Running && Plan is null) return;
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

    internal void RevokePlan()
    {
        if (Running) Stop();
        else if (Plan is not null && CanContinue)
            PauseForReplan("mode_changed", "Mode changed. The retained plan is descriptive only; review and request a fresh plan before executing.");
    }

    internal void ApproveResourceHandoff(string windowId)
    {
        if (!CanApproveResourceHandoff(windowId))
            throw new InvalidOperationException("This task is not waiting for an explicitly selected resource.");
        WindowId = windowId;
        Plan = null;
        PlanCursor = 0;
        replanRequired = true;
        ReplanReason = "resource_handoff";
        SetStatus("needs_input",
            "The selected window is approved for this task. Fresh evidence is required before planning or acting.");
        DiagnosticLog.Record("screen_task_resource_handoff", new { taskId = Id, step = Step });
    }

    private void PauseForReplan(string reason, string detail, string status = "blocked")
    {
        replanRequired = true;
        ReplanReason = reason;
        SetStatus(status, detail);
        DiagnosticLog.Record("screen_task_plan_boundary", new
        { taskId = Id, planId = Plan?.PlanId, cursor = PlanCursor, reason, status });
    }

    private void FinishPlan()
    {
        var boundary = Plan!.Boundary;
        PauseForReplan(boundary.Kind,
            (boundary.Kind == "completion_candidate"
                ? "Completion suggested, not independently verified. Review the current app.\n"
                : boundary.Kind == "resource"
                    ? "This task needs a different window. Select that window, then choose “Use selected window & continue.” MSGuide will not switch windows automatically.\n"
                    : "Plan segment stopped at a boundary. No new resource or permission was acquired.\n")
            + boundary.Reason + (boundary.Needed.Length == 0 ? "" : "\nNeeded: " + boundary.Needed),
            boundary.Kind == "completion_candidate" ? "review_required" : "needs_input");
    }

    internal static string DescribePlan(PlanSegment plan, int cursor = 0) =>
        $"Plan: {cursor}/{plan.Steps.Length} steps observed\n"
        + string.Join("\n", plan.Steps.Select((step, index) =>
            $"{index + 1}. {(index < cursor ? "[observed] " : "")}{step.Instruction}"))
        + $"\nBoundary: {plan.Boundary.Kind} - {plan.Boundary.Reason}"
        + (plan.Boundary.Needed.Length == 0 ? "" : "\nNeeded: " + plan.Boundary.Needed);

    private void SetStatus(string status, string detail)
    {
        Status = status;
        Detail = detail;
        DiagnosticLog.Record("screen_task_state", new
        { taskId = Id, step = Step, status, actions = ActionsTaken, planId = Plan?.PlanId, cursor = PlanCursor });
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
            observation.WindowId, observation.ResourceId, observation.OcrText, observation.Elements
        }))));

    private static bool EffectObserved(TargetInfo target, Observation before, Observation after)
    {
        var previous = before.Elements.Where(e => e.TargetId == target.TargetId).ToArray();
        var next = after.Elements.Where(e => target.ControlId is null
            ? e.TargetId == target.TargetId : e.ControlId == target.ControlId).ToArray();
        if (previous.Length != 1 || next.Length != 1 || next[0].IsPassword || next[0].IsOffscreen
            || target.ControlId is not null && (previous[0].ControlId != target.ControlId
                || before.Elements.Count(e => e.ControlId == target.ControlId) != 1)) return false;
        var prior = previous[0];
        var current = next[0];
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
            catch (InvalidOperationException ex)
            {
                CheckCurrent();
                DiagnosticLog.Record("screen_task_capture_failed", new
                {
                    taskId = Id, step = Step, errorType = ex.GetType().Name,
                    reason = ex is CaptureResourceChangedException ? "resource_changed_during_capture" : "capture_failed",
                    elapsedMs = clock.ElapsedMilliseconds
                });
                throw;
            }
            CheckCurrent();
            token.ThrowIfCancellationRequested();
            if (result.WindowId != WindowId || !Safety.Fresh(result.CapturedAt, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("The observation is stale or belongs to another window.");
            DiagnosticLog.Record("screen_task_capture", new
            {
                taskId = Id, step = Step, elapsedMs = clock.ElapsedMilliseconds,
                imageShared = result.ImageBase64 is not null, elements = result.Elements.Length,
                result.AutomationComplete, resourceVerified = result.ResourceId is not null
            });
            return result;
        }
        try
        {
            if (UserInput.Length > 0) replanRequired = true;
            var observation = await Observe(Step == 1 || replanRequired, cancellationToken);
            if (!observation.AutomationComplete && allowExecution)
            {
                SetStatus("blocked", "The selected app did not finish exposing its accessible controls in time. Switch to Guide mode or retry after the app settles; no task action was accepted.");
                return;
            }
            if (unchangedState == Fingerprint(observation))
            {
                SetStatus("no_progress", "The screen still matches the unverified action state. No retry was made. Make/check the change manually before continuing.");
                return;
            }
            unchangedState = null;
            while (Step < 10000)
            {
                CheckCurrent();
                TargetInfo? target = null;
                if (Plan is null || replanRequired)
                {
                    Detail = "Planning the next steps from fresh approved evidence...";
                    changed();
                    CheckCurrent();
                    var guideClock = Stopwatch.StartNew();
                    var response = await guide(observation, Progress, cancellationToken);
                    CheckCurrent();
                    if (!Safety.Fresh(observation.CapturedAt, DateTimeOffset.UtcNow)
                        || !Safety.Matches(response, observation.Id, WindowId)
                        || response.TaskId != Id || response.Step != Step
                        || response.Plan is not null && !Safety.ValidPlan(response.Plan, observation))
                        throw new InvalidOperationException("Stale, mismatched, or invalid whole-plan response. No action was accepted.");
                    DiagnosticLog.Record("screen_task_guidance", new
                    {
                        taskId = Id, step = Step, response.CorrelationId, status = response.Status,
                        planId = response.Plan?.PlanId, planSteps = response.Plan?.Steps.Length ?? 0,
                        elapsedMs = guideClock.ElapsedMilliseconds
                    });
                    RemainingWork = response.RemainingWork ?? RemainingWork;
                    UserInput = "";
                    if (response.Plan is { } segment)
                    {
                        Plan = segment;
                        PlanCursor = 0;
                        replanRequired = false;
                        ReplanReason = "";
                    }
                    else
                    {
                        Plan = null;
                        PlanCursor = 0;
                        replanRequired = false;
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
                        if (!Safety.ObservedTarget(response.Target, observation.Elements))
                            throw new InvalidOperationException("The legacy target or its inputs do not match the fresh observation.");
                        if (!allowExecution)
                        {
                            Step++;
                            SetStatus("needs_input", "Guide mode: no action executed. Review the guidance and continue explicitly.\n" + response.Instruction);
                            return;
                        }
                        target = response.Target;
                    }
                }
                if (Plan is { } plan)
                {
                    if (!allowExecution)
                    {
                        PauseForReplan("guide_review", "Guide mode: no action executed. Follow the descriptive plan, then review fresh evidence.",
                            "needs_input");
                        return;
                    }
                    if (PlanCursor == plan.Steps.Length)
                    {
                        if (plan.Steps.Length > 0 && plan.Boundary.Kind is "plan_limit" or "observation")
                        {
                            replanRequired = true;
                            ReplanReason = plan.Boundary.Kind;
                            Detail = "The planned steps were observed. Refreshing the same resource to continue...";
                            DiagnosticLog.Record("screen_task_plan_boundary", new
                            {
                                taskId = Id, planId = plan.PlanId, cursor = PlanCursor,
                                reason = ReplanReason, status = Status, automatic = true
                            });
                            changed();
                            observation = await Observe(true, cancellationToken);
                            if (!observation.AutomationComplete)
                            {
                                PauseForReplan("incomplete_observation",
                                    "The refreshed controls inspection was incomplete. No further step was planned or executed; review the app.");
                                return;
                            }
                            if (plan.ResourceId is null || observation.ResourceId != plan.ResourceId)
                            {
                                PauseForReplan("resource_changed",
                                    "The resource changed while refreshing the plan. No new model request or action was started; review the new resource.");
                                return;
                            }
                            continue;
                        }
                        FinishPlan();
                        return;
                    }
                    var planned = plan.Steps[PlanCursor];
                    if (planned.Kind == "manual")
                    {
                        PauseForReplan(plan.Boundary.Kind, planned.Instruction + "\nNeeded: " + plan.Boundary.Needed,
                            "needs_input");
                        return;
                    }
                    if (plan.ResourceId is null)
                    {
                        PauseForReplan("resource_unverified",
                            "MSGuide could not verify the active page identity. No action was started. Keep one supported browser page visible and capture it again; this is not a report that the page changed.");
                        return;
                    }
                    if (observation.ResourceId != plan.ResourceId)
                    {
                        PauseForReplan("resource_changed", "The selected resource changed or its identity cannot be established locally. No queued step was executed. Review the window/file/site and explicitly replan; a new window requires a new approved request.");
                        return;
                    }
                    target = Safety.BindPlanAction(planned, observation);
                    if (target is null)
                    {
                        PauseForReplan("target_not_grounded", $"Plan step {PlanCursor + 1} could not be uniquely grounded with its expected state. No action was started. Review changed, missing, ambiguous, or unsupported controls before replanning.");
                        return;
                    }
                    Detail = $"Plan step {PlanCursor + 1}/{plan.Steps.Length}: {planned.Instruction}";
                    changed();
                }
                CheckCurrent();
                if (target is null || !Safety.ObservedTarget(target, observation.Elements))
                    throw new InvalidOperationException("The action target or its inputs do not match the fresh observation.");
                if (target.Action is null || string.IsNullOrEmpty(target.TargetId))
                {
                    Step++;
                    SetStatus("needs_input", "No executable target. Perform the described step manually and review fresh evidence.");
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
                CheckCurrent();
                var actionClock = Stopwatch.StartNew();
                DiagnosticLog.Record("screen_task_action_started", new
                { taskId = Id, step = Step, action = target.Action, planId = Plan?.PlanId, cursor = PlanCursor });
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
                string reason = requiresSemanticEffect ? "effect_not_observed" : "no_stable_change";
                string? errorType = null;
                int attempts = 0;
                var observedIds = new HashSet<string> { observation.Id };
                void RejectObservation(string rejection, string? failureType = null)
                {
                    CheckCurrent();
                    verification.Token.ThrowIfCancellationRequested();
                    candidate = null;
                    outcome = "unknown";
                    reason = rejection;
                    errorType = failureType;
                    DiagnosticLog.Record("screen_task_observation_rejected", new
                    {
                        taskId = Id, step = Step, attempt = attempts, reason, errorType,
                        elapsedMs = verifyClock.ElapsedMilliseconds
                    });
                    if (attempts < ObservationAttempts)
                    {
                        Detail = "The app is still updating. Rechecking its screen; the action will not be repeated.";
                        changed();
                        CheckCurrent();
                    }
                }
                try
                {
                    for (int attempt = 0; attempt < ObservationAttempts; attempt++)
                    {
                        if (attempt > 0) await delay(TimeSpan.FromMilliseconds(250), verification.Token);
                        attempts++;
                        Observation next;
                        try { next = await Observe(false, verification.Token); }
                        catch (CaptureResourceChangedException ex)
                        {
                            RejectObservation("resource_changed_during_capture", ex.GetType().Name);
                            continue;
                        }
                        if (!observedIds.Add(next.Id))
                            throw new InvalidOperationException("Post-action verification reused an observation.");
                        if (!next.AutomationComplete)
                        {
                            RejectObservation("incomplete_observation");
                            continue;
                        }
                        if (observation.ResourceId is not null && next.ResourceId is null)
                        {
                            RejectObservation("resource_unverified");
                            continue;
                        }
                        after = next;
                        outcome = requiresSemanticEffect ? "unknown" : "no_progress";
                        reason = requiresSemanticEffect ? "effect_not_observed" : "no_stable_change";
                        errorType = null;
                        string fingerprint = Fingerprint(after);
                        if (requiresSemanticEffect && Plan is not null && after.ResourceId != Plan.ResourceId)
                        {
                            ReplanReason = "resource_changed";
                            reason = "resource_changed";
                            break;
                        }
                        if (EffectObserved(target, observation, after))
                        { outcome = reason = "effect_observed"; break; }
                        if (!requiresSemanticEffect && fingerprint != before && fingerprint == candidate)
                        { outcome = reason = "screen_changed"; break; }
                        candidate = fingerprint != before ? fingerprint : null;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    CheckCurrent();
                    outcome = "unknown";
                    reason = ex is OperationCanceledException && verification.IsCancellationRequested
                        ? "deadline" : "observation_failed";
                    errorType = ex.GetType().Name;
                }
                CheckCurrent();
                Record(observation, target, outcome, after);
                DiagnosticLog.Record("screen_task_verification", new
                {
                    taskId = Id, step = Step - 1, outcome, reason, attempts, errorType,
                    elapsedMs = verifyClock.ElapsedMilliseconds
                });
                if (outcome == "unknown")
                {
                    string failure = reason switch
                    {
                        "deadline" => "Screen verification timed out after 30 seconds.",
                        "incomplete_observation" => "The app kept returning incomplete controls during screen verification.",
                        "resource_changed_during_capture" => "The page kept changing during screen verification.",
                        "resource_unverified" => "The active page could not be identified during screen verification.",
                        "observation_failed" => "A screen inspection failed before the action's effect could be verified.",
                        "resource_changed" => "The resource changed before the control effect could be verified.",
                        _ => "The expected control effect was not observed. Unrelated screen changes do not verify it."
                    };
                    SetStatus("unknown", failure + " The action was invoked once and will not be repeated. Do not retry; review the app manually.");
                    return;
                }
                if (outcome == "no_progress")
                {
                    unchangedState = Fingerprint(after!);
                    SetStatus("no_progress", "No stable progress was observed after the action. It was not retried. Review the app before continuing.");
                    return;
                }
                observation = after!;
                if (Plan is not null)
                {
                    PlanCursor++;
                    if (observation.ResourceId != Plan.ResourceId)
                    {
                        PauseForReplan("resource_changed", "The action reached a different or unidentified resource. Remaining steps were retained but not executed. Review the new resource before explicitly replanning.");
                        return;
                    }
                }
                Detail = outcome == "effect_observed"
                    ? "The control effect was observed; the overall task goal is still unverified."
                    : "A stable screen change was observed, not proof of the action's effect or task completion.";
                changed();
            }
            if (Plan is not null && PlanCursor == Plan.Steps.Length) FinishPlan();
            else if (Step >= 10000)
                SetStatus("blocked", "The task reached its 10,000-decision ceiling. Its checkpoint is retained for review, but continuing requires a new request. Goal completion is unverified.");
        }
        catch (OperationCanceledException)
        {
            if (mine == version)
            {
                bool uncertain = actionInFlight;
                if (pendingAction is { } pending) Record(pending);
                SetStatus(uncertain ? "unknown" : "cancelled", uncertain
                    ? "Cancelled before the action's effect was verified. Its outcome is unknown; do not retry."
                    : "Task cancelled. No queued action can resume; the checkpoint is retained for review.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or JsonException)
        {
            if (mine == version)
            {
                bool uncertain = actionInFlight;
                DiagnosticLog.Record("screen_task_failed", new
                {
                    taskId = Id, step = Step, errorType = ex.GetType().Name,
                    actionPending = uncertain, actions = ActionsTaken
                });
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
