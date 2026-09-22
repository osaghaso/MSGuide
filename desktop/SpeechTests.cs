using System.IO;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MSGuide.Desktop;

internal static class SpeechTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Speech regression failed: {name}.");
    }

    public static async Task RunAsync()
    {
        CheckVoiceControls();
        var first = new FakeRecognizer();
        var second = new FakeRecognizer();
        var queue = new Queue<IDictationRecognizer>([first, second]);
        using var speech = new SpeechService(queue.Dequeue);
        var text = new List<string>();
        string preview = "";
        int level = 0;
        speech.Transcribed += text.Add;
        speech.PreviewChanged += value => preview = value;
        speech.AudioLevelChanged += value => level = value;
        speech.Toggle();
        Check(speech.Listening && first.Starts == 1, "start requires explicit request");
        first.Preview("a provisional phrase");
        Check(preview == "a provisional phrase" && text.Count == 0, "hypothesis stays out of the draft");
        first.Level(14);
        first.Problem("TooSoft");
        Check(level == 14 && speech.Status.Contains("too soft", StringComparison.Ordinal), "soft input is visible");
        first.Result("Check my Teams camera", 0.20f);
        Check(text.SequenceEqual(["Check my Teams camera"])
            && speech.Status.StartsWith("Uncertain", StringComparison.Ordinal), "low confidence is retained for review");
        speech.FinishListening();
        speech.FinishListening();
        Check(!speech.Listening && speech.Finishing && first.Finishes == 1 && first.Cancels == 0,
            "normal stop drains once rather than cancelling");
        first.Result("please", 0.9f);
        Check(text.Count == 2, "final recognized phrase is delivered after stopping");
        first.Complete();
        Check(!speech.Listening && !speech.Finishing && first.Disposals == 1
            && level == 0 && preview.Length == 0
            && speech.Status.Contains("Uncertain transcript", StringComparison.Ordinal),
            "completion closes input and retains uncertainty warning");
        string completedStatus = speech.Status;
        speech.Stop();
        Check(speech.Status == completedStatus, "Stop audio preserves the useful result");

        speech.Toggle();
        first.Result("old phrase", 0.99f);
        first.Problem("TooLoud");
        Check(text.Count == 2 && speech.Status.StartsWith("Recording", StringComparison.Ordinal),
            "old session callbacks cannot affect a new recording");
        speech.Stop();
        second.Result("cancelled phrase", 0.99f);
        second.Complete();
        Check(text.Count == 2 && !speech.Listening && !speech.Finishing && second.Disposals == 1,
            "cancellation discards late phrases and releases input");

        var timeoutInput = new FakeRecognizer();
        using var timeout = new SpeechService(() => timeoutInput);
        timeout.Toggle();
        timeout.FinishListening();
        timeout.FinishTimedOut();
        Check(timeoutInput.Cancels == 1 && timeoutInput.Disposals == 1
            && !timeout.Finishing && timeout.Status.Contains("last phrase", StringComparison.Ordinal),
            "finishing has a bounded deadline and explicit incomplete result");

        var autoInput = new FakeRecognizer { FinishTimeout = TimeSpan.FromSeconds(45) };
        using var autoStop = new SpeechService(() => autoInput);
        autoStop.Toggle();
        autoInput.EndInput();
        Check(!autoStop.Listening && autoStop.Finishing && autoInput.Finishes == 0,
            "the recorder ending input transitions to local transcription without reopening it");
        autoInput.Result("Check the meeting settings", 0.9f);
        autoInput.Complete();
        Check(!autoStop.Finishing, "automatic input-end transcription completes");

        var brokenInput = new FakeRecognizer { StartFailure = new UnauthorizedAccessException() };
        using var failure = new SpeechService(() => brokenInput);
        failure.Toggle();
        string error = failure.Status;
        failure.Stop();
        failure.FinishListening();
        Check(!failure.Listening && brokenInput.Disposals == 1 && failure.Status == error
            && error.Contains("blocked", StringComparison.Ordinal), "input errors survive Stop");

        foreach (bool disposalFailure in new[] { false, true })
        {
            var failedInput = new FakeRecognizer
            { DisposeFailure = disposalFailure ? new IOException("Synthetic private cleanup detail.") : null };
            using var failedSpeech = new SpeechService(() => failedInput);
            failedSpeech.Toggle();
            failedInput.Complete(disposalFailure ? null : new InvalidOperationException("Synthetic private transcription detail."));
            Check(!failedSpeech.Busy && failedInput.Disposals == 1
                && failedSpeech.Status.StartsWith("Local dictation failed", StringComparison.Ordinal)
                && !failedSpeech.Status.Contains("private", StringComparison.Ordinal),
                "acknowledged input is released on transcription/cleanup failure without faulting callbacks or claiming success");
        }

        var stuckInput = new FakeRecognizer();
        using var stuck = new SpeechService(() => stuckInput);
        stuck.Toggle();
        stuckInput.Complete(new MicrophoneStopUnconfirmedException());
        stuck.Toggle();
        Check(stuck.InputStopUnconfirmed && stuckInput.Starts == 1
            && stuck.Status.Contains("Close MSGuide", StringComparison.Ordinal),
            "unknown microphone stop prevents a second recording and gives explicit recovery");

        var pendingInput = new FakeRecognizer { AcknowledgeCancellation = false };
        var replacementInput = new FakeRecognizer();
        var pendingInputs = new Queue<IDictationRecognizer>([pendingInput, replacementInput]);
        using var pending = new SpeechService(pendingInputs.Dequeue);
        var cancelledWords = new List<string>();
        pending.Transcribed += cancelledWords.Add;
        pending.Toggle();
        pending.StopListening();
        Check(pending.Stopping && pending.Busy && !pending.Status.Contains("Microphone off", StringComparison.Ordinal)
            && pendingInput.Disposals == 0, "cancel waits for hardware acknowledgement without claiming input off");
        pending.Toggle();
        bool changeRejected = false;
        try { pending.SelectInput(MicrophoneChoice.Default); }
        catch (InvalidOperationException) { changeRejected = true; }
        Check(changeRejected && pendingInputs.Count == 1, "pending closure gates recording and device changes");
        pending.InputStopTimedOut();
        pendingInput.Result("cancelled transcript", 1);
        pending.Toggle();
        Check(pending.InputStopUnconfirmed && pending.Busy && cancelledWords.Count == 0
            && pendingInputs.Count == 1, "stop timeout is retained independently of cancelled transcript callbacks");
        pendingInput.EndInput();
        Check(!pending.Busy && !pending.InputStopUnconfirmed && pendingInput.Disposals == 1,
            "late hardware acknowledgement clears only the input closure gate");
        pending.Toggle();
        pendingInput.Result("stale previous recording", 1);
        pendingInput.Complete();
        Check(replacementInput.Starts == 1 && pending.Listening && cancelledWords.Count == 0,
            "new recording after acknowledged closure rejects old session callbacks");
        pending.Stop();

        var wave = new WhisperTests.MemoryWaveIn { NotifyStopped = false };
        int whisperStarts = 0, unexpectedTranscriptions = 0;
        WhisperDictationRecognizer? whisper = null;
        using (var hardware = new SpeechService(() =>
        {
            whisperStarts++;
            return whisper = new WhisperDictationRecognizer(() => wave, (_, _) =>
            {
                unexpectedTranscriptions++;
                return Task.FromResult<IReadOnlyList<WhisperSegment>>([]);
            });
        }))
        {
            hardware.Toggle();
            hardware.StopListening();
            await Task.Delay(TimeSpan.FromMilliseconds(3400));
            hardware.Toggle();
            Check(wave.Active && hardware.InputStopUnconfirmed && whisperStarts == 1
                && !wave.Disposed && hardware.Status.Contains("not confirmed", StringComparison.Ordinal),
                "actual Whisper adapter cancel timeout retains the unacknowledged fake hardware input");
            wave.AcknowledgeStopped();
            await whisper!.Cleanup.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!hardware.Busy && !wave.Active && wave.Disposed && unexpectedTranscriptions == 0,
                "late actual-adapter acknowledgement closes input without reviving cancelled inference");
        }

        var disposingInput = new FakeRecognizer { AcknowledgeCancellation = false };
        var disposing = new SpeechService(() => disposingInput);
        disposing.Toggle();
        disposing.Dispose();
        Check(disposing.Stopping && disposingInput.Disposals == 0, "disposal does not report unacknowledged hardware off");
        disposingInput.EndInput();
        Check(!disposing.Busy && disposingInput.Disposals == 1, "disposal retains hardware completion handling");

        var silentInput = new FakeRecognizer();
        var rejectedInput = new FakeRecognizer();
        var inputs = new Queue<IDictationRecognizer>([silentInput, rejectedInput]);
        using var silence = new SpeechService(inputs.Dequeue);
        silence.Toggle();
        silentInput.Complete();
        Check(silence.Status.Contains("No audio", StringComparison.Ordinal), "silence has actionable diagnostics");
        silence.Toggle();
        rejectedInput.Level(30);
        rejectedInput.Reject();
        Check(silence.Status.Contains("not recognized", StringComparison.Ordinal), "rejected speech is not silently dropped");
        silence.Stop();

        var delayedInput = new FakeRecognizer();
        using var delayed = new SpeechService(() => delayedInput);
        var delayedText = new List<string>();
        delayed.Transcribed += delayedText.Add;
        delayed.Toggle();
        // Keep the dispatcher blocked until the old result is queued, then revoke it.
        Task.Run(() => delayedInput.Result("stale queued text", 0.9f)).GetAwaiter().GetResult();
        delayed.Stop();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(delayedText.Count == 0, "queued result is discarded after cancellation");

        var finalInput = new FakeRecognizer();
        using var final = new SpeechService(() => finalInput);
        var finalText = new List<string>();
        final.Transcribed += finalText.Add;
        final.Toggle();
        final.FinishListening();
        Task.Run(() =>
        {
            finalInput.Result("final queued text", 0.8f);
            finalInput.Complete();
        }).GetAwaiter().GetResult();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(finalText.SequenceEqual(["final queued text"]) && !final.Finishing,
            "queued final result survives normal Stop and reaches the UI before completion");

        var uiInputs = Enumerable.Range(0, 5).Select(_ => new FakeRecognizer()).ToArray();
        var uiQueue = new Queue<IDictationRecognizer>(uiInputs);
        var ui = new MainWindow(new SpeechService(uiQueue.Dequeue));
        try { ui.CheckSpeechComposer(uiInputs); }
        finally { ui.Close(); }
        var compactInputs = Enumerable.Range(0, 5).Select(_ => new FakeRecognizer()).ToArray();
        var compactQueue = new Queue<IDictationRecognizer>(compactInputs);
        var compactUi = new MainWindow(new SpeechService(compactQueue.Dequeue), new CompanionPosition());
        try { compactUi.CheckCompactSpeechComposer(compactInputs); }
        finally { compactUi.Close(); }
        var pendingUiInput = new FakeRecognizer { AcknowledgeCancellation = false };
        var pendingUi = new MainWindow(new SpeechService(() => pendingUiInput));
        try { pendingUi.CheckPendingSpeechControls(pendingUiInput); }
        finally { pendingUi.Close(); }
        foreach (string replacement in new[] { "none", "new-task", "new-status" })
        {
            var pausedInput = new FakeRecognizer { AcknowledgeCancellation = false };
            var pausedUi = new MainWindow(new SpeechService(() => pausedInput));
            try { pausedUi.CheckPausedSpeechStatus(pausedInput, replacement); }
            finally { pausedUi.Close(); }
        }
    }

    private static void CheckVoiceControls()
    {
        var pendingInput = new FakeRecognizer { AcknowledgeCancellation = false };
        var processingInput = new FakeRecognizer();
        var uncertainInput = new FakeRecognizer();
        var blockedInput = new FakeRecognizer { StartFailure = new UnauthorizedAccessException() };
        var queue = new Queue<IDictationRecognizer>([pendingInput, processingInput, uncertainInput, blockedInput]);
        using var speech = new SpeechService(queue.Dequeue);
        int records = 0, appends = 0, cancellations = 0, refreshes = 0, selections = 0;
        var words = new List<string>();
        speech.Transcribed += words.Add;
        var voice = new CompanionVoiceControls(new CompanionVoiceActions(
            () => { records++; speech.Toggle(); },
            () => { appends++; speech.Toggle(); },
            () => { cancellations++; speech.Stop(); },
            () => refreshes++,
            input => { selections++; speech.SelectInput(input); },
            () => speech.Stop()));
        var otherInput = new MicrophoneChoice(91, "Synthetic voice control input");
        var choices = new[] { MicrophoneChoice.Default, otherInput };
        void Update(bool hasDraft = true, bool hasInput = true) =>
            voice.Update(SpeechControlsState.From(speech, hasDraft, hasInput),
                hasInput ? choices : [], hasInput ? speech.Input : null, speech.Status, "", 0);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var options = voice.Children.OfType<Expander>().Single();
        var optionContent = (StackPanel)options.Content;
        Check(!voice.RecordButton.IsEnabled && queue.Count == 4,
            "constructing voice controls neither records nor assumes an input is available");
        Update(hasInput: false);
        Click(voice.RecordButton);
        Click(voice.AppendButton);
        Check(records == 0 && appends == 0 && queue.Count == 4
            && !voice.RecordButton.IsEnabled && !voice.AppendButton.IsEnabled,
            "missing input gates both voice entry points, including invoked disabled controls");
        Update(hasDraft: false);
        var icon = voice.RecordButton.Content as System.Windows.Shapes.Path;
        var microphoneIcon = icon?.Data;
        Check(icon is not null && microphoneIcon is not null
            && BindingOperations.GetBinding(icon, Shape.StrokeProperty)?.Source == voice.RecordButton
            && voice.RecordButton.MinWidth >= 44 && voice.RecordButton.MinHeight >= 44
            && voice.RecordButton.IsTabStop && voice.RecordButton.Focusable
            && voice.RecordButton.Style == voice.FindResource("PrimaryButtonStyle")
            && AutomationProperties.GetName(voice.RecordButton).Contains("Record a new voice question", StringComparison.Ordinal)
            && voice.AppendButton.Visibility == Visibility.Collapsed
            && optionContent.Children.Contains(voice.StopButton) && !options.IsExpanded
            && voice.StatusText.Text == "MIC OFF",
            "idle voice has a themed keyboard-accessible mic hit target without redundant Stop audio or status clutter");
        options.IsExpanded = true;
        voice.InputPicker.SelectedItem = otherInput;
        Click(voice.RefreshButton);
        options.IsExpanded = false;
        Update();
        Check(selections == 1 && refreshes == 1 && records == 0 && appends == 0 && queue.Count == 4
            && voice.AppendButton.Visibility == Visibility.Visible
            && ((string)voice.RecordButton.ToolTip).Contains("Replace the current draft only after recognition succeeds", StringComparison.Ordinal)
            && ((string)voice.AppendButton.ToolTip).Contains("append them to the current draft", StringComparison.Ordinal)
            && optionContent.Children.OfType<TextBlock>().Any(text => text.Text.Contains("Audio stays on this device", StringComparison.Ordinal)),
            "options, refresh, input selection and draft synchronization never opt into recording; replace and append are distinct");
        Click(voice.AppendButton);
        Update();
        var stopIcon = icon!.Data;
        Check(appends == 1 && pendingInput.Starts == 1 && speech.Listening && stopIcon != microphoneIcon
            && voice.RecordButton.IsEnabled && !voice.InputPicker.IsEnabled && !voice.RefreshButton.IsEnabled
            && voice.AppendButton.Visibility == Visibility.Collapsed && voice.InputLevel.Visibility == Visibility.Visible
            && AutomationProperties.GetName(voice.RecordButton) == "Stop recording and transcribe",
            "explicit append starts recording and the primary icon becomes stop-and-transcribe with input gating");
        Click(voice.RecordButton);
        Update();
        var processingIcon = icon.Data;
        Check(speech.Stopping && speech.Finishing && processingIcon != stopIcon && processingIcon != microphoneIcon
            && !voice.RecordButton.IsEnabled && !optionContent.Children.Contains(voice.StopButton)
            && voice.StopButton.IsEnabled && voice.StopButton.IsTabStop
            && AutomationProperties.GetName(voice.RecordButton).StartsWith("Stopping microphone", StringComparison.Ordinal)
            && AutomationProperties.GetName(voice.StopButton) == "Cancel local transcription",
            "stopping exposes cancellation immediately outside collapsed options and never advertises recording");
        Click(voice.RecordButton);
        Click(voice.AppendButton);
        Click(voice.RefreshButton);
        Check(records == 1 && appends == 1 && refreshes == 1 && queue.Count == 3,
            "pending microphone closure gates primary, append and device refresh callbacks");
        Click(voice.StopButton);
        speech.InputStopTimedOut();
        Update();
        Check(cancellations == 1 && speech.InputStopUnconfirmed && icon.Data != processingIcon
            && !voice.RecordButton.IsEnabled && !voice.InputPicker.IsEnabled
            && !optionContent.Children.Contains(voice.StopButton) && voice.StopButton.IsEnabled
            && AutomationProperties.GetName(voice.RecordButton).StartsWith("Microphone status unknown", StringComparison.Ordinal)
            && voice.StatusText.Text.Contains("MIC STATUS UNKNOWN", StringComparison.Ordinal)
            && voice.StatusText.Text.Contains("Close MSGuide", StringComparison.Ordinal),
            "unknown closure keeps a warning icon, explicit recovery and immediate audio stop without enabling another recording");
        pendingInput.EndInput();
        Update();
        Click(voice.RecordButton);
        Click(voice.RecordButton);
        processingInput.EndInput();
        Update();
        Check(speech.Finishing && !speech.Stopping && icon.Data == processingIcon
            && AutomationProperties.GetName(voice.RecordButton).StartsWith("Transcribing locally", StringComparison.Ordinal)
            && (string)voice.StopButton.Content == "Cancel" && !optionContent.Children.Contains(voice.StopButton),
            "confirmed microphone closure changes processing semantics without hiding cancellation");
        Click(voice.StopButton);
        processingInput.Result("cancelled words", 1);
        Update();
        Check(!speech.Busy && processingInput.Cancels == 1 && words.Count == 0 && icon.Data == microphoneIcon
            && optionContent.Children.Contains(voice.StopButton),
            "cancelling local processing rejects late words and restores the idle mic without restarting");
        Click(voice.RecordButton);
        uncertainInput.Result("Review these words", 0.2f);
        uncertainInput.Complete();
        Update();
        Check(words.SequenceEqual(["Review these words"]) && !speech.Busy && icon.Data == microphoneIcon
            && voice.StatusText.Text.Contains("Uncertain transcript", StringComparison.Ordinal)
            && AutomationProperties.GetHelpText(voice.StatusText).Contains(speech.Status, StringComparison.Ordinal),
            "completed uncertain text stays visibly marked for review rather than being hidden by concise status");
        Click(voice.RecordButton);
        Update();
        Check(!speech.Busy && blockedInput.Starts == 1 && icon.Data != microphoneIcon
            && voice.RecordButton.IsEnabled && queue.Count == 0
            && AutomationProperties.GetName(voice.RecordButton).StartsWith("Retry microphone", StringComparison.Ordinal)
            && voice.StatusText.Text.Contains("Microphone access is blocked.", StringComparison.Ordinal),
            "input failure shows an accessible retry state and full recovery text without automatically retrying");
    }

    public static async Task RunSyntheticAsync()
    {
        using var audio = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToWaveStream(audio);
            synth.Speak("My camera is not working in Microsoft Teams.");
            synth.SetOutputToNull();
        }
        audio.Position = 0;
        using var speech = new SpeechService(() => new WindowsDictationRecognizer(audio));
        int characters = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Transcribed += text => characters += text.Length;
        speech.StatusChanged += _ =>
        {
            if (!speech.Listening && !speech.Finishing) done.TrySetResult();
        };
        speech.Toggle();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(characters > 0 && !speech.Listening && !speech.Finishing,
            "actual Windows dictation produces text from memory without microphone or speakers");
    }

    internal sealed class FakeRecognizer : IDictationRecognizer
    {
        public string CultureName => "en-US";
        public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(2);
        public string FinishDescription => "Finishing the last phrase.";
        public int Starts, Finishes, Cancels, Disposals;
        public Exception? StartFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public bool AcknowledgeCancellation { get; init; } = true;
        public event Action<string, float>? Recognized;
        public event Action<string>? Hypothesized;
        public event Action<int>? AudioLevel;
        public event Action<string>? SignalProblem;
        public event Action? Rejected;
        public event Action<Exception?>? Completed;
        public event Action<Exception?>? InputStopped;
        public void Start()
        {
            Starts++;
            if (StartFailure is not null)
            {
                InputStopped?.Invoke(null);
                throw StartFailure;
            }
        }
        public void Finish() => Finishes++;
        public void Cancel()
        {
            Cancels++;
            if (AcknowledgeCancellation) InputStopped?.Invoke(null);
        }
        public void Dispose()
        {
            Disposals++;
            if (DisposeFailure is not null) throw DisposeFailure;
        }
        public void Result(string text, float confidence) => Recognized?.Invoke(text, confidence);
        public void Preview(string text) => Hypothesized?.Invoke(text);
        public void Level(int value) => AudioLevel?.Invoke(value);
        public void Problem(string value) => SignalProblem?.Invoke(value);
        public void Reject() => Rejected?.Invoke();
        public void Complete(Exception? error = null)
        {
            InputStopped?.Invoke(error is MicrophoneStopUnconfirmedException ? error : null);
            Completed?.Invoke(error);
        }
        public void EndInput() => InputStopped?.Invoke(null);
    }
}

