using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MSGuide.Desktop;

public partial class MainWindow
{
    private DemoTaskSession? demoTask;
    private NotepadTaskSession? notepadTask;
    private bool taskRunning;
    private readonly DispatcherTimer taskTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    private void StopDemoTask()
    {
        taskTimer.Stop();
        taskTimer.Tick -= ExecuteTask_Tick;
        demoTask?.Stop();
        demoTask = null;
        notepadTask?.Stop();
        notepadTask = null;
        taskRunning = false;
        if (TaskConsent is null) return; // XAML initialization can raise events before all controls exist.
        TaskConsent.IsChecked = false;
        StartTaskButton.IsEnabled = ObserveTaskButton.IsEnabled = false;
        TaskStatusText.Text = "No task authority active. Prepare and approve a new plan to continue.";
    }

    private void Interaction_Changed(object sender, RoutedEventArgs e)
    {
        if (!loaded) return;
        CancelWork();
        speech.Stop();
        TaskPlanText.Text = "Mode changed. Prepare a new task; previous approval has been revoked.";
    }

    private void PrepareTask_Click(object sender, RoutedEventArgs e)
    {
        CancelWork();
        speech.Stop();
        try
        {
            if (demo is null) throw new InvalidOperationException("Open this companion's demo first.");
            demoTask = new DemoTaskSession(demo, Handle,
                ControlMode.IsChecked == true ? InteractionMode.Control : InteractionMode.Guide);
            TaskPlanText.Text = (demoTask.Mode == InteractionMode.Guide ? "GUIDE ME · you click\n" : "DO IT FOR ME · local semantic UI actions\n") + demoTask.Plan;
            TaskStatusText.Text = "Review the plan. No actions have been authorized yet.";
        }
        catch (Exception ex) { FailTask(ex); }
    }

