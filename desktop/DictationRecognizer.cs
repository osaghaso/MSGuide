using System.Globalization;
using System.IO;
using System.Speech.Recognition;

namespace MSGuide.Desktop;

internal interface IDictationRecognizer : IDisposable
{
    string CultureName { get; }
    TimeSpan FinishTimeout { get; }
    string FinishDescription { get; }
    event Action<string, float>? Recognized;
    event Action<string>? Hypothesized;
    event Action<int>? AudioLevel;
    event Action<string>? SignalProblem;
    event Action? Rejected;
    event Action<Exception?>? Completed;
    event Action<Exception?>? InputStopped;
    void Start();
    void Finish();
    void Cancel();
}

internal sealed class WindowsDictationRecognizer : IDictationRecognizer
{
    private readonly SpeechRecognitionEngine engine;
    private readonly Stream? syntheticInput;
    public string CultureName { get; }
    public TimeSpan FinishTimeout => TimeSpan.FromSeconds(2);
    public string FinishDescription => "Finishing the last phrase (up to 2 seconds). Review the transcript before asking.";
    public event Action<string, float>? Recognized;
    public event Action<string>? Hypothesized;
    public event Action<int>? AudioLevel;
    public event Action<string>? SignalProblem;
    public event Action? Rejected;
    public event Action<Exception?>? Completed;
    public event Action<Exception?>? InputStopped;
    private bool disposed;
    private int inputClosureRequested;

    internal WindowsDictationRecognizer(Stream? syntheticInput = null)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0) throw new InvalidOperationException("No installed Windows speech recognizer.");
        var info = installed.FirstOrDefault(item => item.Culture.Equals(CultureInfo.CurrentUICulture)) ?? installed[0];
        engine = new SpeechRecognitionEngine(info);
        CultureName = info.Culture.Name;
        this.syntheticInput = syntheticInput;
        engine.SpeechRecognized += (_, e) => Recognized?.Invoke(e.Result.Text, e.Result.Confidence);
        engine.SpeechHypothesized += (_, e) => Hypothesized?.Invoke(e.Result.Text);
        engine.AudioLevelUpdated += (_, e) => AudioLevel?.Invoke(e.AudioLevel);
        engine.AudioSignalProblemOccurred += (_, e) => SignalProblem?.Invoke(e.AudioSignalProblem.ToString());
        engine.SpeechRecognitionRejected += (_, _) => Rejected?.Invoke();
        engine.RecognizeCompleted += (_, e) =>
        {
            if (Interlocked.Exchange(ref inputClosureRequested, 1) != 0) return;
            _ = Task.Run(() =>
            {
                try { engine.SetInputToNull(); }
                catch (Exception error) when (error is InvalidOperationException
                    or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Record("microphone_input_close_failed", new { errorType = error.GetType().Name });
                    InputStopped?.Invoke(new MicrophoneStopUnconfirmedException());
                    return;
                }
                InputStopped?.Invoke(null);
                Completed?.Invoke(e.Error);
            });
        };
    }

    public void Start()
    {
        try
        {
            engine.LoadGrammar(new DictationGrammar());
            if (syntheticInput is null) engine.SetInputToDefaultAudioDevice();
            else engine.SetInputToWaveStream(syntheticInput);
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            Dispose();
            InputStopped?.Invoke(null);
            throw;
        }
    }

    public void Finish() => engine.RecognizeAsyncStop();
    public void Cancel() => engine.RecognizeAsyncCancel();
    public void Dispose()
    {
        if (disposed) return;
        engine.Dispose();
        disposed = true;
    }
}