public partial class MainWindow
{
    internal void CheckCompactSpeechComposer(SpeechTests.FakeRecognizer[] inputs)
    {
        loaded = true;
        try
        {
            var compact = companion.Prompt;
            var synthetic = new MicrophoneChoice(91, "Synthetic test input");
            MicrophonePicker.ItemsSource = new[] { MicrophoneChoice.Default, synthetic };
            MicrophonePicker.SelectedItem = MicrophoneChoice.Default;
            UpdatePromptSubmissionUi();
            compact.Voice.InputPicker.SelectedItem = synthetic;
            IntegrationTests.Require(speech.Input == synthetic && Equals(MicrophonePicker.SelectedItem, synthetic)
                && inputs.All(input => input.Starts == 0) && !compact.CanSubmit);
            compact.Voice.RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inputs[0].Level(23);
            inputs[0].Preview("Synthetic preview");
            IntegrationTests.Require(speech.Listening && inputs[0].Starts == 1
                && !compact.CanSubmit && !compact.Voice.InputPicker.IsEnabled
                && compact.Voice.InputLevel.Value == 23
                && compact.Voice.PreviewText.Text.Contains("Synthetic preview"));
            compact.SubmitAsync().GetAwaiter().GetResult();
            IntegrationTests.Require(speech.Listening && inputs[0].Finishes == 0 && cameraRecovery.CanStart);
            compact.Voice.RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inputs[0].Result("Check my Teams camera", 0.2f);
            inputs[0].Complete();
            IntegrationTests.Require(PromptBox.Text == "Check my Teams camera"
                && compact.DraftControl.Text == PromptBox.Text && compact.CanSubmit
                && compact.Voice.StatusText.Text.Contains("MIC OFF")
                && compact.Voice.StatusText.Text.Contains("Uncertain transcript")
                && AutomationProperties.GetName(compact.Voice.RecordButton).Contains("replace the draft")
                && compact.Voice.AppendButton.Visibility == Visibility.Visible && cameraRecovery.CanStart);
            compact.Voice.AppendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inputs[1].Result("please", 0.9f);
            inputs[1].Complete();
            IntegrationTests.Require(PromptBox.Text == "Check my Teams camera please"
                && compact.DraftControl.Text == PromptBox.Text);
            compact.Voice.RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            compact.DraftControl.Text = "Typed in the compact prompt";
            inputs[2].Result("late discarded words", 1);
            inputs[2].Complete();
            IntegrationTests.Require(PromptBox.Text == "Typed in the compact prompt"
                && inputs[2].Cancels == 1 && !speech.Busy);
            compact.Voice.RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            compact.Voice.RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(!compact.CanSubmit && !compact.Voice.RecordButton.IsEnabled);
            compact.Voice.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inputs[3].Result("cancelled transcription", 1);
            IntegrationTests.Require(compact.DraftControl.Text == "Typed in the compact prompt"
                && !speech.Busy && inputs[3].Cancels == 1);
            compact.Voice.RecordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            compact.DismissPrompt();
            inputs[4].Result("hidden transcription", 1);
            IntegrationTests.Require(!speech.Busy && inputs[4].Cancels == 1
                && compact.DraftControl.Text == PromptBox.Text && cameraRecovery.CanStart);
        }
        finally { loaded = false; }
    }

