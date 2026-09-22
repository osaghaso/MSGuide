using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows.Threading;

namespace MSGuide.Desktop;

public sealed class SpeechService : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Func<IDictationRecognizer> createRecognizer;
    private readonly DispatcherTimer finishTimer;
    private readonly DispatcherTimer inputStopTimer;
    private IDictationRecognizer? recognizer;
    private SpeechSynthesizer? synthesizer;
    private int phrases, peakLevel;
    private bool uncertain, acceptTranscript, inputClosed, disposed;
    private string cancelReason = "";
    internal MicrophoneChoice Input { get; private set; } = MicrophoneChoice.Default;
    public bool Listening { get; private set; }
    public bool Finishing { get; private set; }
    public bool Stopping { get; private set; }
    public bool InputStopUnconfirmed { get; private set; }
    public bool Busy => Listening || Finishing || Stopping || InputStopUnconfirmed;
    public string Status { get; private set; } = "Microphone off. Nothing is listening.";
    public event Action<string>? Transcribed;
    public event Action<string>? StatusChanged;
    public event Action<string>? PreviewChanged;
    public event Action<int>? AudioLevelChanged;

    public SpeechService() : this(null) { }

    internal SpeechService(Func<IDictationRecognizer>? createRecognizer)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.createRecognizer = createRecognizer ?? (() =>
            new WhisperDictationRecognizer(Input.DeviceNumber, Input.DeviceNumber < 0 ? null : Input.Name));
        finishTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        finishTimer.Tick += (_, _) => FinishTimedOut();
        inputStopTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        { Interval = TimeSpan.FromSeconds(3) };
        inputStopTimer.Tick += (_, _) => InputStopTimedOut();
    }

    internal void SelectInput(MicrophoneChoice input)
    {
        dispatcher.VerifyAccess();
        if (Busy)
            throw new InvalidOperationException("Stop dictation before changing the microphone.");
        Input = input;
    }

    public void Toggle()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InputStopUnconfirmed)
        {
            SetStatus(new MicrophoneStopUnconfirmedException().Message);
            return;
        }
        if (Listening) { FinishListening(); return; }
        if (Finishing || Stopping || recognizer is not null) return;
        StopSpeaking();
        phrases = peakLevel = 0;
        uncertain = false;
        PreviewChanged?.Invoke("");
        AudioLevelChanged?.Invoke(0);
        try
        {
            var current = createRecognizer();
            if (current.FinishTimeout <= TimeSpan.Zero || current.FinishTimeout > TimeSpan.FromSeconds(45))
            {
                current.Dispose();
                throw new InvalidOperationException("Speech finalization timeout must be bounded to 45 seconds.");
            }
            recognizer = current;
            acceptTranscript = true;
            inputClosed = false;
            cancelReason = "";
            current.Recognized += (text, confidence) => Dispatch(current, () =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetStatus("Speech was detected but no words were recognized. Check your input level or type the question.");
                    return;
                }
                phrases++;
                uncertain |= confidence < 0.45;
                Transcribed?.Invoke(text);
                PreviewChanged?.Invoke("");
                SetStatus(uncertain
                    ? "Uncertain transcript added. Review and correct the words before asking."
                    : Finishing ? "Transcribing locally..." : "Transcript added. Keep speaking or stop and review.");
            });
            current.Hypothesized += text => Dispatch(current, () => PreviewChanged?.Invoke(text));
            current.AudioLevel += level => Dispatch(current, () =>
            {
                peakLevel = Math.Max(peakLevel, level);
                AudioLevelChanged?.Invoke(Math.Clamp(level, 0, 100));
            });
            current.SignalProblem += problem => Dispatch(current, () => SetStatus(problem switch
            {
                "TooSoft" => "Microphone signal is too soft. Speak closer or select a different input in Sound settings.",
                "TooLoud" => "Microphone signal is too loud. Lower the input level in Sound settings.",
                _ => "Audio is unclear. Check the microphone and input level in Sound settings."
            }));
            current.Rejected += () => Dispatch(current, () =>
                SetStatus("Audio was heard, but words were not recognized. Speak clearly or check the input in Sound settings."));
            current.Completed += error => Dispatch(current, () => CompleteListening(error), transcript: false);
            current.InputStopped += error => Dispatch(current, () => InputStopped(error), transcript: false);
            Listening = true;
            SetStatus($"Recording locally ({current.CultureName}) using {Input}. Stop to transcribe; auto-stop after 30 seconds.");
            current.Start();
        }
        catch (Exception ex) when (IsSpeechFailure(ex))
        {
            if (recognizer is null) SetStatus(InputFailure(ex));
            else CancelListening(InputFailure(ex));
        }
    }

    private void Dispatch(IDictationRecognizer source, Action callback, bool transcript = true)
    {
        void Deliver()
        {
            if (ReferenceEquals(recognizer, source) && (!transcript || acceptTranscript)) callback();
        }
        if (dispatcher.CheckAccess()) Deliver();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke((Action)Deliver);
    }

    public void FinishListening()
    {
        dispatcher.VerifyAccess();
        if (!Listening || Finishing || recognizer is null) return;
        BeginFinishing();
        try { recognizer.Finish(); }
        catch (Exception ex) when (IsSpeechFailure(ex))
        {
            CancelListening(InputFailure(ex));
        }
    }

    private void BeginFinishing()
    {
        if (recognizer is null || Finishing) return;
        Listening = false;
        Finishing = true;
        Stopping = !inputClosed;
        finishTimer.Interval = recognizer.FinishTimeout;
        SetStatus(Stopping ? "Stopping microphone; waiting for input closure before transcription."
            : recognizer.FinishDescription);
        finishTimer.Start();
        if (Stopping) inputStopTimer.Start();
    }

    internal void FinishTimedOut()
    {
        if (!Finishing) return;
        CancelListening("Local transcription timed out before the last phrase could finish. Review the retained transcript; nothing was submitted.");
    }

    internal void InputStopTimedOut()
    {
        if (recognizer is not null && !inputClosed) InputStopped(new MicrophoneStopUnconfirmedException());
    }

    private void InputStopped(Exception? error)
    {
        if (recognizer is null) return;
        inputStopTimer.Stop();
        Stopping = false;
        if (error is not null)
        {
            InputStopUnconfirmed = true;
            acceptTranscript = Listening = Finishing = false;
            finishTimer.Stop();
            PreviewChanged?.Invoke("");
            AudioLevelChanged?.Invoke(0);
            SetStatus(new MicrophoneStopUnconfirmedException().Message);
            return;
        }
        inputClosed = true;
        InputStopUnconfirmed = false;
        if (!acceptTranscript)
        {
            var cleanupError = ReleaseRecognizer();
            SetStatus(cleanupError is not null ? InputFailure(cleanupError)
                : "Microphone off. " + (cancelReason.Length > 0
                    ? cancelReason : "Input closure confirmed. Review the retained transcript."));
        }
        else if (!Finishing) BeginFinishing();
        else SetStatus(recognizer.FinishDescription);
    }

    private void CompleteListening(Exception? error)
    {
        if (!inputClosed)
        {
            InputStopped(new MicrophoneStopUnconfirmedException());
            return;
        }
        if (!acceptTranscript) return;
        var cleanupError = ReleaseRecognizer();
        error ??= cleanupError;
        SetStatus(error is not null ? InputFailure(error)
            : phrases > 0 ? uncertain
                ? "Microphone off. Uncertain transcript captured; review and correct it before asking."
                : "Microphone off. Review the transcript, then select Ask MSGuide."
            : peakLevel == 0
                ? "Microphone off. No audio reached the recognizer. Check the selected microphone and its mute switch."
                : "Microphone off. Audio was heard but no words were captured. Check input volume and recognizer language, or type your question.");
    }

    private Exception? ReleaseRecognizer()
    {
        finishTimer.Stop();
        inputStopTimer.Stop();
        var old = recognizer;
        recognizer = null;
        Listening = Finishing = Stopping = acceptTranscript = false;
        Exception? cleanupError = null;
        try { old?.Dispose(); }
        catch (Exception error) when (IsSpeechFailure(error))
        {
            cleanupError = error;
            InputStopUnconfirmed |= error is MicrophoneStopUnconfirmedException;
            DiagnosticLog.Record("speech_cleanup_failed", new { errorType = error.GetType().Name });
        }
        PreviewChanged?.Invoke("");
        AudioLevelChanged?.Invoke(0);
        return cleanupError;
    }

    public void StopListening()
    {
        dispatcher.VerifyAccess();
        CancelListening("Recording cancelled. Existing transcript retained; no further words will be added.");
    }

    private void CancelListening(string reason)
    {
        if (recognizer is null || !acceptTranscript) return;
        var current = recognizer;
        acceptTranscript = Listening = Finishing = false;
        cancelReason = reason;
        finishTimer.Stop();
        PreviewChanged?.Invoke("");
        AudioLevelChanged?.Invoke(0);
        Stopping = !inputClosed;
        if (Stopping)
        {
            inputStopTimer.Start();
            SetStatus("Stopping microphone; input closure is not yet confirmed. " + reason);
        }
        try
        {
            current.Cancel();
        }
        catch (Exception ex) when (IsSpeechFailure(ex))
        {
            if (!inputClosed) InputStopped(new MicrophoneStopUnconfirmedException());
            else cancelReason = InputFailure(ex);
        }
        if (inputClosed && ReferenceEquals(recognizer, current))
        {
            var cleanupError = ReleaseRecognizer();
            SetStatus(cleanupError is not null ? InputFailure(cleanupError) : "Microphone off. " + cancelReason);
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static bool IsSpeechFailure(Exception ex) =>
        ex is InvalidOperationException or COMException or NotSupportedException
            or UnauthorizedAccessException or IOException or FormatException
            or NAudio.MmException or DllNotFoundException or BadImageFormatException;

    private static string InputFailure(Exception ex) => ex is MicrophoneStopUnconfirmedException
        ? ex.Message
        : ex is UnauthorizedAccessException
            ? "Microphone access is blocked. Allow microphone access for desktop apps in Windows Settings, or type your question."
        : ex is FileNotFoundException
            ? "A local speech model or runtime is missing. See speech setup and run Install-MSGuideSpeechModel.ps1 -AcceptDownload only after approving the model download. Existing text is retained."
            : $"Local dictation failed ({ex.GetType().Name}, 0x{ex.HResult:X8}). Check the selected microphone and local speech model setup. Existing text is retained.";

    public void Speak(string text)
    {
        StopListening();
        if (Stopping || InputStopUnconfirmed) return;
        try
        {
            synthesizer ??= new SpeechSynthesizer();
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(text);
        }
        catch (Exception ex) when (IsSpeechFailure(ex))
        { SetStatus("Speech playback unavailable. The instruction remains on screen."); }
    }

    public void StopSpeaking() => synthesizer?.SpeakAsyncCancelAll();
    public void Stop() { StopListening(); StopSpeaking(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        synthesizer?.Dispose();
        synthesizer = null;
    }
}