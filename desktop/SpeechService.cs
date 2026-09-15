using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace MSGuide.Desktop;

public sealed class SpeechService : IDisposable
{
    private SpeechRecognitionEngine? recognizer;
    private SpeechSynthesizer? synthesizer;
    public bool Listening { get; private set; }
    public event Action<string>? Transcribed;
    public event Action<string>? StatusChanged;

    public void Toggle()
    {
        if (Listening) { StopListening(); return; }
        StopListening();
        StopSpeaking();
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed.Count == 0) throw new InvalidOperationException("No installed Windows speech recognizer.");
            var info = installed.FirstOrDefault(r => r.Culture.Equals(System.Globalization.CultureInfo.CurrentUICulture)) ?? installed[0];
            recognizer = new SpeechRecognitionEngine(info);
            recognizer.LoadGrammar(new DictationGrammar());
            recognizer.SetInputToDefaultAudioDevice();
            var current = recognizer;
            recognizer.SpeechRecognized += (_, e) =>
            {
                if (Listening && ReferenceEquals(recognizer, current) && e.Result.Confidence >= 0.45)
                    Transcribed?.Invoke(e.Result.Text);
            };
            recognizer.RecognizeCompleted += (_, e) =>
            {
                if (ReferenceEquals(recognizer, current))
                {
                    Listening = false;
                    StatusChanged?.Invoke(e.Error is null ? "Microphone off · edit the transcript before sending." : "Speech stopped unexpectedly. Type your question instead.");
                }
            };
            Listening = true;
            recognizer.RecognizeAsync(RecognizeMode.Multiple);
            StatusChanged?.Invoke($"● Listening locally ({info.Culture.Name}) · click Stop microphone. Auto-stop after 30 seconds.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.PlatformNotSupportedException)
        {
            StopListening();
            StatusChanged?.Invoke("Microphone/installed recognizer unavailable. Check Windows speech and microphone settings, or type your question.");
        }
    }

    public void StopListening()
    {
        Listening = false;
        var old = recognizer;
        recognizer = null;
        if (old is not null)
        {
            try { old.RecognizeAsyncCancel(); old.SetInputToNull(); }
            catch (InvalidOperationException) { }
            finally { old.Dispose(); }
        }
        StatusChanged?.Invoke("Microphone off · nothing is listening.");
    }

    public void Speak(string text)
    {
        StopListening();
        try
        {
            synthesizer ??= new SpeechSynthesizer();
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.PlatformNotSupportedException)
        { StatusChanged?.Invoke("Speech playback unavailable. The instruction remains on screen."); }
    }

    public void StopSpeaking() => synthesizer?.SpeakAsyncCancelAll();
    public void Stop() { StopListening(); StopSpeaking(); }
    public void Dispose() { Stop(); synthesizer?.Dispose(); synthesizer = null; }
}