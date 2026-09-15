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
    private ApiClient? api;
    private readonly OverlayWindow overlay = new();
    private readonly SpeechService speech = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? operation;
    private Snapshot? snapshot;
    private DemoWindow? demo;
    private long generation;
    private bool loaded, refreshing, closing, hotkeyRegistered, sending;
    private DateTimeOffset microphoneStarted;
    private Highlight? highlight;
    private nint Handle => new WindowInteropHelper(this).Handle;
    private sealed record Highlight(WindowChoice Window, Native.RECT Rect, DateTimeOffset CapturedAt, double[] Box)
    { public bool HasShown { get; set; } }

    public MainWindow()
    {
        InitializeComponent();
        speech.Transcribed += text => Dispatcher.BeginInvoke(() =>
        {
            if (!closing && speech.Listening)
            {
                PromptBox.Text = (PromptBox.Text + " " + text).Trim();
                PromptBox.CaretIndex = PromptBox.Text.Length;
            }
        });
        speech.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            SpeechText.Text = status;
            MicButton.Content = speech.Listening ? "Stop microphone" : "Start microphone";
        });
        SourceInitialized += InitializeNative;
        Loaded += async (_, _) =>
        {
            loaded = true;
            RefreshWindows();
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
        Native.SetWindowDisplayAffinity(Handle, 0x11);
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

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x312 && wParam.ToInt32() == 0x4D47)
        { InvokeNearCursor(); handled = true; }
        return 0;
    }

    private void InvokeNearCursor()
    {
        CancelWork();
        speech.Stop();
        WindowState = WindowState.Normal;
        Show();
        if (Native.GetCursorPos(out var cursor))
        {
            var monitor = Native.MonitorFromPoint(cursor, 2);
            var info = new Native.MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
                double scale = dpi > 0 ? dpi / 96d : 1;
                int width = Math.Min(info.Work.Width, (int)Math.Round(480 * scale));
                int height = Math.Min(info.Work.Height, (int)Math.Round(780 * scale));
                int x = Math.Clamp(cursor.X + 18, info.Work.Left, info.Work.Right - width);
                int y = Math.Clamp(cursor.Y + 18, info.Work.Top, info.Work.Bottom - height);
                Native.SetWindowPos(Handle, 0, x, y, width, height, 0x14);
                // Reapply after a monitor DPI transition; the requested desktop rect stays physical.
                Native.SetWindowPos(Handle, 0, x, y, width, height, 0x14);
            }
        }
        Activate();
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
        StatusText.Text = "Ready · choose a window and capture explicitly.";
    }

    private void RefreshWindows()
    {
        var selected = WindowPicker.SelectedItem as WindowChoice;
        refreshing = true;
        var windows = WindowChoice.List(Handle, overlay.Handle);
        WindowPicker.ItemsSource = windows;
        WindowPicker.SelectedItem = windows.FirstOrDefault(w => w.Id == selected?.Id)
            ?? windows.FirstOrDefault(w => w.Title == "MSGuide Demo");
        refreshing = false;
    }

    private CancellationToken BeginWork()
    {
        CancelWork();
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

    private void CancelWork()
    {
        StopDemoTask();
        generation++;
        operation?.Cancel(); operation?.Dispose(); operation = null;
        sending = false;
        ClearSnapshot();
        highlight = null; overlay.Hide();
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
        speech.Stop();
        PromptBox.Clear();
        DraftBox.Clear();
        TaskPlanText.Text = "Paused. Prepare a new plan to continue.";
        AnswerText.Text = "Paused. Capture, text, and response cleared. Already transmitted data cannot be recalled.";
        CitationsPanel.Children.Clear();
        SpeakButton.IsEnabled = false;
        ModeText.Text = "PAUSED · no execution authority";
        StatusText.Text = "Paused · no capture, microphone, or speech active.";
    }

    private void Dismiss()
    {
        Pause();
        if (hotkeyRegistered) Hide();
        else WindowState = WindowState.Minimized;
    }

    private async Task CheckHealth()
    {
        var ct = BeginWork();
        long mine = generation;
        try
        {
            api ??= new ApiClient();
            StatusText.Text = "Checking the local service…";
            var health = await api.Health(ct);
            if (mine != generation) return;
            if (health.Status != "ok" || health.Version != "0.2.0" || health.Mode is not ("demo" or "model"))
                throw new InvalidOperationException("Incompatible service. Expected healthy API v0.2.0 with demo/model mode.");
            ModeText.Text = ModeLabel(health.Mode);
            StatusText.Text = $"Connected · {health.Mode} · v{health.Version} · no capture active";
        }
        catch (OperationCanceledException) { if (mine == generation) StatusText.Text = "Service check cancelled or timed out."; }
        catch (Exception ex) { if (mine == generation) ShowError(ex); }
    }

    private static string ModeLabel(string mode) => mode == "demo"
        ? "DEMO · deterministic rules / sample guidance · not a model"
        : "MODEL · backend-configured provider · verify its data policy";

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (WindowPicker.SelectedItem is not WindowChoice window)
        { StatusText.Text = "Choose a visible window first. Open demo for the supported synthetic workflow."; return; }
        var ct = BeginWork();
        long mine = generation;
        speech.Stop();
        CaptureButton.IsEnabled = NextButton.IsEnabled = false;
        StatusText.Text = $"Capturing only “{window.Title}” locally… nothing is being uploaded.";
        try
        {
            var result = await CaptureService.Capture(window, ct);
            if (!CurrentWork(mine, ct)) { result.Dispose(); return; }
            snapshot = result;
            PreviewImage.Source = FullPreviewImage.Source = result.Preview;
            CaptureDetails.Text = $"{window.Title} · {result.Preview!.PixelWidth} × {result.Preview.PixelHeight} · {result.Png.Length / 1024} KB PNG\n{result.Note}";
            MetadataText.Text = result.Text + "\n\nELEMENTS (normalized x, y, width, height):\n"
                + JsonSerializer.Serialize(result.Elements, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            ReviewPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Review ready · stored in memory · nothing sent. Approve or discard.";
        }
        catch (OperationCanceledException) { if (mine == generation) StatusText.Text = "Capture cancelled. Nothing sent."; }
        catch (Exception ex) { if (mine == generation) ShowError(ex); }
        finally { if (mine == generation) CaptureButton.IsEnabled = NextButton.IsEnabled = true; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
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
        StatusText.Text = ShareImage.IsChecked == true ? "Sending approved text, boxes, and image…" : "Sending approved text and boxes only · image not shared…";
        try
        {
            api ??= new ApiClient();
            var response = await api.Guide(current.Observation(ShareImage.IsChecked == true), prompt, ct);
            if (!CurrentWork(mine, ct)) return;
            if (!current.Valid() || !Safety.Matches(response, current.Id, current.Window.Id))
                throw new InvalidOperationException("Discarded stale, mismatched, or invalid response. Capture and review again.");
            var foreground = Native.GetForegroundWindow();
            if (foreground != Handle && foreground != current.Window.Handle)
                throw new InvalidOperationException("Focus changed to another application. Response discarded; capture again.");
            ModeText.Text = ModeLabel(response.Mode);
            AnswerText.Text = response.Instruction;
            SpeakButton.IsEnabled = true;
            RenderCitations(response.Citations);
            StatusText.Text = response.Status switch
            {
                "completed" => "Completed according to fresh guidance · you performed every step.",
                "clarification" => "More context needed · no highlight shown.",
                _ => "Next step ready · switch to the selected window to see the outline."
            };
            if (response.Status == "next_step" && response.Target is { } target)
            {
                if (!Safety.ObservedTarget(target, current.Elements))
                    StatusText.Text = "Target rejected: not grounded in the reviewed elements, low confidence, or invalid coordinates. No highlight. Capture again or clarify.";
                else highlight = new(current.Window, current.Rect, current.CapturedAt, target.Box);
            }
            ClearSnapshot();
        }
        catch (OperationCanceledException) { if (mine == generation) { ClearSnapshot(); StatusText.Text = "Request cancelled or timed out. Already sent data cannot be recalled."; } }
        catch (Exception ex) { if (mine == generation) { ClearSnapshot(); ShowError(ex); } }
        finally { if (mine == generation) sending = false; }
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
        if (speech.Listening && DateTimeOffset.UtcNow - microphoneStarted > TimeSpan.FromSeconds(30)) speech.StopListening();
        // Snapshot/outline checks below inspect identity/bounds only. ValidateDemoTask above
        // also checks Notepad tab metadata; neither path captures pixels or reads editor text here.
        if (snapshot is not null && !snapshot.Valid())
        { CancelWork(); StatusText.Text = "Snapshot expired or window moved/closed. Capture and review again."; }
        if (highlight is not { } h) return;
        if (!Safety.Fresh(h.CapturedAt, DateTimeOffset.UtcNow) || !h.Window.Matches()
            || !Native.GetWindowRect(h.Window.Handle, out var now) || !h.Rect.Same(now))
        { highlight = null; overlay.Hide(); StatusText.Text = "Highlight expired or window changed. Check next step for a fresh review."; return; }
        nint foreground = Native.GetForegroundWindow();
        if (foreground == h.Window.Handle)
        {
            if (!h.HasShown)
            {
                overlay.PointAt(h.Rect, h.Box);
                h.HasShown = overlay.IsVisible;
                if (!h.HasShown) { highlight = null; StatusText.Text = "Could not position the outline. Capture and review again."; }
            }
        }
        else if (h.HasShown || foreground != Handle)
        { highlight = null; overlay.Hide(); }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex is InvalidOperationException ? ex.Message
            : ex is System.Net.Http.HttpRequestException ? "Cannot reach the loopback service. Start the backend and check the endpoint; no automatic retry."
            : "Operation failed. Check service compatibility or window support, then capture and approve again.";
    }

    private void OpenDemo_Click(object sender, RoutedEventArgs e)
    {
        CancelWork();
        if (demo is null)
        {
            demo = new DemoWindow();
            demo.WorkflowChanged += (_, _) =>
            {
                if (demoTask is { } task)
                {
                    highlight = null; overlay.Hide();
                    if (task.Executing) return;
                    if (task.Mode == InteractionMode.Guide && taskRunning)
                    { TaskStatusText.Text = "Demo changed. Choose I did it · check to verify the next step."; return; }
                }
                CancelWork();
                speech.Stop();
                StatusText.Text = "Demo changed. Check next step for a fresh capture and review.";
            };
            demo.Closed += (_, _) => { demo = null; if (!closing) { CancelWork(); RefreshWindows(); } };
        }
        demo.Show(); demo.Activate();
        RefreshWindows();
        WindowPicker.SelectedItem = WindowPicker.Items.Cast<WindowChoice>().FirstOrDefault(w => w.Title == "MSGuide Demo");
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { CancelWork(); RefreshWindows(); }
    private async void Health_Click(object sender, RoutedEventArgs e) => await CheckHealth();
    private void Window_Changed(object sender, SelectionChangedEventArgs e) { if (loaded && !refreshing) CancelWork(); }
    private void Prompt_Changed(object sender, TextChangedEventArgs e) { if (loaded) CancelWork(); }
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
        CancelWork();
        microphoneStarted = DateTimeOffset.UtcNow;
        speech.Toggle();
    }
    private void StopSpeech_Click(object sender, RoutedEventArgs e) { speech.Stop(); CancelWork(); StatusText.Text = "Speech and pending guidance stopped."; }
    private void Speak_Click(object sender, RoutedEventArgs e) => speech.Speak(AnswerText.Text);

    private void Cleanup()
    {
        closing = true;
        timer.Stop();
        CancelWork();
        if (hotkeyRegistered) Native.UnregisterHotKey(Handle, 0x4D47);
        if (Handle != 0) HwndSource.FromHwnd(Handle)?.RemoveHook(WindowHook);
        speech.Dispose(); api?.Dispose();
        overlay.Close(); demo?.Close();
        PromptBox.Clear(); MetadataText.Clear(); AnswerText.Text = ""; CitationsPanel.Children.Clear();
    }
}