    internal void CheckPausedSpeechStatus(SpeechTests.FakeRecognizer input, string replacement)
    {
        loaded = true;
        try
        {
            PromptBox.Text = "Synthetic draft before Pause";
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pause();
            IntegrationTests.Require(speech.Busy && speech.Stopping
                && MicrophoneStateText.Text == "MIC STOPPING"
                && StatusText.Text.Contains("shutdown pending", StringComparison.Ordinal)
                && !StatusText.Text.Contains("microphone off", StringComparison.Ordinal)
                && !MicButton.IsEnabled && !MicrophonePicker.IsEnabled && PromptBox.Text.Length == 0);
            input.Result("Cancelled transcript must stay discarded", 1);
            speech.InputStopTimedOut();
            IntegrationTests.Require(speech.InputStopUnconfirmed && speech.Busy
                && MicrophoneStateText.Text == "MIC STATUS UNKNOWN"
                && StatusText.Text.Contains("microphone status unknown", StringComparison.Ordinal)
                && !MicButton.IsEnabled && !MicrophonePicker.IsEnabled && PromptBox.Text.Length == 0);
            long pauseGeneration = generation;
            if (replacement == "new-task") PromptBox.Text = "A newer synthetic task";
            if (replacement == "new-status") StatusText.Text = "A newer status message";
            string newerStatus = StatusText.Text;
            input.EndInput();
            input.Result("Stale late transcript", 1);
            IntegrationTests.Require(!speech.Busy && MicrophoneStateText.Text == "MIC OFF"
                && MicButton.IsEnabled && MicrophonePicker.IsEnabled);
            if (replacement == "none")
                IntegrationTests.Require(StatusText.Text == "Paused · microphone off · no execution authority."
                    && PromptBox.Text.Length == 0);
            else
                IntegrationTests.Require(StatusText.Text == newerStatus
                    && (replacement == "new-task" ? generation != pauseGeneration && PromptBox.Text == "A newer synthetic task"
                        : generation == pauseGeneration && PromptBox.Text.Length == 0));
        }
        finally { loaded = false; }
    }

