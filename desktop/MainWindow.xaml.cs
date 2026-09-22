using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

public partial class MainWindow : Window
{
    private const string ClickyDemoQuestion = "Help me find the build error";
    private ApiClient? api;
    private readonly OverlayWindow overlay = new();
    private readonly CompanionShell companion;
    private readonly SpeechService speech;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? operation;
    private Snapshot? snapshot;
    private DemoWindow? demo;
    private long generation;
    private long pausedGeneration = -1;
    private string? pausedBanner;
    private bool loaded, refreshing, closing, hotkeyRegistered, sending;
    private DateTimeOffset microphoneStarted;
    private Highlight? highlight;
    private WindowChoice? invokedWindow;
    private ScreenTaskSession? screenTask;
    private nint Handle => new WindowInteropHelper(this).Handle;
    private sealed record Highlight(WindowChoice Window, Native.RECT Rect, DateTimeOffset CapturedAt,
        TargetInfo Target, string? ResourceId = null)
    {
        public Highlight(WindowChoice window, Native.RECT rect, DateTimeOffset capturedAt, double[] box)
            : this(window, rect, capturedAt, new TargetInfo("Task target", box, 1)) { }

        public bool HasShown { get; set; }
    }

    public MainWindow() : this(new SpeechService()) { }

    internal MainWindow(SpeechService speech)
    {
        this.speech = speech;
        InitializeComponent();
        companion = new CompanionShell(SubmitCompanionPromptAsync, ShowDetailsNearCursor,
            ContinueScreenTaskAsync, () => StopScreenTask_Click(this, new RoutedEventArgs()),
            () => CameraSwitchMode_Click(this, new RoutedEventArgs()),
            text => PromptBox.Text = text);
        MicrophonePicker.ItemsSource = new[] { MicrophoneChoice.Default };
        MicrophonePicker.SelectedItem = MicrophoneChoice.Default;
        ConfigureScreenShareStatus();
        ConfigureProductShell();
        InitializeCameraRecovery();
        UpdateCompactTaskUi();
        speech.Transcribed += ApplyTranscript;
        speech.StatusChanged += status =>
        {
            if (closing) return;
            SpeechText.Text = status;
            AutomationProperties.SetHelpText(SpeechText, status);
            if (!speech.Listening && !speech.Finishing) replaceVoiceDraft = false;
            UpdatePromptSubmissionUi();
            UpdatePauseStatus();
        };
        speech.PreviewChanged += text =>
        {
            if (closing) return;
            SpeechPreviewText.Text = string.IsNullOrWhiteSpace(text) ? "" : $"Hearing: {text}";
            SpeechPreviewText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        };
        speech.AudioLevelChanged += level => { if (!closing) SpeechInputLevel.Value = level; };
        SourceInitialized += InitializeNative;
        Loaded += async (_, _) =>
        {
            loaded = true;
            RefreshWindows();
            RefreshMicrophones();
            timer.Tick += Timer_Tick;
            timer.Start();
            PromptBox.Focus();
            await CheckHealth();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Pause(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        Closed += (_, _) => Cleanup();
    }

    private void InitializeNative(object? sender, EventArgs e)
    {
        if (!Native.SetWindowDisplayAffinity(Handle, Native.MSGuideDisplayAffinity))
        {
            ScreenShareText.Text = "SHARE STATUS UNKNOWN";
            ScreenSharePill.SetResourceReference(Border.BackgroundProperty, "DangerSoftBrush");
            ScreenShareText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            ScreenSharePill.ToolTip = "Windows did not confirm the requested screen-sharing protection.";
        }
        HwndSource.FromHwnd(Handle)?.AddHook(WindowHook);
        string configured = Environment.GetEnvironmentVariable("MSGUIDE_HOTKEY") ?? "Ctrl+Alt+M";
        try
        {
            var parts = configured.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            uint modifiers = 0x4000; // MOD_NOREPEAT
            foreach (string part in parts[..^1])
                modifiers |= part.ToLowerInvariant() switch
                { "ctrl" or "control" => 2u, "alt" => 1u, "shift" => 4u, "win" => 8u, _ => throw new FormatException() };
            if (parts.Length < 2 || !Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None) throw new FormatException();
            hotkeyRegistered = Native.RegisterHotKey(Handle, 0x4D47, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
            HotkeyText.Text = hotkeyRegistered ? $"{configured} · show near cursor   |   Esc · dismiss"
                : "Hotkey unavailable (already registered). Use the taskbar to reopen MSGuide.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        { HotkeyText.Text = "Invalid MSGUIDE_HOTKEY. Use Ctrl+Alt+M format; taskbar invocation is still available."; }
    }

    private void ConfigureScreenShareStatus()
    {
        bool shareable = Native.ShareableDemo;
        ScreenShareText.Text = shareable ? "VISIBLE IN SHARE" : "NOT SHARED";
        ScreenSharePill.SetResourceReference(Border.BackgroundProperty,
            shareable ? "WarningSoftBrush" : "SurfaceRaisedBrush");
        ScreenShareText.SetResourceReference(TextBlock.ForegroundProperty,
            shareable ? "WarningBrush" : "MutedTextBrush");
        ScreenSharePill.ToolTip = shareable
            ? "MSGuide and its guidance overlay can appear in full-screen sharing."
            : "Privacy default: MSGuide and its guidance overlay are excluded from screen capture.";
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x312 && wParam.ToInt32() == 0x4D47)
        { InvokeNearCursor(); handled = true; }
        return 0;
    }

    private void InvokeNearCursor()
    {
        if (screenTask is null || !cameraRecovery.CanStart || demoTask is not null || notepadTask is not null)
            CancelWork();
        speech.Stop();
        nint previousForeground = Native.GetForegroundWindow();
        if (previousForeground != 0 && previousForeground != Handle && previousForeground != overlay.Handle
            && previousForeground != companion.PromptHandle
            && Native.NormalWindow(previousForeground))
        {
            Native.GetWindowThreadProcessId(previousForeground, out var processId);
            invokedWindow = new(previousForeground, processId, Native.Title(previousForeground),
                Native.WindowClass(previousForeground));
        }
        Hide();
        UpdateCompactTaskUi();
        companion.Invoke(PromptBox.Text);
        StatusText.Text = sessionScreenContextApproved
            ? "Ready · ask about the app you invoked MSGuide from."
            : "Ready · choose a window and capture explicitly.";
    }

    public void StartCompanionMode()
    {
        companion.Start();
        if (hotkeyRegistered) Hide();
        else
        {
            ShowDetailsNearCursor();
            StatusText.Text = HotkeyText.Text;
        }
    }

    private void ShowDetailsNearCursor()
    {
        WindowState = WindowState.Normal;
        Show();
        if (CompanionPlacement.TryCurrent(620, 800, out var rect))
            Native.SetWindowPos(Handle, 0, rect.Left, rect.Top, rect.Width, rect.Height, 0x14);
        Activate();
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    private async Task SubmitCompanionPromptAsync(string text)
    {
        PromptBox.Text = text;
        companion.ShowProcessing();
        await SubmitPromptAsync();
    }

    private void RefreshWindows()
    {
        var selected = WindowPicker.SelectedItem as WindowChoice;
        refreshing = true;
        var windows = WindowChoice.List(Handle, overlay.Handle);
        WindowPicker.ItemsSource = windows;
        WindowPicker.SelectedItem = windows.FirstOrDefault(w => w.Id == invokedWindow?.Id)
            ?? windows.FirstOrDefault(w => w.Id == selected?.Id)
            ?? windows.FirstOrDefault(w => w.Title == "MSGuide Demo");
        invokedWindow = null;
        refreshing = false;
        RefreshCameraWindows(windows);
    }

    private CancellationToken BeginWork(bool preserveScreenTask = false)
    {
        CancelWork(preserveScreenTask: preserveScreenTask);
        operation = new CancellationTokenSource();
        return operation.Token;
    }

    private void ClearSnapshot()
    {
        // Clearing our own approval controls must not look like user revocation.
        sending = false;
        PreviewImage.Source = null;
        FullPreviewImage.Source = null;
        MetadataText.Clear();
        CaptureDetails.Text = "";
        snapshot?.Dispose(); snapshot = null;
        ConsentBox.IsChecked = false; ShareImage.IsChecked = false;
        ReviewPanel.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;
    }

    private void CancelWork(bool cancelCameraRecovery = true, bool preserveScreenTask = false)
    {
        pausedBanner = null;
        if (!preserveScreenTask && screenTask is { Running: true } task)
        {
            task.Stop();
            if (!closing)
            {
                UpdateScreenTaskUi();
                companion.FinishTask(task);
            }
        }
        if (cancelCameraRecovery) CancelCameraRecoveryForSupersession();
        StopDemoTask();
        generation++;
        operation?.Cancel(); operation?.Dispose(); operation = null;
        sending = false;
        ClearSnapshot();
        ClearHighlight();
        speech.StopSpeaking();
        AnswerText.Text = "";
        CitationsPanel.Children.Clear();
        SpeakButton.IsEnabled = false;
        CaptureButton.IsEnabled = NextButton.IsEnabled = true;
    }

    private bool CurrentWork(long mine, CancellationToken ct) => !closing && mine == generation && !ct.IsCancellationRequested;

    private void Pause()
    {
        CancelWork();
        ForgetScreenTask();
        ResetScreenContextUi();
        speech.Stop();
        PromptBox.Clear();
        DraftBox.Clear();
        TaskPlanText.Text = "Paused. Prepare a new plan to continue.";
        AnswerText.Text = "Paused. Capture, text, and response cleared. Already transmitted data cannot be recalled.";
        CitationsPanel.Children.Clear();
        SpeakButton.IsEnabled = false;
        ModeText.Text = "PAUSED · no execution authority";
        pausedGeneration = generation;
        pausedBanner = StatusText.Text;
        UpdatePauseStatus();
    }

    private void UpdatePauseStatus()
    {
        if (pausedBanner is null || pausedGeneration != generation || StatusText.Text != pausedBanner) return;
        pausedBanner = speech.InputStopUnconfirmed
            ? "Paused · microphone status unknown; recording remains blocked until closure is confirmed."
            : speech.Busy
                ? "Paused · audio shutdown pending; recording remains blocked."
                : "Paused · microphone off · no execution authority.";
        StatusText.Text = pausedBanner;
    }

    private void Dismiss()
    {
        Pause();
        if (hotkeyRegistered) Hide();
        else WindowState = WindowState.Minimized;
    }

    private async Task CheckHealth()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        long mine = generation;
        CheckServiceButton.IsEnabled = false;
        SetConnectionStatus("Checking the local service...");
        try
        {
            api ??= new ApiClient();
            var health = await api.Health(ct);
            if (closing) return;
            if (health.Status != "ok" || health.Version != "0.2.0" || health.Mode is not ("demo" or "model"))
                throw new InvalidOperationException("Incompatible service. Expected healthy API v0.2.0 with demo/model mode.");
            ModeText.Text = ModeLabel(health.Mode);
            SetConnectionStatus($"Connected · {health.Mode} · v{health.Version}");
            if (mine == generation && cameraRecovery.CanStart && snapshot is null && !sending)
                StatusText.Text = "Ready. Type or speak a question to get started.";
        }
        catch (OperationCanceledException)
        {
            if (!closing) SetConnectionStatus("Connection check cancelled or timed out.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException
            or JsonException or ArgumentException)
        {
            if (!closing) SetConnectionStatus("The guidance service is unavailable or incompatible. Local camera checks and dictation remain available.");
        }
        finally { if (!closing) CheckServiceButton.IsEnabled = true; }
    }

    private static string ModeLabel(string mode) => mode == "demo"
        ? "DEMO · deterministic rules / sample guidance · not a model"
        : "MODEL · backend-configured provider · verify its data policy";

    private async void Capture_Click(object sender, RoutedEventArgs e) => _ = await CaptureAsync();

    private async Task<bool> CaptureAsync()
    {
        if (WindowPicker.SelectedItem is not WindowChoice window)
        { StatusText.Text = "Choose the visible window you want help with before capturing it."; return false; }
        var ct = BeginWork();
        long mine = generation;
        bool captured = false;
        speech.Stop();
        CaptureButton.IsEnabled = NextButton.IsEnabled = false;
        StatusText.Text = $"Capturing only “{window.Title}” locally… nothing is being uploaded.";
        try
        {
            var result = await CaptureService.Capture(window, ct);
            if (!CurrentWork(mine, ct)) { result.Dispose(); return false; }
            snapshot = result;
            PreviewImage.Source = FullPreviewImage.Source = result.Preview;
            CaptureDetails.Text = $"{window.Title} · {result.Preview!.PixelWidth} × {result.Preview.PixelHeight} · {result.Png.Length / 1024} KB PNG\n{result.Note}";
            MetadataText.Text = result.Text + "\n\nELEMENTS (normalized x, y, width, height):\n"
                + JsonSerializer.Serialize(result.Elements, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            ReviewPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Review ready · stored in memory · nothing sent. Approve or discard.";
            captured = true;
        }
        catch (OperationCanceledException) { if (mine == generation) StatusText.Text = "Capture cancelled. Nothing sent."; }
        catch (Exception ex) { if (mine == generation) ShowError(ex); }
        finally { if (mine == generation) CaptureButton.IsEnabled = NextButton.IsEnabled = true; }
        return captured;
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (sending || snapshot is null || ConsentBox.IsChecked != true) return;
        if (!snapshot.Valid()) { CancelWork(); StatusText.Text = "Snapshot expired or window changed. Capture and review again."; return; }
        string prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0) { StatusText.Text = "Type or dictate a question before sending."; return; }
        var current = snapshot;
        var ct = operation?.Token ?? CancellationToken.None;
        long mine = generation;
        sending = true;
        SendButton.IsEnabled = false;
        speech.Stop();
        bool shareImage = ShareImage.IsChecked == true;
        StatusText.Text = shareImage ? "Sending approved text, boxes, and image…" : "Sending approved text and boxes only · image not shared…";
        try
        {
            api ??= new ApiClient();
            var observation = current.Observation(shareImage);
            var response = await api.Guide(observation, prompt, ct);
            if (!CurrentWork(mine, ct)) return;
            if (!current.Valid() || !Safety.Matches(response, current.Id, current.Window.Id)
                || response.Plan is not null && !Safety.ValidPlan(response.Plan, observation))
                throw new InvalidOperationException("Discarded stale, mismatched, or invalid response. Capture and review again.");
            var foreground = Native.GetForegroundWindow();
            if (foreground != Handle && foreground != current.Window.Handle)
                throw new InvalidOperationException("Focus changed to another application. Response discarded; capture again.");
            ModeText.Text = ModeLabel(response.Mode);
            AnswerText.Text = response.Instruction
                + (response.Plan is null ? "" : "\n" + ScreenTaskSession.DescribePlan(response.Plan));
            companion.ShowResponse(response.Instruction);
            ShowPromptFeedback("Guidance is ready in Screen context. Review the next step below.");
            SpeakButton.IsEnabled = true;
            RenderCitations(response.Citations);
            StatusText.Text = response.Status switch
            {
                "completed" or "completion_candidate" => "Completion reported, not independently verified. Review the current app.",
                "clarification" or "needs_input" => "More input needed · task is not complete.",
                "blocked" => "Unsupported or blocked step · task is not complete.",
                _ => "Next step ready · switch to the selected window to see the outline."
            };
            var suggested = response.Target
                ?? (response.Plan?.Steps.FirstOrDefault() is { } first ? Safety.BindPlanAction(first, observation) : null);
            if (response.Status == "next_step" && suggested is { } target)
            {
                if (!Safety.ObservedTarget(target, current.Elements))
                    StatusText.Text = "Target rejected: not grounded in the reviewed elements, low confidence, or invalid coordinates. No highlight. Capture again or clarify.";
                else
                {
                    highlight = new(current.Window, current.Rect, current.CapturedAt, target, current.ResourceId);
                    UpdateScreenActionUi();
                }
            }
            ClearSnapshot();
        }
        catch (OperationCanceledException) { if (mine == generation) { ClearSnapshot(); StatusText.Text = "Request cancelled or timed out. Already sent data cannot be recalled."; } }
        catch (Exception ex) { if (mine == generation) { ClearSnapshot(); ShowError(ex); } }
        finally { if (mine == generation) sending = false; }
    }

    private async Task CaptureAndGuideAsync(bool resume = false)
    {
        if (WindowPicker.SelectedItem is not WindowChoice window
            || !sessionScreenContextApproved)
        {
            ShowPromptFeedback("Choose the exact window and approve screen context first.");
            return;
        }
        var previous = screenTask;
        if (resume && previous is { } retained && retained.CanApproveResourceHandoff(window.Id))
            retained.ApproveResourceHandoff(window.Id);
        else if (resume && (previous is null || !previous.CanContinue || previous.WindowId != window.Id))
        {
            ShowPromptFeedback("The retained task cannot continue on this window. Review it or submit a new request.");
            return;
        }
        var ct = BeginWork(preserveScreenTask: resume);
        long mine = generation;
        var task = resume ? previous! : new ScreenTaskSession(PromptBox.Text.Trim(), window.Id);
        screenTask = task;
        int shownStep = task.History.LastOrDefault()?.Step ?? 0;
        companion.BeginTask();
        void Changed()
        {
            if (!CurrentWork(mine, ct) || !ReferenceEquals(screenTask, task)) return;
            UpdateScreenTaskUi();
            StatusText.Text = task.Detail;
            ShowPromptFeedback(task.Detail);
            shownStep = PublishScreenTaskActions(task, shownStep, companion.ShowTaskAction);
            if (task.Running) companion.ShowTaskStatus(task.Detail);
            else companion.FinishTask(task);
        }
        async Task<Observation> CaptureStep(bool includeImage, CancellationToken token)
        {
            if (!CurrentWork(mine, ct)) throw new OperationCanceledException(ct);
            ClearHighlight();
            ClearSnapshot();
            var result = await CaptureService.Capture(window, token, includeImage);
            if (!CurrentWork(mine, ct) || token.IsCancellationRequested)
            {
                result.Dispose();
                throw new OperationCanceledException(token);
            }
            snapshot = result;
            return result.Observation(includeImage);
        }
        async Task<Guidance> GuideStep(Observation observation, TaskProgress progress, CancellationToken token)
        {
            api ??= new ApiClient();
            sending = true;
            try
            {
                var response = await api.Guide(observation, task.Prompt, token, progress);
                if (!CurrentWork(mine, ct)) throw new OperationCanceledException(ct);
                if (snapshot is not { } current || current.Id != observation.Id || !current.Valid()
                    || !Safety.Matches(response, current.Id, window.Id)
                    || response.TaskId != progress.TaskId || response.Step != progress.Step
                    || response.Plan is not null && !Safety.ValidPlan(response.Plan, observation))
                {
                    DiagnosticLog.Record("screen_task_response_discarded", new
                    { taskId = task.Id, step = progress.Step, reason = "snapshot_or_response_changed" });
                    throw new InvalidOperationException("Discarded stale or mismatched task response.");
                }
                nint foreground = Native.GetForegroundWindow();
                if (foreground != Handle && foreground != companion.PromptHandle && foreground != window.Handle)
                {
                    DiagnosticLog.Record("screen_task_response_discarded", new
                    { taskId = task.Id, step = progress.Step, reason = "foreground_changed" });
                    throw new InvalidOperationException("Focus changed to another app. No action was accepted.");
                }
                ModeText.Text = ModeLabel(response.Mode);
                AnswerText.Text = response.Instruction
                    + (response.Plan is null ? "" : "\n" + ScreenTaskSession.DescribePlan(response.Plan));
                SpeakButton.IsEnabled = true;
                RenderCitations(response.Citations);
                var guidanceTarget = response.Target;
                if (guidanceTarget is null && SelectedCameraMode == CameraRecoveryInteractionMode.Guide
                    && response.Plan?.Steps.FirstOrDefault() is { Kind: "action" } firstStep)
                    guidanceTarget = Safety.BindPlanAction(firstStep, observation);
                if (response.Status == "next_step" && guidanceTarget is { } target
                    && Safety.ObservedTarget(target, current.Elements))
                {
                    highlight = new(window, current.Rect, current.CapturedAt, target, observation.ResourceId);
                    UpdateScreenActionUi();
                }
                return response;
            }
            finally { if (mine == generation) sending = false; }
        }
        async Task<DesktopActionResult> ExecuteStep(Observation observation, TargetInfo target, CancellationToken token)
        {
            if (!CurrentWork(mine, ct)) throw new OperationCanceledException(ct);
            if (!CanAutoExecuteScreenAction(true, sessionAutomationApproved, target, SelectedCameraMode)
                || snapshot is not { } current || current.Id != observation.Id || !current.Valid())
                return new(false, true, "Task authority or the fresh target changed. No action was started.");
            var result = await ExecuteScreenActionAsync(
                new(window, current.Rect, current.CapturedAt, target, observation.ResourceId), token, automatic: true);
            if (!CurrentWork(mine, ct)) throw new OperationCanceledException(ct);
            return result ?? new(true, false, "The native action outcome is unknown. No retry is allowed.");
        }
        try
        {
            await task.RunAsync(CaptureStep, GuideStep, ExecuteStep,
                sessionAutomationApproved && SelectedCameraMode == CameraRecoveryInteractionMode.Control,
                Changed, ct, includePlanningImages: Environment.GetEnvironmentVariable("MSGUIDE_UIA_ONLY") != "1");
        }
        finally
        {
            if (mine == generation)
            {
                ClearSnapshot();
                if (task.Status != "needs_input") ClearHighlight();
                UpdateScreenTaskUi();
            }
        }
    }

    internal static int PublishScreenTaskActions(ScreenTaskSession task, int shownStep, Action<int, string> publish)
    {
        foreach (var step in task.History)
        {
            if (step.Step <= shownStep) continue;
            publish(step.Step, $"{step.Action} - {step.Outcome}");
            shownStep = step.Step;
        }
        return shownStep;
    }

    private void UpdateScreenTaskUi()
    {
        if (ScreenTaskPanel is null) return;
        ScreenTaskPanel.Visibility = screenTask is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCompactTaskUi();
        if (screenTask is not { } task) return;
        ScreenTaskStatusText.Text = $"{task.Status} - {task.Detail}"
            + (task.Plan is null ? "" : "\n" + ScreenTaskSession.DescribePlan(task.Plan, task.PlanCursor))
            + (task.RemainingWork.Length == 0 ? "" : "\nModel checkpoint (unverified): " + task.RemainingWork)
            + (task.History.Count == 0 ? "" : "\nRecent steps:\n" + string.Join("\n",
                task.History.Select(step => $"{step.Step}. {step.Action} - {step.Outcome}")));
        bool resourceHandoff = WindowPicker.SelectedItem is WindowChoice selected
            && task.CanApproveResourceHandoff(selected.Id);
        ContinueScreenTaskButton.IsEnabled = task.CanContinue && !DesktopAction.IsBusy
            && WindowPicker.SelectedItem is WindowChoice current
            && (current.Id == task.WindowId || resourceHandoff);
        ContinueScreenTaskButton.Content = resourceHandoff
            ? "Use selected window & continue"
            : task.ReplanRequired ? "Review boundary & replan" : "Review & continue";
        StopScreenTaskButton.IsEnabled = task.Running || task.Plan is not null && task.CanContinue;
        ScreenTaskReplyBox.IsEnabled = !task.Running && task.CanContinue;
    }

    private async void ContinueScreenTask_Click(object sender, RoutedEventArgs e) =>
        await ContinueScreenTaskAsync(ScreenTaskReplyBox.Text);

    private async Task ContinueScreenTaskAsync(string reply)
    {
        if (screenTask is not { CanContinue: true } task) return;
        task.UserInput = reply;
        ScreenTaskReplyBox.Clear();
        companion.Prompt.TaskReply.Clear();
        await CaptureAndGuideAsync(resume: true);
    }

    private void StopScreenTask_Click(object sender, RoutedEventArgs e)
    {
        screenTask?.Stop();
        CancelWork();
        UpdateScreenTaskUi();
        if (screenTask is { } task) companion.FinishTask(task);
    }

    private void UpdateCompactTaskUi() => companion?.UpdateTask(
        SelectedCameraMode, screenTask,
        screenTask is { CanContinue: true } task && !DesktopAction.IsBusy
            && WindowPicker.SelectedItem is WindowChoice selected
            && (selected.Id == task.WindowId || task.CanApproveResourceHandoff(selected.Id)),
        screenTask is { } retained && WindowPicker.SelectedItem is WindowChoice current
            && retained.CanApproveResourceHandoff(current.Id),
        cameraRecoverySensing is ICameraRecoveryControl);

    private void ForgetScreenTask()
    {
        screenTask = null;
        companion.ClearFeedback();
        ScreenTaskReplyBox.Clear();
        ScreenTaskStatusText.Text = "";
        UpdateScreenTaskUi();
    }

    private void RenderCitations(Citation[]? citations)
    {
        CitationsPanel.Children.Clear();
        foreach (var citation in (citations ?? []).Take(12))
        {
            if (citation is null) continue;
            var uri = Safety.CitationUri(citation.Source);
            string title = string.IsNullOrWhiteSpace(citation.Title) ? "Source" : citation.Title[..Math.Min(200, citation.Title.Length)];
            if (uri is null)
            { CitationsPanel.Children.Add(new TextBlock { Text = title + " (non-HTTPS source; not opened)", FontSize = 12 }); continue; }
            var button = new Button { Content = new TextBlock { Text = $"{title} ↗\n{uri.Host}", FontSize = 12 }, ToolTip = uri.AbsoluteUri, HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(button, $"Open HTTPS source: {title}, {uri.Host}");
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { StatusText.Text = "Could not open the HTTPS source in your browser."; }
            };
            CitationsPanel.Children.Add(button);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        ValidateDemoTask();
        if (screenTask is { Running: false }) UpdateScreenTaskUi();
        if (speech.Listening && DateTimeOffset.UtcNow - microphoneStarted > TimeSpan.FromSeconds(30)) speech.FinishListening();
        // Snapshot/outline checks below inspect identity/bounds only. ValidateDemoTask above
        // also checks Notepad tab metadata; neither path captures pixels or reads editor text here.
        if (snapshot is not null && !snapshot.Valid())
        { CancelWork(); StatusText.Text = "Snapshot expired or window moved/closed. Capture and review again."; }
        if (highlight is not { } h) return;
        if (!Safety.Fresh(h.CapturedAt, DateTimeOffset.UtcNow) || !h.Window.Matches()
            || !Native.GetWindowRect(h.Window.Handle, out var now) || !h.Rect.Same(now))
        { ClearHighlight(); StatusText.Text = "Highlight expired or window changed. Check next step for a fresh review."; return; }
        nint foreground = Native.GetForegroundWindow();
        if (foreground == h.Window.Handle)
        {
            if (!h.HasShown)
            {
                overlay.PointAt(h.Rect, h.Target.Box);
                h.HasShown = overlay.IsVisible;
                if (!h.HasShown) { ClearHighlight(); StatusText.Text = "Could not position the outline. Capture and review again."; }
            }
        }
        else if (PreserveScreenActionApproval(
            foreground, Handle, h.HasShown, SelectedCameraMode, h.Target))
        {
            overlay.Hide();
            h.HasShown = false;
        }
        else if (h.HasShown || foreground != Handle)
        { ClearHighlight(); }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex is InvalidOperationException ? ex.Message
            : ex is System.Net.Http.HttpRequestException ? "Cannot reach the loopback service. Start the backend and check the endpoint; no automatic retry."
            : "Operation failed. Check service compatibility or window support, then capture and approve again.";
    }

    private void UpdateScreenActionUi()
    {
        bool available = SelectedCameraMode == CameraRecoveryInteractionMode.Control
            && screenTask?.Running != true && !DesktopAction.IsBusy
            && highlight?.Target is { TargetId.Length: > 0, Action.Length: > 0 };
        ApproveScreenActionButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ApproveScreenActionButton.IsEnabled = available;
        if (available)
        {
            ApproveScreenActionButton.Content = $"Approve & {highlight!.Target.Action} “{highlight.Target.Label}”";
            ApproveScreenActionButton.ToolTip = highlight.Target.Action == "set_value"
                ? $"Replace the entire field with:\n{highlight.Target.Value}"
                : highlight.Target.Action == "scroll" ? $"One small scroll {highlight.Target.ScrollDirection}" : null;
        }
    }

    internal static bool CanAutoExecuteScreenAction(
        bool requestUsesSessionGrant, bool sessionAutomationGranted, TargetInfo target,
        CameraRecoveryInteractionMode mode) =>
        requestUsesSessionGrant && sessionAutomationGranted
        && mode == CameraRecoveryInteractionMode.Control
        && target is { TargetId.Length: > 0, Action.Length: > 0 } && Safety.ValidActionInput(target);

    internal static bool PreserveScreenActionApproval(
        nint foreground, nint companion, bool hasShown,
        CameraRecoveryInteractionMode mode, TargetInfo target) =>
        foreground == companion && hasShown
        && mode == CameraRecoveryInteractionMode.Control
        && target is { TargetId.Length: > 0, Action.Length: > 0 };

    internal static bool PreserveTaskDemoChange(ScreenTaskSession? task, string windowId) =>
        task is { Running: true, AwaitingActionEvidence: true } && task.WindowId == windowId;

    private void ClearHighlight()
    {
        highlight = null;
        overlay.Hide();
        ApproveScreenActionButton.Visibility = Visibility.Collapsed;
        ApproveScreenActionButton.IsEnabled = false;
    }

    private async void ApproveScreenAction_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCameraMode != CameraRecoveryInteractionMode.Control
            || screenTask?.Running == true || DesktopAction.IsBusy
            || highlight is not { } approved
            || string.IsNullOrWhiteSpace(approved.Target.TargetId)
            || string.IsNullOrWhiteSpace(approved.Target.Action))
            return;
        await ExecuteScreenActionAsync(
            approved, operation?.Token ?? CancellationToken.None, automatic: false);
    }

    private async Task<DesktopActionResult?> ExecuteScreenActionAsync(
        Highlight approved, CancellationToken ct, bool automatic)
    {
        long mine = generation;
        ApproveScreenActionButton.IsEnabled = false;
        StatusText.Text = automatic
            ? $"Showing the next {approved.Target.Action} action on “{approved.Target.Label}”…"
            : $"Showing the approved {approved.Target.Action} action on “{approved.Target.Label}”…";
        async Task<bool> Present(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!CurrentWork(mine, token) || !approved.Window.Matches()
                || !Safety.Fresh(approved.CapturedAt, DateTimeOffset.UtcNow)
                || !Native.GetWindowRect(approved.Window.Handle, out var current)
                || !approved.Rect.Same(current))
                return false;
            nint foreground = Native.GetForegroundWindow();
            if (!DesktopAction.CanPresentInForeground(foreground, approved.Window.Handle, Handle, companion.PromptHandle)
                || foreground != approved.Window.Handle && !Native.SetForegroundWindow(approved.Window.Handle))
                return false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            token.ThrowIfCancellationRequested();
            if (!CurrentWork(mine, token) || Native.GetForegroundWindow() != approved.Window.Handle) return false;
            highlight = approved;
            bool markerShown = await companion.MoveToActionTargetAsync(approved.Rect, approved.Target.Box, token);
            if (!CurrentWork(mine, token) || Native.GetForegroundWindow() != approved.Window.Handle) return false;
            overlay.PointAt(approved.Rect, approved.Target.Box, showBadge: !markerShown);
            if (!overlay.IsVisible) return false;
            approved.HasShown = true;
            DiagnosticLog.Record("screen_action_presented", new
            { targetId = approved.Target.TargetId, action = approved.Target.Action, foreground = true });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(250, token);
            return CurrentWork(mine, token) && overlay.IsVisible
                && Native.GetForegroundWindow() == approved.Window.Handle;
        }
        try
        {
            var result = await DesktopAction.RunVisible(Present, token => DesktopAction.ExecuteAsync(
                approved.Window, approved.Rect, approved.CapturedAt, approved.Target, token,
                approved.ResourceId), () =>
                {
                    if (mine == generation) ClearHighlight();
                }, ct);
            if (!CurrentWork(mine, ct)) return null;
            StatusText.Text = result.Detail;
            return result;
        }
        catch (OperationCanceledException)
        {
            if (mine == generation)
                StatusText.Text = "Cancellation requested. An in-flight action may still finish; no retry.";
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.Runtime.InteropServices.COMException
                or UnauthorizedAccessException)
        {
            if (mine == generation)
            {
                ClearHighlight();
                StatusText.Text = "The grounded action could not be invoked. MSGuide did not retry it.";
            }
        }
        finally
        {
            if (mine == generation)
            {
                if (automatic) companion.ShowTaskStatus(StatusText.Text);
                else companion.ShowResponse(StatusText.Text);
            }
        }
        return null;
    }

    private async void OpenDemo_Click(object sender, RoutedEventArgs e) => await OpenDemoAsync();

    private async Task OpenDemoAsync()
    {
        CancelWork();
        Task? rendered = null;
        if (demo is null)
        {
            demo = new DemoWindow();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRendered(object? sender, EventArgs args)
            {
                demo.ContentRendered -= OnRendered;
                ready.TrySetResult();
            }
            demo.ContentRendered += OnRendered;
            rendered = ready.Task;
            demo.WorkflowChanged += (_, _) =>
            {
                if (demoTask is { } task)
                {
                    ClearHighlight();
                    if (task.Executing) return;
                    if (task.Mode == InteractionMode.Guide && taskRunning)
                    { TaskStatusText.Text = "Demo changed. Choose I did it · check to verify the next step."; return; }
                }
                if (demo is not null && PreserveTaskDemoChange(screenTask,
                    new WindowChoice(new WindowInteropHelper(demo).Handle, (uint)Environment.ProcessId, demo.Title).Id))
                {
                    ClearHighlight();
                    return;
                }
                CancelWork();
                speech.Stop();
                StatusText.Text = "Demo changed. Check next step for a fresh capture and review.";
            };
            demo.Closed += (_, _) => { demo = null; if (!closing) { CancelWork(); RefreshWindows(); } };
        }
        demo.Show(); demo.Activate();
        if (rendered is not null) await rendered;
        RefreshWindows();
        WindowPicker.SelectedItem = WindowPicker.Items.Cast<WindowChoice>().FirstOrDefault(w => w.Title == "MSGuide Demo");
    }

    internal void ConfigureClickyDemoRequest()
    {
        CameraControlMode.IsChecked = true;
        PromptBox.Text = ClickyDemoQuestion;
    }

    private async void StartClickyDemo_Click(object sender, RoutedEventArgs e)
    {
        ConfigureClickyDemoRequest();
        await OpenDemoAsync();
        OpenScreenContext();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Activate();
        Capture_Click(CaptureButton, new RoutedEventArgs());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) { CancelWork(); RefreshWindows(); }
    private async void Health_Click(object sender, RoutedEventArgs e) => await CheckHealth();
    private void Window_Changed(object sender, SelectionChangedEventArgs e) { if (loaded && !refreshing) CancelWork(); }
    private void Prompt_Changed(object sender, TextChangedEventArgs e)
    {
        if (!loaded) return;
        bool cancelledSpeech = !applyingTranscript && (speech.Listening || speech.Finishing);
        if (cancelledSpeech) speech.Stop();
        promptRequestActive = false;
        bool recoveryWasActive = !cameraRecovery.CanSelectMode || cameraRecoveryBusy;
        CancelWork();
        ForgetScreenTask();
        ResetScreenContextUi();
        ResetCameraRecovery();
        StatusText.Text = cancelledSpeech
            ? "Dictation cancelled because you edited the question. Your edits are retained."
            : recoveryWasActive
                ? "Previous task stopped. Review your new question before asking; previous approval was cleared."
            : "Question draft updated. Review it, then select Ask MSGuide; previous approval was cleared.";
        ShowPromptFeedback(cancelledSpeech ? StatusText.Text
            : applyingTranscript
                ? "Voice draft updated. Review it before selecting Ask MSGuide."
                : "Draft changed. Select Ask MSGuide or press Enter to submit; previous approval was cleared.");
        UpdateCameraRecoveryUi();
        UpdatePromptSubmissionUi();
    }
    private void Approval_Changed(object sender, RoutedEventArgs e)
    {
        if (!loaded) return;
        if (sending) { CancelWork(); StatusText.Text = "Approval changed: request cancelled. Already transmitted data cannot be recalled."; return; }
        SendButton.IsEnabled = snapshot is not null && ConsentBox.IsChecked == true;
    }
    private void Discard_Click(object sender, RoutedEventArgs e) { CancelWork(); speech.Stop(); StatusText.Text = "Discarded. No new data will be sent; already transmitted data cannot be recalled."; }
    private void Pause_Click(object sender, RoutedEventArgs e) => Pause();
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (speech.Listening) speech.FinishListening();
        else StartVoiceDraft(append: false);
    }
    private void StopSpeech_Click(object sender, RoutedEventArgs e)
    {
        bool cancelling = speech.Finishing;
        if (cancelling) speech.StopListening();
        else speech.FinishListening();
        speech.StopSpeaking();
        CancelWork(cancelCameraRecovery: false);
        StatusText.Text = cancelling
            ? "Local transcription cancelled. Existing text is retained; no request was submitted."
            : "Audio stop requested. Review the transcript before asking; camera mode and approval are unchanged.";
    }
    private void Speak_Click(object sender, RoutedEventArgs e) => speech.Speak(AnswerText.Text);

    private void Cleanup()
    {
        closing = true;
        timer.Stop();
        CancelWork();
        if (hotkeyRegistered) Native.UnregisterHotKey(Handle, 0x4D47);
        if (Handle != 0) HwndSource.FromHwnd(Handle)?.RemoveHook(WindowHook);
        speech.Dispose(); api?.Dispose();
        companion.Stop();
        overlay.Close(); demo?.Close();
        PromptBox.Clear(); MetadataText.Clear(); AnswerText.Text = ""; CitationsPanel.Children.Clear();
    }
}