    private void TaskConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (!loaded) return;
        if (taskRunning && TaskConsent.IsChecked != true)
        { StopTask_Click(sender, e); return; }
        StartTaskButton.IsEnabled = (demoTask is not null || notepadTask is not null) && !taskRunning && TaskConsent.IsChecked == true;
    }

    private void StartTask_Click(object sender, RoutedEventArgs e)
    {
        if (notepadTask is { } note && !taskRunning && TaskConsent.IsChecked == true)
        {
            try
            {
                note.Approve();
                taskRunning = true;
                StartTaskButton.IsEnabled = false;
                ObserveTaskButton.IsEnabled = note.Mode == InteractionMode.Guide;
                PresentNotepadStep();
                if (note.Mode == InteractionMode.Control)
                {
                    taskTimer.Tick += ExecuteTask_Tick;
                    taskTimer.Start();
                }
            }
            catch (Exception ex) { FailTask(ex); }
            return;
        }
        if (demoTask is null || taskRunning || TaskConsent.IsChecked != true) return;
        try
        {
            demoTask.Approve();
            taskRunning = true;
            StartTaskButton.IsEnabled = false;
            ObserveTaskButton.IsEnabled = demoTask.Mode == InteractionMode.Guide;
            PresentTaskStep();
            if (demoTask.Mode == InteractionMode.Control)
            {
                taskTimer.Tick += ExecuteTask_Tick;
                taskTimer.Start();
            }
        }
        catch (Exception ex) { FailTask(ex); }
    }

    private void PresentTaskStep()
    {
        if (demoTask is not { } task || demo is null) return;
        var target = task.Observe();
        if (task.Finished)
        {
            StopDemoTask();
            highlight = null; overlay.Hide();
            TaskStatusText.Text = "Verified: troubleshooting instructions reached. Task finished; no build was repaired. Execution authority revoked.";
            return;
        }
        TaskStatusText.Text = $"{(task.Mode == InteractionMode.Guide ? "Your next step" : "Next approved action")}: {task.CurrentStep!.Label} — {task.CurrentStep.Explanation}";
        if (target is { } t)
            highlight = new(new WindowChoice(new WindowInteropHelper(demo).Handle, (uint)Environment.ProcessId, demo.Title),
                t.Bounds, DateTimeOffset.UtcNow, t.Box);
    }

    private void ExecuteTask_Tick(object? sender, EventArgs e)
    {
        if (taskRunning && notepadTask is { } note)
        {
            try { note.Execute(); PresentNotepadStep(); }
            catch (Exception ex) { FailTask(ex); }
            return;
        }
        if (!taskRunning || demoTask is not { } task) return;
        try
        {
            // A single synchronous action per dispatcher tick, never a queue of future inputs.
            task.ExecuteNext();
            PresentTaskStep();
        }
        catch (Exception ex) { FailTask(ex); }
    }

    private void ObserveTask_Click(object sender, RoutedEventArgs e)
    {
        if (taskRunning && notepadTask?.Mode == InteractionMode.Guide)
        {
            try { PresentNotepadStep(); }
            catch (Exception ex) { FailTask(ex); }
            return;
        }
        if (!taskRunning || demoTask?.Mode != InteractionMode.Guide) return;
        try { PresentTaskStep(); }
        catch (Exception ex) { FailTask(ex); }
    }

    private void ValidateDemoTask()
    {
        if (notepadTask is { } note)
        {
            try { note.Validate(); }
            catch (Exception ex) { FailTask(ex); }
        }
        if (demoTask is null) return;
        try { demoTask.Validate(); }
        catch (Exception ex) { FailTask(ex); }
    }

    private void FailTask(Exception ex)
    {
        StopDemoTask();
        highlight = null; overlay.Hide();
        TaskStatusText.Text = ex is InvalidOperationException ? ex.Message : "Task stopped because local control could not be verified. No automatic retry.";
    }

    private void StopTask_Click(object sender, RoutedEventArgs e)
    {
        CancelWork();
        speech.Stop();
        TaskStatusText.Text = "Stopped. No further actions will be started. Completed actions are not undone; an in-flight native request may still finish.";
    }

    private void TakeOver_Click(object sender, RoutedEventArgs e)
    {
        StopTask_Click(sender, e);
        GuideMode.IsChecked = true;
        TaskStatusText.Text = "Manual takeover. Control approval revoked; continue yourself or prepare a new Guide me task.";
    }

    private void Draft_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!loaded) return;
        CancelWork();
        TaskPlanText.Text = "Draft changed. Prepare and approve a new plan.";
    }

    private void PrepareNotepad_Click(object sender, RoutedEventArgs e)
    {
        CancelWork(); speech.Stop();
        try
        {
            if (ControlMode.IsChecked == true && Environment.GetEnvironmentVariable("MSGUIDE_ENABLE_EXPERIMENTAL_NOTEPAD_CONTROL") != "1")
                throw new InvalidOperationException("Notepad control awaits native acceptance. For synthetic testing only, launch with MSGUIDE_ENABLE_EXPERIMENTAL_NOTEPAD_CONTROL=1; otherwise choose Guide me.");
            if (WindowPicker.SelectedItem is not WindowChoice selected)
                throw new InvalidOperationException("Select your blank Notepad window in the window chooser below first.");
            notepadTask = new(new NotepadEditor(selected, Handle),
                ControlMode.IsChecked == true ? InteractionMode.Control : InteractionMode.Guide, DraftBox.Text);
            TaskPlanText.Text = (notepadTask.Mode == InteractionMode.Guide ? "GUIDE ME · you type\n" : "DO IT FOR ME · one local text insertion\n") + notepadTask.Plan;
            TaskStatusText.Text = "Review target and exact text, approve, then Start. No existing text was read. Keep MSGuide foreground for control insertion.";
        }
        catch (Exception ex) { FailTask(ex); }
    }

    private void PresentNotepadStep()
    {
        if (notepadTask is not { } note) return;
        var target = note.Observe();
        if (note.Finished)
        {
            StopDemoTask(); highlight = null; overlay.Hide();
            TaskStatusText.Text = "Verified: Notepad contains the exact approved draft. Authority revoked. No Save command issued; Notepad may retain unsaved session data.";
            return;
        }
        TaskStatusText.Text = note.Mode == InteractionMode.Guide
            ? "Switch to Notepad and type the exact draft shown in the plan. Return and choose I did it · check. The check reads at most 1000 characters locally."
            : "Next approved action: insert the draft in the empty editor and verify it locally. Keep MSGuide foreground.";
        if (target is { } t) highlight = new(note.Window, t.Bounds, DateTimeOffset.UtcNow, t.Box);
    }
}