    internal void CheckPendingSpeechControls(SpeechTests.FakeRecognizer input)
    {
        loaded = true;
        try
        {
            PromptBox.Text = "Retained typed draft";
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StopAudioButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IntegrationTests.Require(MicrophoneStateText.Text == "MIC STOPPING"
                && !MicrophonePicker.IsEnabled && !MicButton.IsEnabled
                && !companion.Prompt.Voice.RecordButton.IsEnabled
                && companion.Prompt.Voice.StatusText.Text.Contains("MIC STOPPING"));
            StopAudioButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            speech.InputStopTimedOut();
            input.Result("Cancelled replacement", 1);
            IntegrationTests.Require(MicrophoneStateText.Text == "MIC STATUS UNKNOWN"
                && !MicrophonePicker.IsEnabled && !RefreshMicrophonesButton.IsEnabled
                && !MicButton.IsEnabled && !AddVoiceButton.IsEnabled && !AskPromptButton.IsEnabled
                && PromptBox.Text == "Retained typed draft"
                && !companion.Prompt.CanSubmit && !companion.Prompt.Voice.InputPicker.IsEnabled
                && companion.Prompt.Voice.StatusText.Text.Contains("MIC STATUS UNKNOWN"));
            input.EndInput();
            IntegrationTests.Require(MicrophoneStateText.Text == "MIC OFF"
                && MicrophonePicker.IsEnabled && MicButton.IsEnabled && AskPromptButton.IsEnabled
                && PromptBox.Text == "Retained typed draft"
                && companion.Prompt.CanSubmit && companion.Prompt.Voice.InputPicker.IsEnabled);
        }
        finally { loaded = false; }
    }

