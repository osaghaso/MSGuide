using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace MSGuide.Desktop;

internal static class PromptTests
{
    private static Observation Screen(int state, ElementInfo? element = null) =>
        new(Guid.NewGuid().ToString(), "synthetic-window", "Synthetic task", DateTimeOffset.UtcNow,
            800, 600, $"Synthetic page {state}",
            [element ?? new("button", "Continue", [0.1, 0.2, 0.3, 0.1], TargetId: "uia-repeat", Action: "invoke")], null);

    private static Guidance Response(Observation observation, TaskProgress progress,
        string status = "next_step", string? value = null, string? direction = null)
    {
        var element = observation.Elements[0];
        return new("synthetic-correlation", observation.Id, observation.WindowId, "Synthetic instruction",
            status, status == "next_step" ? new(element.Label, element.Box, element.Confidence,
                TargetId: element.TargetId, ToggleState: element.ToggleState, Action: element.Action,
                Value: value, ScrollDirection: direction, ValueHash: element.ValueHash) : null,
            [], "model", "Continue the original goal.", progress.TaskId, progress.Step);
    }

    public static async Task RunTaskLoopAsync()
    {
        static Task NoDelay(TimeSpan _, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        int state = 0, calls = 0;
        var images = new List<bool>();
        var contexts = new List<TaskProgress>();
        var task = new ScreenTaskSession("Reach page 10", "synthetic-window");
        Task<Observation> Capture(bool image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            images.Add(image);
            return Task.FromResult(Screen(state));
        }
        Task<Guidance> Guide(Observation observation, TaskProgress progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            contexts.Add(progress);
            return Task.FromResult(Response(observation, progress, state < 10 ? "next_step" : "completion_candidate"));
        }
        Task<DesktopActionResult> Execute(Observation _, TargetInfo target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            state++;
            return Task.FromResult(new DesktopActionResult(true, true, "Invocation returned; effect unverified."));
        }
        await task.RunAsync(Capture, Guide, Execute, true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(task.Status == "review_required" && calls == 10 && task.ActionsTaken == 10);
        IntegrationTests.Require(task.History.Count == 10 && task.History.All(step => step.Outcome == "screen_changed"));
        IntegrationTests.Require(images.Count == 21 && images.Count(image => image) == 1);
        IntegrationTests.Require(task.History.All(step => step.AfterObservationId is not null));
        string originalId = task.Id;
        IntegrationTests.Require(task.Detail.Contains("not independently verified", StringComparison.Ordinal));
        IntegrationTests.Require(contexts[8].History.Length == 8 && contexts[8].Step == 9
            && contexts[8].TaskId == originalId && contexts[8].RemainingWork.Length > 0);
        IntegrationTests.Require(contexts.Skip(1).All(context => context.History.Length > 0));
        var bounded = new ScreenTaskSession("Long synthetic task", "synthetic-window");
        var boundedPublished = new List<int>();
        int goal = state + 24, boundedShownStep = 0;
        await bounded.RunAsync(Capture,
            (observation, progress, _) => Task.FromResult(Response(observation, progress,
                state < goal ? "next_step" : "completion_candidate")),
            Execute, true, () => boundedShownStep = MainWindow.PublishScreenTaskActions(
                bounded, boundedShownStep, (step, _) => boundedPublished.Add(step)),
            CancellationToken.None, NoDelay);
        IntegrationTests.Require(bounded.Status == "review_required" && boundedShownStep == 24
            && bounded.ActionsTaken == 24 && bounded.History.Count == ScreenTaskSession.HistoryLimit
            && bounded.History[0].Step == 9 && bounded.Progress.History.Length == 16
            && boundedPublished.SequenceEqual(Enumerable.Range(1, 24)));

        int notificationState = 0, notificationShownStep = 0;
        var notifications = new List<string>();
        var notificationTask = new ScreenTaskSession("Toggle then invoke", "synthetic-window");
        Task<Observation> NotificationCapture(bool _, CancellationToken token)
        {
            var toggle = new ElementInfo("button", "Synthetic toggle", [0.1, 0.2, 0.3, 0.1],
                TargetId: "uia-toggle", Action: "toggle", ToggleState: notificationState == 0 ? "off" : "on");
            var invoke = new ElementInfo("button", "Continue", [0.1, 0.4, 0.3, 0.1],
                TargetId: "uia-invoke", Action: "invoke");
            return Task.FromResult(Screen(notificationState) with { Elements = [toggle, invoke] });
        }
        void PublishNotifications()
        {
            notificationShownStep = MainWindow.PublishScreenTaskActions(notificationTask, notificationShownStep,
                (step, result) => notifications.Add($"{step}. {result}"));
            IntegrationTests.Require(notifications.Count == notificationTask.History.Count);
        }
        await notificationTask.RunAsync(NotificationCapture,
            (observation, progress, _) => Task.FromResult(Response(
                observation with { Elements = [observation.Elements[notificationState == 0 ? 0 : 1]] },
                progress, notificationState < 2 ? "next_step" : "completion_candidate")),
            (_, _, _) =>
            {
                notificationState++;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }, true, PublishNotifications, CancellationToken.None, NoDelay);
        IntegrationTests.Require(notificationTask.Status == "review_required" && notificationTask.ActionsTaken == 2
            && notifications.SequenceEqual(["1. toggle - effect_observed", "2. invoke - screen_changed"]));

        var noProgress = new ScreenTaskSession("Make a change", "synthetic-window");
        int noOpCalls = 0, captures = 0;
        Task<Observation> Unchanged(bool _, CancellationToken token)
        { captures++; return Task.FromResult(Screen(0)); }
        Task<DesktopActionResult> NoOp(Observation _, TargetInfo target, CancellationToken token)
        { noOpCalls++; return Task.FromResult(new DesktopActionResult(true, true, "Returned.")); }
        Task<Guidance> Next(Observation observation, TaskProgress progress, CancellationToken token) =>
            Task.FromResult(Response(observation, progress));
        await noProgress.RunAsync(Unchanged, Next, NoOp, true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(noProgress.Status == "no_progress" && noOpCalls == 1
            && captures == ScreenTaskSession.ObservationAttempts + 1);
        await noProgress.RunAsync(Unchanged, Next, NoOp, true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(noOpCalls == 1 && noProgress.Status == "no_progress");
        var notInvoked = new ScreenTaskSession("Retry only after explicit review", "synthetic-window");
        int retryShownStep = 0;
        var retryNotifications = new List<string>();
        void PublishRetry() => retryShownStep = MainWindow.PublishScreenTaskActions(notInvoked, retryShownStep,
            (step, result) => retryNotifications.Add($"{step}. {result}"));
        await notInvoked.RunAsync(Unchanged, Next,
            (_, _, _) => Task.FromResult(new DesktopActionResult(false, true, "No invocation started.")),
            true, PublishRetry, CancellationToken.None, NoDelay);
        IntegrationTests.Require(notInvoked.Status == "blocked" && notInvoked.CanContinue
            && notInvoked.History.Single().Outcome == "not_invoked" && notInvoked.ActionsTaken == 0
            && retryNotifications.SequenceEqual(["1. invoke - not_invoked"]));
        int beforeResume = noOpCalls;
        retryShownStep = notInvoked.History.LastOrDefault()?.Step ?? 0;
        await notInvoked.RunAsync(Unchanged, Next, NoOp, true, PublishRetry, CancellationToken.None, NoDelay);
        IntegrationTests.Require(noOpCalls == beforeResume + 1 && notInvoked.Status == "no_progress"
            && notInvoked.ActionsTaken == 1
            && retryNotifications.SequenceEqual(["1. invoke - not_invoked", "2. invoke - no_progress"]));

        foreach (string status in new[] { "blocked", "needs_input", "completion_candidate", "completed" })
        {
            var stopped = new ScreenTaskSession("Synthetic goal", "synthetic-window");
            await stopped.RunAsync(Unchanged,
                (observation, progress, _) => Task.FromResult(Response(observation, progress, status)),
                (_, _, _) => throw new InvalidOperationException("Non-action status executed."),
                true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(stopped.Status == (status is "completed" or "completion_candidate"
                ? "review_required" : status) && stopped.ActionsTaken == 0 && stopped.CanContinue);
        }
        var guideOnly = new ScreenTaskSession("Guide me", "synthetic-window");
        await guideOnly.RunAsync(Unchanged, Next,
            (_, _, _) => throw new InvalidOperationException("Guide mode executed."),
            false, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(guideOnly.Status == "needs_input" && guideOnly.ActionsTaken == 0);
        var demoChange = new ScreenTaskSession("Observe a demo transition", "synthetic-window");
        await demoChange.RunAsync(Unchanged,
            (observation, progress, _) =>
            {
                IntegrationTests.Require(!MainWindow.PreserveTaskDemoChange(demoChange, observation.WindowId));
                return Task.FromResult(Response(observation, progress));
            },
            (observation, _, _) =>
            {
                IntegrationTests.Require(MainWindow.PreserveTaskDemoChange(demoChange, observation.WindowId)
                    && !MainWindow.PreserveTaskDemoChange(demoChange, "other-window"));
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            },
            true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(demoChange.Status == "no_progress"
            && !MainWindow.PreserveTaskDemoChange(demoChange, "synthetic-window"));
        var incomplete = new ScreenTaskSession("Incomplete screen", "synthetic-window");
        await incomplete.RunAsync((_, _) => Task.FromResult(Screen(0) with { AutomationComplete = false }),
            (_, _, _) => throw new InvalidOperationException("Incomplete evidence reached inference."),
            NoOp, true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(incomplete.Status == "blocked" && incomplete.ActionsTaken == 0);

        foreach (string action in new[] { "set_value", "scroll", "toggle", "select", "expand", "collapse" })
        foreach (int effectRead in new[] { 1, ScreenTaskSession.ObservationAttempts, -1 })
        {
            bool applied = false;
            int semanticCalls = 0, semanticCaptures = 0;
            var semantic = new ScreenTaskSession("Synthetic semantic step", "synthetic-window");
            Task<Observation> SemanticCapture(bool _, CancellationToken token)
            {
                semanticCaptures++;
                applied = semanticCalls > 0 && effectRead > 0 && semanticCaptures - 1 >= effectRead;
                var element = new ElementInfo("edit", "Synthetic field", [0.1, 0.2, 0.3, 0.1],
                    TargetId: "uia-semantic", Action: applied
                        ? action switch { "expand" => "collapse", "collapse" => "expand", _ => action } : action,
                    ToggleState: action == "toggle" ? applied ? "on" : "off" : null,
                    IsReadOnly: action == "set_value" ? false : null,
                    ValueHash: action == "set_value" ? AutomationEvidence.ValueDigest(applied ? "new" : "") : null,
                    ValueLength: action == "set_value" ? applied ? 3 : 0 : null,
                    IsSelected: action == "select" ? applied : null,
                    ScrollDirections: action == "scroll" ? ["down"] : null,
                    VerticalScrollPercent: action == "scroll" ? applied ? 10 : 0 : null);
                // Unrelated notification changes settle before the control effect, if any.
                return Task.FromResult(Screen(semanticCalls, element));
            }
            Task<Guidance> SemanticGuide(Observation observation, TaskProgress progress, CancellationToken token) =>
                Task.FromResult(Response(observation, progress,
                    applied ? "completion_candidate" : "next_step",
                    action == "set_value" ? "new" : null, action == "scroll" ? "down" : null));
            Task<DesktopActionResult> SemanticExecute(Observation _, TargetInfo target, CancellationToken token)
            {
                semanticCalls++;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }
            await semantic.RunAsync(SemanticCapture, SemanticGuide, SemanticExecute,
                true, () => { }, CancellationToken.None, NoDelay);
            IntegrationTests.Require(semanticCalls == 1 && semantic.ActionsTaken == 1
                && semanticCaptures == 1 + (effectRead > 0 ? effectRead : ScreenTaskSession.ObservationAttempts)
                && semantic.History.Single().AfterObservationId is not null
                && semantic.History.Single().Outcome == (effectRead > 0 ? "effect_observed" : "unknown")
                && semantic.Status == (effectRead > 0 ? "review_required" : "unknown"));
            if (effectRead < 0)
            {
                IntegrationTests.Require(!semantic.CanContinue && !semantic.AwaitingActionEvidence);
                bool replayRejected = false;
                try
                {
                    await semantic.RunAsync(SemanticCapture, SemanticGuide, SemanticExecute,
                        true, () => { }, CancellationToken.None, NoDelay);
                }
                catch (InvalidOperationException) { replayRejected = true; }
                IntegrationTests.Require(replayRejected && semanticCalls == 1
                    && semanticCaptures == ScreenTaskSession.ObservationAttempts + 1
                    && semantic.Status == "unknown" && semantic.History.Count == 1);
            }
        }

        foreach (Exception error in new Exception[]
        {
            new UnauthorizedAccessException("Synthetic external capture text must not be retained."),
            new System.Runtime.InteropServices.COMException(
                "Synthetic external capture text must not be retained.", unchecked((int)0x80070005))
        })
        foreach (bool afterInvocation in new[] { false, true })
        {
            var denied = new ScreenTaskSession("Capture access denied", "synthetic-window");
            string deniedId = denied.Id;
            int deniedCalls = 0, deniedCaptures = 0, deniedGuidance = 0;
            bool deny = true;
            var reportedStates = new List<string>();
            Task<Observation> DeniedCapture(bool _, CancellationToken token)
            {
                deniedCaptures++;
                return deny && (!afterInvocation || deniedCalls > 0)
                    ? Task.FromException<Observation>(error) : Task.FromResult(Screen(deniedCalls));
            }
            Task<Guidance> DeniedGuide(Observation observation, TaskProgress progress, CancellationToken token)
            {
                deniedGuidance++;
                return Task.FromResult(Response(observation, progress,
                    deniedCalls == 0 ? "next_step" : "completion_candidate"));
            }
            Task<DesktopActionResult> DeniedExecute(Observation _, TargetInfo target, CancellationToken token)
            {
                deniedCalls++;
                return Task.FromResult(new DesktopActionResult(true, true, "Returned."));
            }
            var deniedRun = denied.RunAsync(DeniedCapture, DeniedGuide, DeniedExecute,
                true, () => reportedStates.Add(denied.Status), CancellationToken.None, NoDelay);
            await deniedRun;
            string expectedStatus = afterInvocation ? "unknown" : "failed";
            IntegrationTests.Require(deniedRun.IsCompletedSuccessfully && !denied.Running
                && denied.Status == expectedStatus && reportedStates[^1] == expectedStatus
                && denied.Id == deniedId && !denied.AwaitingActionEvidence
                && deniedCalls == (afterInvocation ? 1 : 0) && denied.ActionsTaken == deniedCalls
                && deniedCaptures == (afterInvocation ? 2 : 1)
                && !denied.Detail.Contains(error.Message, StringComparison.Ordinal));
            deny = false;
            if (afterInvocation)
            {
                IntegrationTests.Require(!denied.CanContinue && denied.History.Single().Outcome == "unknown");
                bool replayRejected = false;
                try
                {
                    await denied.RunAsync(DeniedCapture, DeniedGuide, DeniedExecute,
                        true, () => { }, CancellationToken.None, NoDelay);
                }
                catch (InvalidOperationException) { replayRejected = true; }
                IntegrationTests.Require(replayRejected && deniedCalls == 1 && deniedCaptures == 2
                    && deniedGuidance == 1 && denied.Status == "unknown" && denied.History.Count == 1);
            }
            else
            {
                IntegrationTests.Require(denied.CanContinue && denied.History.Count == 0 && deniedGuidance == 0
                    && denied.Detail.Contains("Capture access was denied", StringComparison.Ordinal));
                await denied.RunAsync(DeniedCapture, DeniedGuide, DeniedExecute,
                    true, () => { }, CancellationToken.None, NoDelay);
                IntegrationTests.Require(denied.Status == "review_required" && denied.Id == deniedId
                    && deniedCalls == 1 && denied.ActionsTaken == 1 && deniedCaptures == 4
                    && denied.History.Single().Step == 1 && denied.History.Single().Outcome == "screen_changed");
            }
        }

        var malformed = new ScreenTaskSession("Echo mismatch", "synthetic-window");
        await malformed.RunAsync(Unchanged,
            (observation, progress, _) => Task.FromResult(Response(observation, progress) with { Step = progress.Step + 1 }),
            (_, _, _) => throw new InvalidOperationException("Mismatched response executed."),
            true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(malformed.Status == "failed" && malformed.ActionsTaken == 0);

        var late = new TaskCompletionSource<Guidance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<(Observation Observation, TaskProgress Progress)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var superseded = new ScreenTaskSession("Old task", "synthetic-window");
        string shown = "";
        var oldRun = superseded.RunAsync(Unchanged, (observation, progress, _) =>
        {
            entered.SetResult((observation, progress));
            return late.Task; // Deliberately ignores cancellation to exercise the generation guard.
        }, NoOp, true, () => shown = "old", CancellationToken.None, NoDelay);
        var oldRequest = await entered.Task;
        superseded.Stop();
        var newer = new ScreenTaskSession("New task", "synthetic-window");
        await newer.RunAsync(Unchanged,
            (observation, progress, _) => Task.FromResult(Response(observation, progress, "blocked")),
            NoOp, true, () => shown = "new", CancellationToken.None, NoDelay);
        late.SetResult(Response(oldRequest.Observation, oldRequest.Progress));
        await oldRun;
        IntegrationTests.Require(shown == "new" && superseded.Status == "cancelled"
            && superseded.History.Count == 0 && newer.Status == "blocked");

        var unknown = new ScreenTaskSession("Unknown action", "synthetic-window");
        await unknown.RunAsync(Unchanged, Next,
            (_, _, _) => Task.FromResult(new DesktopActionResult(true, false, "Unknown native outcome.")),
            true, () => { }, CancellationToken.None, NoDelay);
        IntegrationTests.Require(unknown.Status == "unknown" && !unknown.CanContinue
            && unknown.History.Single().Outcome == "unknown");

        var verifyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelledVerification = new CancellationTokenSource();
        int verificationCaptures = 0;
        var interrupted = new ScreenTaskSession("Cancel during verification", "synthetic-window");
        var interruptedRun = interrupted.RunAsync(async (_, token) =>
        {
            if (++verificationCaptures > 1)
            {
                verifyEntered.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            return Screen(0);
        }, Next, NoOp, true, () => { }, cancelledVerification.Token, NoDelay);
        await verifyEntered.Task;
        cancelledVerification.Cancel();
        await interruptedRun;
        IntegrationTests.Require(interrupted.Status == "unknown" && !interrupted.CanContinue
            && interrupted.History.Single().Outcome == "unknown");
    }

    public static async Task RunNativeLifecycleAsync()
    {
        IntegrationTests.Require(DesktopAction.CanPresentInForeground(1, 1, 2, 3)
            && DesktopAction.CanPresentInForeground(2, 1, 2, 3)
            && DesktopAction.CanPresentInForeground(3, 1, 2, 3)
            && !DesktopAction.CanPresentInForeground(4, 1, 2, 3)
            && !DesktopAction.CanPresentInForeground(0, 1, 2, 3)
            && !DesktopAction.CanPresentInForeground(2, 0, 2, 3));
        foreach (string presentation in new[] { "shown", "unavailable", "cancelled", "failed" })
        {
            var order = new List<string>();
            using var cancelled = new CancellationTokenSource();
            try
            {
                var result = await DesktopAction.RunVisible(token =>
                {
                    order.Add("present");
                    if (presentation == "failed") throw new InvalidOperationException("Synthetic presentation failure.");
                    if (presentation == "cancelled") cancelled.Cancel();
                    return Task.FromResult(presentation != "unavailable");
                }, token =>
                {
                    token.ThrowIfCancellationRequested();
                    order.Add("invoke");
                    return Task.FromResult(new DesktopActionResult(true, true, "Synthetic visible action."));
                }, () => order.Add("clear"), cancelled.Token);
                IntegrationTests.Require(result.Invoked == (presentation == "shown"));
            }
            catch (OperationCanceledException) when (presentation == "cancelled") { }
            catch (InvalidOperationException) when (presentation == "failed") { }
            IntegrationTests.Require(order.SequenceEqual(presentation == "shown"
                ? new[] { "present", "invoke", "clear" } : new[] { "present", "clear" }));
        }
        foreach (bool startInvocation in new[] { false, true })
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int lateInvocations = 0;
            try
            {
                var running = DesktopAction.RunBounded((token, begin) =>
                {
                    if (startInvocation) IntegrationTests.Require(begin());
                    entered.SetResult();
                    release.Wait(); // Fake hung native provider; finally below always releases it.
                    if (!startInvocation && begin()) Interlocked.Increment(ref lateInvocations);
                    return new(startInvocation, true, "Late native return.");
                }, CancellationToken.None, TimeSpan.FromMilliseconds(60));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
                IntegrationTests.Require(DesktopAction.IsBusy && result.Invoked == startInvocation
                    && result.OutcomeKnown != startInvocation);
                bool secondInvoked = false;
                var second = await DesktopAction.RunBounded((_, _) =>
                { secondInvoked = true; return new(true, true, "Must not execute."); }, CancellationToken.None);
                IntegrationTests.Require(!second.Invoked && !secondInvoked);
                bool captureBlocked = false;
                try { await CaptureService.Capture(new WindowChoice(0, 0, "Synthetic"), CancellationToken.None); }
                catch (InvalidOperationException) { captureBlocked = true; }
                IntegrationTests.Require(captureBlocked);
            }
            finally
            {
                release.Set();
                await DesktopAction.WhenIdle.WaitAsync(TimeSpan.FromSeconds(2));
            }
            IntegrationTests.Require(!DesktopAction.IsBusy && lateInvocations == 0);
        }
    }

    private sealed class SlowGuidanceHandler : HttpMessageHandler
    {
        internal bool Cancelled;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath == "/v1/sessions")
                return new(HttpStatusCode.OK)
                { Content = JsonContent.Create(new SessionInfo("synthetic-session", DateTimeOffset.UtcNow.AddHours(1))) };
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("The client freshness deadline did not cancel guidance.");
        }
    }

    public static async Task RunClientDeadlineAsync()
    {
        var handler = new SlowGuidanceHandler();
        using var client = new ApiClient(handler, "synthetic-token");
        var old = Screen(0) with { CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-53.7) };
        bool rejected = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { await client.Guide(old, "Synthetic request", CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        IntegrationTests.Require(rejected && handler.Cancelled && handler.Calls == 2
            && clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    public static void Run()
    {
        if (!MainWindow.IsPromptSubmitKey(Key.Enter, ModifierKeys.None)
            || MainWindow.IsPromptSubmitKey(Key.Enter, ModifierKeys.Shift)
            || MainWindow.IsPromptSubmitKey(Key.Space, ModifierKeys.None)
            || MainWindow.CanAutoCapture(false, new WindowChoice((nint)1, 1, "Calculator"))
            || MainWindow.CanAutoCapture(true, null)
            || !MainWindow.CanAutoCapture(true, new WindowChoice((nint)1, 1, "Calculator")))
            throw new InvalidOperationException("Prompt submission keyboard contract failed.");
        string? previous = Environment.GetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS");
        try
        {
            var companionPrompt = new CompanionPromptWindow(_ => Task.CompletedTask, () => { });
            try
            {
                if (!companionPrompt.HasReadablePrompt)
                    throw new InvalidOperationException("Companion prompt text must remain readable.");
            }
            finally { companionPrompt.Close(); }
            Environment.SetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS", "0");
            var window = new MainWindow();
            try { window.CheckPromptComposer(); }
            finally { window.Close(); }
            Environment.SetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS", "1");
            var developer = new MainWindow();
            try { developer.CheckDeveloperShell(); }
            finally { developer.Close(); }
        }
        finally { Environment.SetEnvironmentVariable("MSGUIDE_DEVELOPER_TOOLS", previous); }
    }
}

public partial class MainWindow
{
    internal void CheckPromptComposer()
    {
        static void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Prompt composer regression failed.");
        }
        static bool Within(DependencyObject item, DependencyObject ancestor)
        {
            for (DependencyObject? current = item; current is not null; current = LogicalTreeHelper.GetParent(current))
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }
        loaded = true;
        try
        {
            var peer = new ButtonAutomationPeer(AskPromptButton);
            Check(peer.GetName() == "Ask MSGuide" && AskPromptButton.Focusable
                && !AskPromptButton.IsDefault && PromptBox.AcceptsReturn);
            Check(PromptBox.Text.Length == 0 && !AskPromptButton.IsEnabled
                && ProductSubtitle.Text == "Your guide to getting things done at Microsoft."
                && DeveloperToolsExpander.Visibility == Visibility.Collapsed
                && SettingsExpander.Visibility == Visibility.Visible
                && ScreenContextExpander.Visibility == Visibility.Collapsed
                && CameraRecoveryCard.Visibility == Visibility.Collapsed
                && InteractionModePanel.Visibility == Visibility.Visible
                && Within(PromptBox, WorkspaceScroll)
                && Within(CameraRecoveryCard, WorkspaceScroll)
                && Within(SettingsExpander, WorkspaceScroll));
            Check(CameraStatePanel.Visibility == Visibility.Collapsed
                && CameraProgressPanel.Visibility == Visibility.Collapsed
                && CameraIdleHint.Visibility == Visibility.Visible);
            PromptBox.Text = "";
            Check(!AskPromptButton.IsEnabled);
            PromptBox.Text = "Help me fix my camera in Teams";
            Check(AskPromptButton.IsEnabled && cameraRecovery.State == CameraRecoveryState.Idle
                && cameraRecovery.Target is null && !cameraRecoveryBusy);
            PromptBox.Text = "Explain a different application";
            AskPromptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(PromptFeedbackText.Visibility == Visibility.Visible
                && PromptFeedbackText.Text.Contains("Choose the app", StringComparison.Ordinal)
                && ScreenContextExpander.Visibility == Visibility.Visible
                && ScreenContextExpander.IsExpanded
                && cameraRecovery.State == CameraRecoveryState.Idle);
            Check(ScreenContextExpander.Visibility == Visibility.Visible && ScreenContextExpander.IsExpanded
                && ReviewPanel.Visibility == Visibility.Collapsed && snapshot is null
                && ConsentBox.IsChecked != true && ShareImage.IsChecked != true
                && !SendButton.IsEnabled && !speech.Listening);
            CloseScreenContext_Click(this, new RoutedEventArgs());
            Check(ScreenContextExpander.Visibility == Visibility.Collapsed && snapshot is null);
            VoiceSettings_Click(this, new RoutedEventArgs());
            Check(SettingsExpander.IsExpanded && !speech.Listening && !speech.Finishing);
            SettingsExpander.IsExpanded = false;
            cameraRecoverySensing = new FixtureCameraRecoverySensing();
            cameraRecovery.Start();
            UpdateCameraRecoveryUi();
            Check(!CameraGuideMode.IsEnabled && !CameraControlMode.IsEnabled
                && CameraSwitchModeButton.Visibility == Visibility.Visible
                && CameraRecoveryCard.Visibility == Visibility.Visible);
            CameraSwitchModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(SelectedCameraMode == CameraRecoveryInteractionMode.Control
                && cameraRecovery.CanStart && cameraRecovery.Target is null
                && !cameraRecoveryBusy && CameraGuideMode.IsEnabled && CameraControlMode.IsEnabled);
            cameraRecovery.Start();
            PromptBox.Text = "A new request while an old task was active";
            Check(cameraRecovery.CanStart && CameraRecoveryCard.Visibility == Visibility.Collapsed
                && cameraRecovery.Target is null && CameraGuideMode.IsEnabled && CameraControlMode.IsEnabled);
            PromptBox.Text = "";
            ApplyTranscript("Check my Teams camera");
            Check(PromptBox.Text == "Check my Teams camera" && AskPromptButton.IsEnabled
                && cameraRecovery.CanStart && !speech.Listening);
            ApplyTranscript(new string('a', 4000));
            Check(PromptBox.Text.Length == PromptBox.MaxLength
                && PromptFeedbackText.Text.Contains("length limit", StringComparison.Ordinal));
        }
        finally { loaded = false; }
    }

    internal void CheckDeveloperShell()
    {
        if (DeveloperToolsExpander.Visibility != Visibility.Visible
            || ScreenContextExpander.Visibility != Visibility.Visible
            || !developerToolsEnabled
            || ClickyDemoButton.Visibility != Visibility.Visible
            || ClickyDemoButton.Content?.ToString() != "Start demo · capture locally"
            || ConsentBox.IsChecked == true || ShareImage.IsChecked == true)
            throw new InvalidOperationException("Developer tooling must be explicit without granting capture/share consent.");
        ConfigureClickyDemoRequest();
        if (SelectedCameraMode != CameraRecoveryInteractionMode.Control
            || PromptBox.Text != ClickyDemoQuestion)
            throw new InvalidOperationException("Clicky demo setup regression failed.");
    }
}
