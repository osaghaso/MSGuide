using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class NotepadTests
{
    internal sealed class AssertionFailure(int line) : InvalidOperationException($"Notepad assertion at source line {line}.");
    private static void Check(bool value, [CallerLineNumber] int line = 0)
    { if (!value) throw new AssertionFailure(line); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected);
    }

    private sealed class Editor : INotepadEditor
    {
        public WindowChoice Window => new(1, 1, "Synthetic test editor");
        internal string Text = "";
        internal bool Available = true, Corrupt, Timeout;
        internal int Writes, Reads;
        public void Validate() { if (!Available) throw new InvalidOperationException(); }
        public int Length() { Validate(); return Text.Length; }
        public string Read() { Validate(); Reads++; return Text; }
        public void Write(string text)
        {
            Validate(); Writes++;
            Text = Corrupt ? "unexpected" : text;
            if (Timeout) throw new InvalidOperationException();
        }
        public (Native.RECT Bounds, double[] Box) Target() => (new() { Right = 100, Bottom = 100 }, [0, 0, 1, 1]);
    }

    internal static void Run(List<string> checks)
    {
        const string draft = "MSGuide synthetic draft.";
        var now = DateTimeOffset.UtcNow;
        Editor editor = new();
        NotepadTaskSession New(InteractionMode mode = InteractionMode.Control) => new(editor, mode, draft, () => now);
        var task = New(); Reject(task.Execute); Check(editor.Writes == 0); Reject(task.Approve);
        task = New(); task.Approve(); task.Execute(); Check(task.Finished && editor.Text == draft && editor.Writes == 1);
        Reject(task.Execute); Check(editor.Writes == 1);
        checks.Add("notepad-single-use-approved-exact-text");

        editor = new() { Text = "existing content" }; Reject(() => New()); Check(editor.Reads == 0 && editor.Writes == 0);
        editor = new(); task = New(); editor.Text = "changed"; Reject(task.Approve); Check(editor.Writes == 0);
        editor = new(); task = New(); task.Approve(); editor.Text = "changed"; Reject(task.Execute); Check(editor.Writes == 0);
        checks.Add("notepad-existing-and-changed-content-not-overwritten");

        editor = new(); task = New(InteractionMode.Guide); task.Approve();
        Check(task.Observe() is not null && !task.Finished && editor.Writes == 0);
        editor.Text = "part"; Check(task.Observe() is not null && !task.Finished);
        editor.Text = draft; Check(task.Observe() is null && task.Finished && editor.Writes == 0);
        Reject(task.Execute); Check(editor.Writes == 0);
        checks.Add("notepad-guide-verifies-user-text-without-writing");

        editor = new(); task = new(editor, InteractionMode.Guide, "Line one\nLine two"); task.Approve();
        editor.Text = "Line one\rLine two";
        Check(task.Observe() is null && task.Finished && editor.Writes == 0);
        checks.Add("notepad-newline-normalization");

        foreach (string cause in new[] { "stop", "expiry", "scope", "duplicate-approval", "oversized" })
        {
            editor = new(); task = New(); task.Approve();
            switch (cause)
            {
                case "stop": task.Stop(); break;
                case "expiry": now = now.AddSeconds(60); break;
                case "scope": editor.Available = false; break;
                case "duplicate-approval": Reject(task.Approve); break;
                case "oversized": editor.Text = new string('x', 1001); break;
            }
            Reject(task.Execute); Check(editor.Writes == 0);
        }
        checks.Add("notepad-stop-expiry-scope-and-reapproval-rejected");

        foreach (bool timeout in new[] { false, true })
        {
            editor = new() { Timeout = timeout, Corrupt = !timeout }; task = New(); task.Approve();
            Reject(task.Execute); Reject(task.Execute); Check(editor.Writes == 1);
        }
        checks.Add("notepad-unknown-or-mismatched-outcome-never-retried");
        editor = new();
        foreach (string invalid in new[] { "", "  ", "bad\0text", new string('x', 1001) })
            Reject(() => new NotepadTaskSession(editor, InteractionMode.Control, invalid));
        Check(!NotepadEditor.TrustedExecutable(@"C:\Temp\notepad.exe"));
        checks.Add("notepad-draft-and-executable-validation");

        var companion = new MainWindow();
        try
        {
            foreach (string reason in new[] { "stop", "takeover", "consent", "mode", "pause", "prompt", "draft" })
            {
                editor = new(); task = New(); task.Approve();
                Check(companion.CheckNotepadInterruption(task, reason));
                Reject(task.Execute); Check(editor.Writes == 0);
            }
        }
        finally { companion.Close(); }
        checks.Add("notepad-companion-interruption-handlers-revoke");
    }

    internal static async Task RunNative(nint target, List<string> checks, Action<string> stage,
        InteractionMode mode = InteractionMode.Control)
    {
        // The caller must supply a deliberately selected blank Notepad HWND. No discovery by title.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        const string draft = "MSGuide native test:\napproved synthetic draft.";
        bool guide = mode == InteractionMode.Guide;
        using var cancelled = new CancellationTokenSource();
        var instructions = new TextBlock { Text = $"Selected Notepad HWND: {target.ToInt64()}\n\n"
            + (guide ? "Guide test: you type; this test never inserts text. Approve local text verification, then type exactly:\n\n"
                : "Control test: approve one insertion into the selected empty editor:\n\n")
            + draft + "\n\nNo Save command. Notepad may retain unsaved text. Use only a new blank single-tab window." };
        var approve = new Button { Content = guide ? "Approve Guide verification" : "Approve one insertion" };
        var verify = new Button { Content = "I typed it · check", IsEnabled = false };
        var stop = new Button { Content = "Stop / take over manually" };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(instructions); panel.Children.Add(approve);
        if (guide) panel.Children.Add(verify);
        panel.Children.Add(stop);
        var host = new Window { Title = $"MSGuide Notepad {mode} acceptance", Width = 540, Height = 480,
            Content = new ScrollViewer { Content = panel } };
        NotepadTaskSession? task = null;
        var overlay = new OverlayWindow();
        int activations = 0;
        overlay.Activated += (_, _) => activations++;
        void Stop()
        {
            task?.Stop(); overlay.Hide(); cancelled.Cancel();
        }
        stop.Click += (_, _) => Stop();
        host.Closed += (_, _) => Stop();
        try
        {
            stage("notepad-await-explicit-approval");
            // Only the dedicated approval button accepts consent, not a click anywhere in the host.
            var consent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            approve.Click += (_, _) => { approve.IsEnabled = false; consent.TrySetResult(); };
            host.Show();
            await consent.Task.WaitAsync(TimeSpan.FromSeconds(30), cancelled.Token);
            var hostHandle = new WindowInteropHelper(host).Handle;
            Check(Native.GetForegroundWindow() == hostHandle);
            Native.GetWindowThreadProcessId(target, out var pid);
            stage("notepad-target-validation");
            var editor = new NotepadEditor(new(target, pid, Native.Title(target)), hostHandle);
            task = new NotepadTaskSession(editor, mode, draft);
            task.Approve();
            if (guide)
            {
                stage("notepad-guide-highlight");
                var location = task.Observe();
                Check(location is not null && !task.Finished && editor.Length() == 0);
                var t = location!.Value;
                overlay.PointAt(t.Bounds, t.Box);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(overlay.IsVisible && Native.IsWindowVisible(overlay.Handle)
                    && Native.GetForegroundWindow() == hostHandle && activations == 0);
                checks.Add("native-notepad-guide-highlight-no-activation");
                instructions.Text = "Type these two lines in the selected Notepad, then return and click I typed it · check within 45 seconds:\n\n" + draft;
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                verify.Click += (_, _) =>
                {
                    try
                    {
                        cancelled.Token.ThrowIfCancellationRequested();
                        Check(Native.GetForegroundWindow() == hostHandle);
                        if (task.Observe() is null && task.Finished) completed.TrySetResult();
                        else instructions.Text = "Text does not match yet. Correct it yourself, then check again:\n\n" + draft;
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                };
                verify.IsEnabled = true;
                stage("notepad-guide-await-user-text");
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(45), cancelled.Token);
                Check(task.Finished && activations == 0);
                checks.Add("native-notepad-guide-user-multiline-text-verified-no-write");
            }
            else
            {
                stage("notepad-control-pre-action");
                instructions.Text = "Approved. Insertion starts after 1.2 seconds. Keep this window foreground; Stop revokes future work.";
                await Task.Delay(1200, cancelled.Token);
                cancelled.Token.ThrowIfCancellationRequested();
                stage("notepad-insert-and-verify"); task.Execute();
                Check(task.Finished);
                checks.Add("native-notepad-exact-approved-text-verified-no-save-command");
            }
            stage("complete");
        }
        finally
        {
            task?.Stop();
            try { host.Close(); }
            finally { overlay.Close(); }
        }
    }
}

public partial class MainWindow
{
    internal bool CheckNotepadInterruption(NotepadTaskSession session, string reason)
    {
        loaded = false; ControlMode.IsChecked = true; TaskConsent.IsChecked = true;
        notepadTask = session; taskRunning = true;
        taskTimer.Tick += ExecuteTask_Tick; taskTimer.Start(); loaded = true;
        try
        {
            switch (reason)
            {
                case "stop": StopTask_Click(this, new RoutedEventArgs()); break;
                case "takeover": TakeOver_Click(this, new RoutedEventArgs()); break;
                case "consent": TaskConsent.IsChecked = false; break;
                case "mode": GuideMode.IsChecked = true; break;
                case "pause": Pause(); break;
                case "prompt": PromptBox.Text = "Changed Notepad task"; break;
                case "draft": DraftBox.Text = "Changed exact draft"; break;
            }
            ExecuteTask_Tick(this, EventArgs.Empty);
            return notepadTask is null && !taskRunning && !taskTimer.IsEnabled && TaskConsent.IsChecked == false
                && !StartTaskButton.IsEnabled && !ObserveTaskButton.IsEnabled && highlight is null;
        }
        finally { loaded = false; }
    }
}