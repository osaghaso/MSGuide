using System.IO;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
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
                && !MicrophonePicker.IsEnabled && !MicButton.IsEnabled);
            StopAudioButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            speech.InputStopTimedOut();
            input.Result("Cancelled replacement", 1);
            IntegrationTests.Require(MicrophoneStateText.Text == "MIC STATUS UNKNOWN"
                && !MicrophonePicker.IsEnabled && !RefreshMicrophonesButton.IsEnabled
                && !MicButton.IsEnabled && !AddVoiceButton.IsEnabled && !AskPromptButton.IsEnabled
                && PromptBox.Text == "Retained typed draft");
            input.EndInput();
            IntegrationTests.Require(MicrophoneStateText.Text == "MIC OFF"
                && MicrophonePicker.IsEnabled && MicButton.IsEnabled && AskPromptButton.IsEnabled
                && PromptBox.Text == "Retained typed draft");
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