    internal void CheckSpeechComposer(SpeechTests.FakeRecognizer[] inputs)
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Speech composer regression failed: {name}.");
        }
        loaded = true;
        try
        {
            var input = inputs[0];
            PromptBox.Text = "";
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            input.Level(14);
            input.Problem("TooSoft");
            input.Preview("check my camera");
            Check(speech.Listening && MicrophoneStateText.Text == "MIC ON"
                && SpeechInputPanel.Visibility == Visibility.Visible && SpeechInputLevel.Value == 14
                && SpeechText.Text.Contains("too soft", StringComparison.Ordinal)
                && SpeechPreviewText.Visibility == Visibility.Visible,
                "microphone state, level, and provisional words are displayed");
            input.Result("Check my Teams camera", 0.2f);
            Check(PromptBox.Text == "Check my Teams camera" && !AskPromptButton.IsEnabled
                && cameraRecovery.CanStart, "uncertain speech edits only a draft");
            StopAudioButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SubmitPromptAsync().GetAwaiter().GetResult();
            Check(speech.Finishing && !AskPromptButton.IsEnabled && !MicButton.IsEnabled
                && input.Finishes == 1 && cameraRecovery.CanStart,
                "Enter while listening finishes dictation without submitting");
            input.Result("please", 0.9f);
            input.Complete();
            Check(PromptBox.Text == "Check my Teams camera please" && AskPromptButton.IsEnabled
                && cameraRecovery.CanStart && input.Finishes == 1 && input.Cancels == 0
                && MicrophoneStateText.Text == "MIC OFF" && MicButton.IsEnabled
                && SpeechText.Text.Contains("Uncertain transcript", StringComparison.Ordinal),
                "final transcript survives Stop audio and remains unsubmitted");
            string status = SpeechText.Text;
            StopAudioButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(SpeechText.Text == status, "repeated Stop audio preserves diagnostics");

            string original = PromptBox.Text;
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(PromptBox.Text == original, "new recording preserves the draft until it succeeds");
            inputs[1].Result("Open the meeting settings", 0.9f);
            inputs[1].Complete();
            Check(PromptBox.Text == "Open the meeting settings" && AddVoiceButton.Visibility == Visibility.Visible,
                "new voice question replaces instead of appending");
            AddVoiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inputs[2].Result("and check audio", 0.9f);
            inputs[2].Complete();
            Check(PromptBox.Text == "Open the meeting settings and check audio",
                "Add more explicitly appends to the current draft");
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PromptBox.Text = "Typed while recording";
            inputs[3].Result("stale voice words", 0.9f);
            inputs[3].Complete();
            Check(PromptBox.Text == "Typed while recording" && !speech.Listening && inputs[3].Cancels == 1,
                "typing cancels dictation so late speech cannot overwrite edits");
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StopAudioButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inputs[4].Result("cancelled replacement", 0.9f);
            Check(PromptBox.Text == "Typed while recording" && !speech.Finishing,
                "cancelled replacement preserves the existing draft");
        }
        finally { loaded = false; }
    }
}
