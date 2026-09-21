using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class BrowserTaskTests
{
    internal static async Task Run(nint handle, string address, List<string> checks, Action<string> stage,
        bool execute)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || uri.Host != "127.0.0.1" || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/browser-task.html" || uri.Fragment.Length != 0
            || !System.Text.RegularExpressions.Regex.IsMatch(uri.Query, @"^\?run=[a-f0-9]{32}$"))
            throw new InvalidOperationException("Only the local synthetic browser fixture is accepted.");
        string run = uri.Query["?run=".Length..];
        Native.GetWindowThreadProcessId(handle, out var pid);
        var browser = new WindowChoice(handle, pid, Native.Title(handle), Native.WindowClass(handle));
        if (!browser.Matches() || !browser.Title.Contains($"MSGuide browser acceptance {run}", StringComparison.Ordinal)
            || !AutomationEvidence.IsSupportedBrowser(browser))
            throw new InvalidOperationException("The selected window is not the owned synthetic browser fixture.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        stage("browser-resource-before-upload");
        var page = await Task.Run(() => AutomationEvidence.ReadBrowserScope(browser, deadline.Token),
            deadline.Token);
        if (page is null) throw new InvalidOperationException("Synthetic browser resource identity was not established.");
        string resource = page.ResourceId;
        IntegrationTests.Require(page.AddressKey == AutomationEvidence.BrowserAddressKey(address)
            && resource == AutomationEvidence.BrowserResourceId(browser, address, page.DocumentId));
        checks.Add("owned-loopback-browser-resource-verified-before-upload");
        using (var initial = await CaptureService.Capture(browser, deadline.Token, includeImage: false))
            IntegrationTests.Require(initial.AutomationComplete && initial.ResourceId == resource
                && initial.Elements.Any(element => element.Label == "Ready for step one")
                && initial.Elements.Count(element => element.Role == "button" && element.IsEnabled
                    && element.Label.StartsWith("Complete step ", StringComparison.Ordinal)) == 3);

        var window = new MainWindow { ShowActivated = execute };
        try
        {
            if (execute)
            {
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ContentRendered += (_, _) => rendered.TrySetResult();
                window.Show();
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            stage("browser-production-task");
            await window.RunBrowserAcceptance(browser, resource, deadline.Token, checks, stage, execute);
            if (!execute) { stage("readonly-complete-no-actions-executed"); return; }
            checks.Add("live-model-plan-native-browser-three-actions-visible-and-observed");
            stage("browser-independent-final-state");
            var finalPage = await Task.Run(() => AutomationEvidence.ReadBrowserScope(browser, deadline.Token),
                deadline.Token);
            using var final = await CaptureService.Capture(browser, deadline.Token, includeImage: false);
            IntegrationTests.Require(finalPage is not null
                && finalPage.AddressKey == AutomationEvidence.BrowserAddressKey(address + "#step-three")
                && final.AutomationComplete && final.ResourceId == finalPage.ResourceId && final.ResourceId != resource
                && final.Elements.Any(element => element.Label == "All three steps finished")
                && final.Elements.Count(element => element.Label.StartsWith("Complete step ", StringComparison.Ordinal)
                    && element.Role == "button" && !element.IsEnabled) == 3);
            checks.Add("fresh-browser-evidence-confirms-all-three-steps");
            checks.Add("real-browser-page-changes-continue-without-approval-or-old-page-target-reuse");
            stage("complete");
        }
        finally { window.Close(); }
    }
}

public partial class MainWindow
{
    internal async Task RunBrowserAcceptance(WindowChoice browser, string resource, CancellationToken token,
        List<string> checks, Action<string> stage, bool execute)
    {
        api ??= new ApiClient();
        var health = await api.Health(token);
        IntegrationTests.Require(health is { Status: "ok", Mode: "model" });
        sessionScreenContextApproved = true;
        sessionAutomationApproved = true;
        CameraControlMode.IsChecked = true;
        PromptBox.Text = "On this local synthetic page, click Complete step one, then Complete step two, "
            + "then Complete step three, in that order. Do not click any browser toolbar controls. "
            + "Stop when All three steps finished is visible.";
        refreshing = true;
        try
        {
            WindowPicker.ItemsSource = new[] { browser };
            WindowPicker.SelectedItem = browser;
        }
        finally { refreshing = false; }
        if (execute) companion.Start();
        if (!execute)
        {
            stage("browser-readonly-plan");
            using var evidence = await CaptureService.Capture(browser, token, includeImage: false);
            IntegrationTests.Require(evidence.ResourceId == resource && evidence.AutomationComplete);
            var task = new ScreenTaskSession(PromptBox.Text, browser.Id);
            var response = await api.Guide(evidence.Observation(false), task.Prompt, token, task.Progress);
            if (response.Plan is not { Steps.Length: 3 } plan)
                throw new InvalidOperationException("The synthetic model did not return the three requested steps.");
            IntegrationTests.Require(Safety.ValidPlan(plan, evidence.Observation(false)));
            foreach (var step in plan.Steps)
            {
                var target = Safety.BindPlanAction(step, evidence.Observation(false));
                if (target is null) throw new InvalidOperationException("A synthetic plan target could not be bound.");
                var rebound = await Task.Run(() => AutomationEvidence.FindUniqueTarget(browser, evidence.Rect,
                    target.TargetId!, target.Label, target.AutomationId, token, resource), token);
                IntegrationTests.Require(rebound is not null);
            }
            checks.Add("live-model-three-step-plan-and-real-targets-verified-without-input");
            return;
        }
        if (Native.GetForegroundWindow() != browser.Handle && Native.GetForegroundWindow() != Handle)
        {
            stage("browser-awaiting-user-foreground");
            await NativeTests.RequireForeground(this);
            token.ThrowIfCancellationRequested();
        }
        stage("browser-plan-and-visible-actions");
        using var registration = token.Register(() => Dispatcher.BeginInvoke((Action)(() => CancelWork())));
        await CaptureAndGuideAsync();
        token.ThrowIfCancellationRequested();
        IntegrationTests.Require(screenTask is { ActionsTaken: 3, Status: "review_required" }
            && screenTask.Plan is { ResourceId: not null } && screenTask.Plan.ResourceId != resource
            && screenTask.PlanCursor == screenTask.Plan.Steps.Length
            && screenTask.History.Count == 3
            && screenTask.History.All(step => step.AfterObservationId is not null
                && step.Outcome is "screen_changed" or "effect_observed"));
    }
}
