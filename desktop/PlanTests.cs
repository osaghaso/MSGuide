using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;

namespace MSGuide.Desktop;

internal static class PlanTests
{
    private static readonly WindowChoice Window = new(new nint(0x1234), 42, "Synthetic resource", "SyntheticClass");
    private const string Resource = "resource-synthetic";
    private static Task NoDelay(TimeSpan _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private static ElementInfo Element(string label = "Continue", string action = "invoke", int runtime = 1,
        string role = "button") =>
        new(role, label, [0.1, 0.2, 0.3, 0.1],
            TargetId: AutomationEvidence.TargetId(Window, role, label, [0.1, 0.2, 0.3, 0.1],
                "synthetic-control", "WPF", 42, [1, runtime]),
            AutomationId: "synthetic-control", FrameworkId: "WPF", Action: action,
            ControlId: AutomationEvidence.ControlId(Window, 42, [1, runtime]));

    private static Observation Screen(int page, params ElementInfo[] elements) =>
        new(Guid.NewGuid().ToString(), Window.Id, Window.Title, DateTimeOffset.UtcNow,
            800, 600, $"Synthetic page {page}", elements.Length == 0 ? [Element()] : elements,
            null, ResourceId: Resource);

    private static PlanAction Action(ElementInfo element, bool observed = true, string? value = null) =>
        new("action", "Perform the synthetic step.",
            new(element.Role, element.Label, element.Action!, element.AutomationId, element.FrameworkId,
                element.Action == "toggle" ? element.ToggleState : null,
                element.Action == "select" ? false : null),
            observed ? element.ControlId : null, value,
            element.Action == "scroll" ? "down" : null,
            element.Action == "set_value" ? observed ? element.ValueHash : AutomationEvidence.ValueDigest("") : null);

    private static PlanSegment Segment(PlanAction[] steps, string boundary = "completion_candidate") =>
        new(Guid.NewGuid().ToString(), Window.Id, steps,
            new(boundary, "Synthetic boundary; goal not independently verified.",
                boundary == "completion_candidate" ? "" : "Explicitly review the required resource or information."),
            Resource);

    private static Guidance Reply(Observation observation, TaskProgress task, PlanSegment plan) =>
        new("synthetic-correlation", observation.Id, observation.WindowId, "Synthetic plan.", "next_step",
            null, [], "model", TaskId: task.TaskId, Step: task.Step, Plan: plan);

    internal static async Task RunAsync(List<string> checks)
    {
        foreach (int length in new[] { 3, 17, 31 })
        {
            int page = 0, modelCalls = 0, invocations = 0, captures = 0, shownStep = 0;
            var images = new List<bool>();
            var published = new List<int>();
            var invokedEvidence = new HashSet<string>();
            var task = new ScreenTaskSession("Complete a same-resource synthetic plan", Window.Id);
            var plan = Segment(Enumerable.Repeat(Action(Element()), length).ToArray());
            await task.RunAsync((image, _) =>
            {
                captures++;
                images.Add(image);
                return Task.FromResult(Screen(page));
            }, (observation, progress, _) =>
            {
                modelCalls++;
                return Task.FromResult(Reply(observation, progress, plan));
            }, (observation, target, _) =>
            {
                IntegrationTests.Require(Safety.ObservedTarget(target, observation.Elements)
                    && invokedEvidence.Add(observation.Id));
                page++;
                invocations++;
                return Task.FromResult(new DesktopActionResult(true, true, "Synthetic invocation returned."));
            }, true, () =>
            {
                IntegrationTests.Require(task.Status != "checkpoint");
                shownStep = MainWindow.PublishScreenTaskActions(task, shownStep,
                    (step, _) => published.Add(step));
            }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(modelCalls == 1 && invocations == length && task.PlanCursor == length
                && task.Status == "review_required" && task.Plan == plan
                && task.History.All(step => step.Outcome == "screen_changed" && step.AfterObservationId is not null)
                && captures == 2 * length + 1 && images.Count(image => image) == 1
                && published.SequenceEqual(Enumerable.Range(1, length))
                && task.History.Count == Math.Min(length, ScreenTaskSession.HistoryLimit));
            checks.Add($"plan-{length}-steps-1-model-call-continuous-every-step-verified-bounded-history");
        }

        foreach (string boundary in new[] { "plan_limit", "observation" })
        {
            int firstLength = boundary == "plan_limit" ? Safety.MaxPlanSteps : 3;
            int page = 0, calls = 0, captures = 0, images = 0;
            var task = new ScreenTaskSession("Continue through same-resource plan boundaries", Window.Id);
            var first = Segment(Enumerable.Repeat(Action(Element()), firstLength).ToArray(), boundary);
            var last = Segment(Enumerable.Repeat(Action(Element()), 9).ToArray());
            await task.RunAsync((image, _) =>
            {
                captures++;
                if (image) images++;
                return Task.FromResult(Screen(page));
            }, (observation, progress, _) =>
            {
                calls++;
                IntegrationTests.Require(progress.TaskId == task.Id && calls <= 2);
                if (calls == 2)
                    IntegrationTests.Require(progress.Step == firstLength + 1 && progress.Plan == first
                        && progress.PlanCursor == firstLength && progress.ReplanReason == boundary
                        && progress.History[^1].AfterObservationId is not null);
                return Task.FromResult(Reply(observation, progress, calls == 1 ? first : last));
            }, (_, _, _) =>
            {
                page++;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }, true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(calls == 2 && images == 2 && page == firstLength + 9
                && captures == 2 * page + 2 && task.ActionsTaken == page
                && task.Plan == last && task.PlanCursor == 9 && task.Status == "review_required");
        }
        checks.Add("plan-limit-and-observation-refresh-automatically-after-safe-progress");

        foreach (string interruption in new[] { "resource", "incomplete", "cancelled" })
        {
            int page = 0, calls = 0, images = 0;
            using var cancellation = new CancellationTokenSource();
            var task = new ScreenTaskSession("Recheck scope before automatic planning", Window.Id);
            var plan = Segment([Action(Element())], "observation");
            await task.RunAsync((image, _) =>
            {
                bool refresh = image && ++images == 2;
                return Task.FromResult(Screen(page) with
                {
                    ResourceId = refresh && interruption == "resource" ? "resource-other" : Resource,
                    AutomationComplete = !refresh || interruption != "incomplete"
                });
            }, (observation, progress, _) =>
            {
                calls++;
                return Task.FromResult(Reply(observation, progress, plan));
            }, (_, _, _) =>
            {
                page++;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }, true, () =>
            {
                if (interruption == "cancelled" && task.ReplanReason == "observation")
                    cancellation.Cancel();
            }, cancellation.Token, NoDelay);
            IntegrationTests.Require(calls == 1 && page == 1 && task.PlanCursor == 1
                && task.Status == (interruption == "cancelled" ? "cancelled" : "blocked"));
        }
        checks.Add("automatic-replanning-stops-before-new-inference-on-scope-loss-or-cancellation");

        foreach (bool complete in new[] { false, true })
        {
            var guided = new ScreenTaskSession("Describe an approved partial screen", Window.Id);
            int guides = 0;
            await guided.RunAsync((_, _) => Task.FromResult(Screen(0) with { AutomationComplete = complete }),
                (observation, progress, _) =>
                {
                    guides++;
                    return Task.FromResult(Reply(observation, progress, Segment([Action(Element(), observed: false)])));
                }, (_, _, _) => throw new InvalidOperationException("Guide mode executed."),
                false, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(guides == 1 && guided.ActionsTaken == 0 && guided.Plan?.Steps.Length == 1
                && guided.Status == "needs_input" && guided.ReplanRequired);
        }
        var incomplete = new ScreenTaskSession("Do not execute partial grounding", Window.Id);
        await incomplete.RunAsync((_, _) => Task.FromResult(Screen(0) with { AutomationComplete = false }),
            (_, _, _) => throw new InvalidOperationException("Incomplete executable context was shared."),
            (_, _, _) => throw new InvalidOperationException("Incomplete evidence executed."),
            true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(incomplete.Status == "blocked" && incomplete.ActionsTaken == 0);
        checks.Add("plan-guide-partial-context-descriptive-only-no-execution");

        foreach (string boundary in new[] { "resource", "needs_input", "permission", "observation", "unsupported", "plan_limit" })
        {
            var task = new ScreenTaskSession("Pause at the next resource boundary", Window.Id);
            var plan = Segment([], boundary);
            int calls = 0;
            await task.RunAsync((_, _) => Task.FromResult(Screen(0)), (observation, progress, _) =>
            {
                calls++;
                return Task.FromResult(Reply(observation, progress, plan));
            }, (_, _, _) => throw new InvalidOperationException("A boundary executed."),
                true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(calls == 1 && task.Plan == plan && task.PlanCursor == 0
                && task.Status == "needs_input" && task.Detail.Contains(plan.Boundary.Needed, StringComparison.Ordinal));
        }

        foreach (string failure in new[] { "missing", "ambiguous", "replaced", "disabled", "password", "renamed", "value_drift", "resource" })
        {
            int page = 0, calls = 0, actions = 0;
            var first = Element();
            var second = Element("Second", failure == "value_drift" ? "set_value" : "invoke", 2,
                role: failure == "value_drift" ? "edit" : "button");
            if (failure == "value_drift")
                second = second with { Role = "edit", IsReadOnly = false, ValueHash = AutomationEvidence.ValueDigest("before"), ValueLength = 6 };
            var plan = Segment([Action(first), Action(second, value: failure == "value_drift" ? "after" : null)]);
            var task = new ScreenTaskSession("Do not act through unexpected drift", Window.Id);
            await task.RunAsync((_, _) =>
            {
                ElementInfo[] elements = [first, second];
                if (page > 0)
                    elements = failure switch
                    {
                        "missing" => [first],
                        "ambiguous" => [first, second, second with { TargetId = "uia-ambiguous", Box = [0.5, 0.2, 0.3, 0.1] }],
                        "replaced" => [first, Element("Second", runtime: 3)],
                        "disabled" => [first, second with { IsEnabled = false }],
                        "password" => [first, second with { IsPassword = true }],
                        "renamed" => [first, second with { Label = "Unexpected" }],
                        "value_drift" => [first, second with { ValueHash = AutomationEvidence.ValueDigest("unexpected"), ValueLength = 10 }],
                        _ => elements
                    };
                return Task.FromResult(Screen(page, elements) with
                { ResourceId = failure == "resource" && page > 0 ? "resource-other" : Resource });
            }, (observation, progress, _) =>
            {
                calls++;
                return Task.FromResult(Reply(observation, progress, plan));
            }, (_, _, _) =>
            {
                page++;
                actions++;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }, true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(calls == 1 && actions == 1 && task.PlanCursor == 1
                && task.Status == "blocked" && task.ReplanRequired && task.Plan == plan);
        }
        var nonempty = Element("Field", "set_value", role: "edit") with
        { Role = "edit", ValueHash = AutomationEvidence.ValueDigest("unexpected"), ValueLength = 10, IsReadOnly = false };
        IntegrationTests.Require(Safety.BindPlanAction(Action(nonempty, observed: false, value: "replacement"),
            Screen(0, nonempty)) is null);
        IntegrationTests.Require(Safety.BindPlanAction(Action(nonempty, value: "replacement"),
            Screen(0, nonempty with { IsReadOnly = true })) is null);
        IntegrationTests.Require(Safety.BindPlanAction(Action(Element(), observed: false),
            Screen(0, Element() with { ControlId = null })) is null);
        checks.Add("plan-resource-info-boundaries-unresolved-ambiguous-replaced-protected-value-drift-no-next-action");

        var valid = Segment([Action(Element()), Action(Element()), Action(Element())]);
        var invalidPlans = new[]
        {
            valid with { Steps = [valid.Steps[0], valid.Steps[1], valid.Steps[2] with { Kind = "shell" }] },
            valid with { Steps = [valid.Steps[0], valid.Steps[1], valid.Steps[2] with { Value = "unexpected input" }] },
            valid with { Steps = [valid.Steps[0], valid.Steps[1], valid.Steps[2] with { ControlId = "invented-opaque-id" }] },
            valid with { Steps = [valid.Steps[0], valid.Steps[1], valid.Steps[2] with { Instruction = new string('x', 501) }] },
            valid with { Steps = Enumerable.Repeat(valid.Steps[0], Safety.MaxPlanSteps + 1).ToArray() },
            valid with { Steps = Enumerable.Repeat(valid.Steps[0], Safety.MaxPlanSteps).ToArray() },
            valid with { Boundary = new("permission", "Missing permission.", "") },
            valid with { WindowId = "another-window" },
            valid with { ResourceId = "another-resource" },
            valid with { Steps = [new("manual", "User action required."), valid.Steps[0]] }
        };
        foreach (var plan in invalidPlans)
        {
            var task = new ScreenTaskSession("Validate the whole plan first", Window.Id);
            int actions = 0;
            await task.RunAsync((_, _) => Task.FromResult(Screen(0)),
                (observation, progress, _) => Task.FromResult(Reply(observation, progress, plan)),
                (_, _, _) => { actions++; return Task.FromResult(new DesktopActionResult(true, true, "Must not run.")); },
                true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(actions == 0 && task.Status == "failed" && task.History.Count == 0);
        }
        bool unknownFieldRejected = false;
        try { JsonSerializer.Deserialize<PlanAction>("""{"Kind":"action","Instruction":"Synthetic","shell":"ignored?"}"""); }
        catch (JsonException) { unknownFieldRejected = true; }
        IntegrationTests.Require(unknownFieldRejected);
        var limitPlan = Segment(Enumerable.Repeat(Action(Element()), Safety.MaxPlanSteps).ToArray(), "plan_limit");
        IntegrationTests.Require(Safety.ValidPlan(limitPlan, Screen(0)));
        var writable = Element("Field", "set_value", 2, role: "edit") with
        { Role = "edit", ValueHash = AutomationEvidence.ValueDigest(""), ValueLength = 0, IsReadOnly = false };
        IntegrationTests.Require(Safety.ValidPlan(Segment([
            Action(Element()), Action(Element()), Action(writable, value: new string('x', 1000))]),
            Screen(0, Element(), writable)));
        foreach (string value in new[] { new string('x', 1001), "\0" })
            IntegrationTests.Require(!Safety.ValidPlan(Segment([
                Action(Element()), Action(Element()), Action(writable, value: value)]), Screen(0, Element(), writable)));
        IntegrationTests.Require(!Safety.ValidPlan(Segment([
            Action(Element()) with { ScrollDirection = "sideways" }]), Screen(0)));
        var scrolling = Element("List", "scroll", 2) with { ScrollDirections = ["down"], VerticalScrollPercent = 0 };
        IntegrationTests.Require(Safety.ValidPlan(Segment([Action(scrolling)]), Screen(0, scrolling))
            && !Safety.ValidPlan(Segment([Action(scrolling) with { ScrollDirection = "sideways" }]), Screen(0, scrolling))
            && !Safety.ValidPlan(Segment([Action(scrolling) with { ScrollDirection = "left" }]), Screen(0, scrolling)));
        checks.Add("plan-malformed-third-step-and-schema-bounds-rejected-before-first-action");

        foreach (PlanSegment? wirePlan in new PlanSegment?[]
        {
            valid, null, valid with { Steps = [valid.Steps[0], valid.Steps[1], valid.Steps[2] with { Kind = "shell" }] }
        })
        {
            var handler = new PlanHandler(wirePlan);
            using var api = new ApiClient(handler, "synthetic-token");
            int actions = 0;
            var task = new ScreenTaskSession("Use the actual HTTP client contract", Window.Id);
            await task.RunAsync((_, _) => Task.FromResult(Screen(actions)),
                (observation, progress, token) => api.Guide(observation, task.Prompt, token, progress),
                (_, _, _) =>
                {
                    actions++;
                    return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
                }, true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(handler.GuidanceCalls == 1 && handler.RequestedPlan);
            IntegrationTests.Require(ReferenceEquals(wirePlan, valid)
                ? actions == 3 && task.Status == "review_required"
                : actions == 0 && task.Status == "failed" && task.History.Count == 0);
            if (wirePlan is null)
            {
                var legacy = await api.Guide(Screen(0), "Explicit legacy compatibility", CancellationToken.None,
                    task.Progress, planSegments: false);
                IntegrationTests.Require(legacy.Plan is null && legacy.Target is not null && !handler.RequestedPlan);
            }
        }
        checks.Add("plan-http-client-whole-segment-one-call-no-silent-legacy-fallback");

        foreach (bool unknown in new[] { false, true })
        {
            int page = 0, actions = 0, calls = 0;
            using var cancellation = new CancellationTokenSource();
            var task = new ScreenTaskSession("Never resume cancelled or unknown queued work", Window.Id);
            Task<Observation> Capture(bool _, CancellationToken token) => Task.FromResult(Screen(page));
            Task<Guidance> Guide(Observation observation, TaskProgress progress, CancellationToken token)
            {
                calls++;
                return Task.FromResult(Reply(observation, progress, valid));
            }
            Task<DesktopActionResult> Execute(Observation _, TargetInfo target, CancellationToken token)
            {
                actions++;
                page++;
                if (actions == 2 && !unknown) cancellation.Cancel();
                return Task.FromResult(new DesktopActionResult(true, !(unknown && actions == 2), "Synthetic result."));
            }
            await task.RunAsync(Capture, Guide, Execute, true, () => { }, cancellation.Token, NoDelay);
            IntegrationTests.Require(actions == 2 && calls == 1 && task.Status == "unknown"
                && !task.CanContinue && task.PlanCursor == 1 && task.History[^1].Outcome == "unknown");
            bool refused = false;
            try { await task.RunAsync(Capture, Guide, Execute, true, () => { }, CancellationToken.None, NoDelay); }
            catch (InvalidOperationException) { refused = true; }
            IntegrationTests.Require(refused && actions == 2 && calls == 1);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var task = new ScreenTaskSession("Cancel before the queued action", Window.Id);
            int actions = 0;
            await task.RunAsync((_, _) => Task.FromResult(Screen(0)),
                (observation, progress, _) => Task.FromResult(Reply(observation, progress, valid)),
                (_, _, _) => { actions++; return Task.FromResult(new DesktopActionResult(true, true, "Must not run.")); },
                true, () => { if (task.Plan is not null) cancellation.Cancel(); }, cancellation.Token, NoDelay);
            IntegrationTests.Require(actions == 0 && task.Status == "cancelled" && !task.CanContinue);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            int page = 0, calls = 0;
            var task = new ScreenTaskSession("Stop after the old action limit without executing the next step", Window.Id);
            var plan = Segment(Enumerable.Repeat(Action(Element()), 17).ToArray());
            await task.RunAsync((_, _) => Task.FromResult(Screen(page)),
                (observation, progress, _) =>
                {
                    calls++;
                    return Task.FromResult(Reply(observation, progress, plan));
                }, (_, _, _) =>
                {
                    page++;
                    return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
                }, true, () =>
                {
                    if (task.PlanCursor == 9) cancellation.Cancel();
                }, cancellation.Token, NoDelay);
            IntegrationTests.Require(page == 9 && calls == 1 && task.PlanCursor == 9
                && task.Status == "cancelled" && !task.CanContinue
                && task.History[^1].AfterObservationId is not null);
        }
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
        {
            var cancelledCapture = new ScreenTaskSession("Cancel while awaiting observation", Window.Id);
            await cancelledCapture.RunAsync(async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return Screen(0);
            }, (_, _, _) => throw new InvalidOperationException("Cancelled capture reached inference."),
                (_, _, _) => throw new InvalidOperationException("Cancelled capture executed."),
                true, () => { }, cancellation.Token, NoDelay);
            IntegrationTests.Require(cancelledCapture.Status == "cancelled"
                && !cancelledCapture.CanContinue && cancelledCapture.ActionsTaken == 0);
        }
        checks.Add("continuous-plan-cancellation-after-nine-actions-unknown-outcomes-never-replay");

        await CheckStableEffects();
        checks.Add("stable-control-effects-survive-label-position-changes-missing-runtime-fails-closed");
        var ui = new MainWindow(new SpeechService(() => new SpeechTests.FakeRecognizer()));
        try { await ui.CheckCompactPlanControls(); }
        finally { ui.Close(); }
        checks.Add("compact-guide-fix-continue-reply-stop-use-existing-handlers");
    }

    private sealed class PlanHandler(PlanSegment? plan) : HttpMessageHandler
    {
        internal int GuidanceCalls;
        internal bool RequestedPlan;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/sessions")
                return new(HttpStatusCode.OK)
                { Content = JsonContent.Create(new SessionInfo("synthetic-session", DateTimeOffset.UtcNow.AddHours(1))) };
            var body = await request.Content!.ReadFromJsonAsync<GuidanceRequest>(token)
                ?? throw new InvalidOperationException("Missing synthetic request.");
            GuidanceCalls++;
            RequestedPlan = body.PlanSegments;
            var progress = body.Task ?? throw new InvalidOperationException("Missing synthetic task context.");
            var element = body.Observation.Elements[0];
            var guidance = new Guidance("synthetic-correlation", body.Observation.Id, body.Observation.WindowId,
                "Synthetic transport result.", "next_step",
                plan is null ? new(element.Label, element.Box, element.Confidence,
                    element.TargetId, Action: element.Action, ControlId: element.ControlId) : null,
                [], "model", TaskId: progress.TaskId, Step: progress.Step, Plan: plan);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(guidance) };
        }
    }

    private static async Task CheckStableEffects()
    {
        foreach (string action in new[] { "toggle", "set_value", "select", "expand", "collapse", "scroll" })
        foreach (string variant in new[] { "stable", "renamed", "moved" })
        {
            bool applied = false;
            int captures = 0, actions = 0;
            var task = new ScreenTaskSession("Observe stable logical control identity", Window.Id);
            await task.RunAsync((_, _) =>
            {
                captures++;
                string label = applied && variant == "renamed" ? "Renamed control" : "Original control";
                double[] box = applied && variant == "moved" ? [0.2, 0.3, 0.3, 0.1] : [0.1, 0.2, 0.3, 0.1];
                var element = Element(label, action) with
                {
                    Box = box,
                    TargetId = AutomationEvidence.TargetId(Window, "button", label, box, "synthetic-control", "WPF", 42, [1, 1]),
                    Action = applied ? action switch { "expand" => "collapse", "collapse" => "expand", _ => action } : action,
                    ToggleState = action == "toggle" ? applied ? "on" : "off" : null,
                    IsSelected = action == "select" ? applied : null,
                    IsReadOnly = action == "set_value" ? false : null,
                    ValueHash = action == "set_value" ? AutomationEvidence.ValueDigest(applied ? "new" : "") : null,
                    ValueLength = action == "set_value" ? applied ? 3 : 0 : null,
                    ScrollDirections = action == "scroll" ? ["down"] : null,
                    VerticalScrollPercent = action == "scroll" ? applied ? 10 : 0 : null
                };
                return Task.FromResult(Screen(0, element));
            }, (observation, progress, _) =>
            {
                var element = observation.Elements[0];
                TargetInfo? target = applied ? null : new(element.Label, element.Box, element.Confidence,
                    element.TargetId, ToggleState: element.ToggleState, Action: element.Action,
                    Value: action == "set_value" ? "new" : null,
                    ScrollDirection: action == "scroll" ? "down" : null, ValueHash: element.ValueHash,
                    ControlId: element.ControlId);
                return Task.FromResult(new Guidance("synthetic", observation.Id, Window.Id, "Synthetic effect.",
                    applied ? "completion_candidate" : "next_step", target, [], "model", TaskId: progress.TaskId, Step: progress.Step));
            }, (_, _, _) =>
            {
                actions++;
                applied = true;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }, true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(actions == 1 && captures == 2 && task.Status == "review_required"
                && task.History.Single().Outcome == "effect_observed");
        }
        IntegrationTests.Require(AutomationEvidence.ControlId(Window, 42, null) is null
            && AutomationEvidence.ControlId(Window, 42, []) is null
            && AutomationEvidence.ControlId(Window, 0, [1]) is null
            && AutomationEvidence.ControlId(Window, 42, [1, 2]) != AutomationEvidence.ControlId(Window, 42, [1, 3])
            && AutomationEvidence.ResourceId(Window, "Synthetic resource", [Element() with { Role = "document" }]) is null);
    }
}

public partial class MainWindow
{
    internal async Task CheckCompactPlanControls()
    {
        loaded = true;
        try
        {
            var selected = new WindowChoice(new nint(0x1234), 42, "Synthetic resource", "SyntheticClass");
            WindowPicker.ItemsSource = new[] { selected };
            WindowPicker.SelectedItem = selected;
            var observation = new Observation("synthetic-observation", selected.Id, selected.Title,
                DateTimeOffset.UtcNow, 800, 600, "", [], null, ResourceId: "resource-synthetic");
            var task = new ScreenTaskSession("Retained compact task", selected.Id);
            await task.RunAsync((_, _) => Task.FromResult(observation),
                (observed, progress, _) => Task.FromResult(new Guidance("synthetic", observed.Id, selected.Id,
                    "Need information.", "next_step", null, [], "model", TaskId: progress.TaskId, Step: progress.Step,
                    Plan: new(Guid.NewGuid().ToString(), selected.Id, [],
                        new("needs_input", "A synthetic reply is needed.", "Provide the missing information."), observed.ResourceId))),
                (_, _, _) => throw new InvalidOperationException("Compact fixture executed."),
                true, () => { }, CancellationToken.None);
            screenTask = task;
            UpdateScreenTaskUi();
            var compact = companion.Prompt;
            IntegrationTests.Require(compact.ModeDescription.Contains("GUIDE", StringComparison.Ordinal)
                && compact.TaskDescription.Contains("Provide the missing information.", StringComparison.Ordinal)
                && compact.ContinueTaskButton.IsEnabled);
            compact.TaskReply.Text = "Synthetic clarification";
            compact.ContinueTaskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(task.UserInput == "Synthetic clarification"
                && PromptFeedbackText.Text.Contains("approve screen context", StringComparison.Ordinal));
            compact.SwitchModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                && task.ReplanRequired && compact.ModeDescription.Contains("FIX", StringComparison.Ordinal));
            compact.SwitchModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(SelectedCameraMode == CameraRecoveryInteractionMode.Guide);
            compact.StopTaskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(task.Status == "cancelled" && !task.CanContinue
                && !compact.ContinueTaskButton.IsEnabled && !compact.StopTaskButton.IsEnabled);
        }
        finally { loaded = false; }
    }
}
