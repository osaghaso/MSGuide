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
    private IDictationRecognizer? recognizer;
    private SpeechSynthesizer? synthesizer;
    private int phrases, peakLevel;
    private bool uncertain;
    internal MicrophoneChoice Input { get; private set; } = MicrophoneChoice.Default;
    public bool Listening { get; private set; }
    public bool Finishing { get; private set; }
    public bool InputStopUnconfirmed { get; private set; }
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
    }

    internal void SelectInput(MicrophoneChoice input)
    {
        dispatcher.VerifyAccess();
        if (Listening || Finishing || InputStopUnconfirmed)
            throw new InvalidOperationException("Stop dictation before changing the microphone.");
        Input = input;
    }

    public void Toggle()
    {
        dispatcher.VerifyAccess();
        if (InputStopUnconfirmed)
        {
            SetStatus(new MicrophoneStopUnconfirmedException().Message);
            return;
        }
        if (Listening) { FinishListening(); return; }
        if (Finishing) return;
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
            current.Completed += error => Dispatch(current, () => CompleteListening(error));
            current.InputEnded += () => Dispatch(current, BeginFinishing);
            Listening = true;
            SetStatus($"Recording locally ({current.CultureName}) using {Input}. Stop to transcribe; auto-stop after 30 seconds.");
            current.Start();
        }
        catch (Exception ex) when (IsSpeechFailure(ex))
        {
            ReleaseRecognizer();
            SetStatus(InputFailure(ex));
        }
    }

    private void Dispatch(IDictationRecognizer source, Action callback)
    {
        void Deliver()
        {
            if (ReferenceEquals(recognizer, source)) callback();
        }
        if (dispatcher.CheckAccess()) Deliver();
        else dispatcher.BeginInvoke((Action)Deliver);
    }

    public void FinishListening()
    {
        dispatcher.VerifyAccess();
        if (!Listening || Finishing || recognizer is null) return;
        BeginFinishing();
        try { recognizer.Finish(); }
        catch (Exception ex) when (IsSpeechFailure(ex))
        {
            ReleaseRecognizer();
            SetStatus(InputFailure(ex));
        }
    }

    private void BeginFinishing()
    {
        if (recognizer is null || Finishing) return;
        Listening = false;
        Finishing = true;
        finishTimer.Interval = recognizer.FinishTimeout;
        SetStatus(recognizer.FinishDescription);
        finishTimer.Start();
    }

    internal void FinishTimedOut()
    {
        if (!Finishing) return;
        StopListening();
        SetStatus("Local transcription timed out before the last phrase could finish. Review the retained transcript; nothing was submitted.");
    }

    private void CompleteListening(Exception? error)
    {
        InputStopUnconfirmed |= error is MicrophoneStopUnconfirmedException;
        ReleaseRecognizer();
        SetStatus(error is not null ? InputFailure(error)
            : phrases > 0 ? uncertain
                ? "Microphone off. Uncertain transcript captured; review and correct it before asking."
                : "Microphone off. Review the transcript, then select Ask MSGuide."
            : peakLevel == 0
                ? "Microphone off. No audio reached the recognizer. Check the selected microphone and its mute switch."
                : "Microphone off. Audio was heard but no words were captured. Check input volume and recognizer language, or type your question.");
    }

    private void ReleaseRecognizer()
    {
        finishTimer.Stop();
        var old = recognizer;
        recognizer = null;
        Listening = Finishing = false;
        old?.Dispose();
        PreviewChanged?.Invoke("");
        AudioLevelChanged?.Invoke(0);
    }

    public void StopListening()
    {
        dispatcher.VerifyAccess();
        if (recognizer is null) return;
        var current = recognizer;
        // Invalidate callbacks before cancellation so old audio cannot repopulate a cleared draft.
        recognizer = null;
        try
        {
            current.Cancel();
            SetStatus("Microphone stopped. Existing transcript retained; no further words will be added.");
        }
        catch (Exception ex) when (IsSpeechFailure(ex)) { SetStatus(InputFailure(ex)); }
        finally
        {
            current.Dispose();
            ReleaseRecognizer();
        }
        StatusChanged?.Invoke(Status);
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
    public void Dispose() { Stop(); synthesizer?.Dispose(); synthesizer = null; }
}