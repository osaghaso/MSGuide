using NAudio.Wave;

namespace MSGuide.Desktop;

internal sealed class WhisperDictationRecognizer : IDictationRecognizer
{
    private enum Phase { New, Recording, Stopping, Transcribing, Completed, Cancelled }
    private readonly object gate = new();
    private readonly object recorderGate = new();
    private readonly WhisperPcmBuffer buffer = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource<Exception?> recordingStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int deviceNumber;
    private readonly string? expectedDeviceName;
    private readonly Func<IWaveIn>? createTestInput;
    private readonly Func<byte[], CancellationToken, Task<IReadOnlyList<WhisperSegment>>> transcribe;
    private IWaveIn? recorder;
    private System.Threading.Timer? captureDeadline;
    private System.Threading.Timer? stopDeadline;
    private Phase phase;
    private Task? worker;
    private bool cancellationDisposed;
    private bool inputClosed, stopFailureReported;
    private int stopRequested;

    public string CultureName => "en-US";
    public TimeSpan FinishTimeout => TimeSpan.FromSeconds(45);
    public string FinishDescription => "Transcribing locally with Whisper; this can take up to 45 seconds.";
    public event Action<string, float>? Recognized;
    public event Action<string>? Hypothesized { add { } remove { } }
    public event Action<int>? AudioLevel;
    public event Action<string>? SignalProblem;
    public event Action? Rejected;
    public event Action<Exception?>? Completed;
    public event Action<Exception?>? InputStopped;

    internal WhisperDictationRecognizer(int deviceNumber = -1, string? expectedDeviceName = null)
    {
        this.deviceNumber = deviceNumber;
        this.expectedDeviceName = expectedDeviceName;
        transcribe = (pcm, token) => WhisperSpeechModel.TranscribeAsync(pcm, token);
    }

    internal WhisperDictationRecognizer(Func<IWaveIn> createTestInput,
        Func<byte[], CancellationToken, Task<IReadOnlyList<WhisperSegment>>> transcribe)
    {
        this.createTestInput = createTestInput;
        this.transcribe = transcribe;
    }

    internal Task Cleanup { get { lock (gate) return worker ?? Task.CompletedTask; } }

    public void Start()
    {
        lock (gate)
        {
            if (phase != Phase.New) throw new InvalidOperationException("A dictation recording can only be started once.");
            try
            {
                if (createTestInput is null)
                {
                    WhisperSpeechModel.EnsureAvailable();
                    ValidateDevice();
                }
                lock (recorderGate)
                {
                    // RecordingStopped must not depend on the WPF dispatcher during cancellation.
                    var context = SynchronizationContext.Current;
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(null);
                        recorder = createTestInput?.Invoke() ?? new WaveInEvent
                        {
                            DeviceNumber = deviceNumber,
                            WaveFormat = new WaveFormat(WhisperSpeechModel.SampleRate, 16, 1),
                            BufferMilliseconds = 100,
                            NumberOfBuffers = 3
                        };
                    }
                    finally { SynchronizationContext.SetSynchronizationContext(context); }
                    recorder.DataAvailable += OnDataAvailable;
                    recorder.RecordingStopped += OnRecordingStopped;
                    phase = Phase.Recording;
                    recorder.StartRecording();
                }
                captureDeadline = new System.Threading.Timer(_ => Finish(), null,
                    TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                worker = Task.Run(CompleteRecordingAsync);
            }
            catch
            {
                phase = Phase.Cancelled;
                buffer.Clear();
                CloseRecorder();
                ReportInputStopped(null);
                DisposeCancellation();
                throw;
            }
        }
    }

    private void ValidateDevice()
    {
        int count = WaveInEvent.DeviceCount;
        if (count == 0) throw new InvalidOperationException("No microphone is available. Connect a microphone and try again.");
        if (deviceNumber < -1 || deviceNumber >= count)
            throw new InvalidOperationException("The selected microphone no longer exists. Select an available microphone and try again.");
        if (deviceNumber >= 0)
        {
            string actual = WaveInEvent.GetCapabilities(deviceNumber).ProductName;
            if (expectedDeviceName is not null && !string.Equals(actual, expectedDeviceName, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected microphone changed. Reselect the microphone before recording.");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        bool full;
        int level;
        try
        {
            lock (gate)
            {
                if (phase is not (Phase.Recording or Phase.Stopping)) return;
                var pcm = e.Buffer.AsSpan(0, e.BytesRecorded);
                full = buffer.Append(pcm);
                level = WhisperSpeechModel.AudioLevel(pcm);
            }
        }
        finally { Array.Clear(e.Buffer); }
        Publish(() => AudioLevel?.Invoke(level));
        if (level >= 98) Publish(() => SignalProblem?.Invoke("TooLoud"));
        if (full) Finish();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        recordingStopped.TrySetResult(e.Exception);

    public void Finish()
    {
        lock (gate)
        {
            if (phase != Phase.Recording) return;
            phase = Phase.Stopping;
            captureDeadline?.Dispose();
            captureDeadline = null;
            cancellation.CancelAfter(FinishTimeout);
        }
        RequestRecorderStop();
    }

    private void RequestRecorderStop()
    {
        if (Interlocked.Exchange(ref stopRequested, 1) != 0) return;
        lock (gate)
        {
            stopDeadline = new System.Threading.Timer(_ => ReportInputStopped(
                new MicrophoneStopUnconfirmedException()),
                null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
        _ = Task.Run(() =>
        {
            try
            {
                lock (recorderGate) recorder?.StopRecording();
            }
            catch (Exception) { ReportInputStopped(new MicrophoneStopUnconfirmedException()); }
        });
    }

    private async Task CompleteRecordingAsync()
    {
        byte[]? pcm = null;
        try
        {
            Exception? captureError = await recordingStopped.Task.ConfigureAwait(false);
            CloseRecorder();
            ReportInputStopped(null);
            lock (gate)
            {
                captureDeadline?.Dispose();
                captureDeadline = null;
                stopDeadline?.Dispose();
                stopDeadline = null;
                if (phase == Phase.Cancelled) return;
                if (phase == Phase.Recording) cancellation.CancelAfter(FinishTimeout);
                phase = Phase.Transcribing;
            }
            if (captureError is not null)
                throw new InvalidOperationException("Microphone capture failed. Check the input device and try again.", captureError);
            cancellation.Token.ThrowIfCancellationRequested();
            pcm = buffer.Take();
            var segments = await transcribe(pcm, cancellation.Token).ConfigureAwait(false);
            if (segments.Count == 0) Publish(() => Rejected?.Invoke());
            foreach (var segment in segments)
                Publish(() => Recognized?.Invoke(segment.Text, segment.Probability));
            Complete(null);
        }
        catch (OperationCanceledException)
        {
            Complete(new TimeoutException("Local speech transcription timed out. Try a shorter recording."));
        }
        catch (Exception error)
        {
            if (!inputClosed) ReportInputStopped(new MicrophoneStopUnconfirmedException());
            Complete(new InvalidOperationException(
                "Local Whisper transcription failed. Check the local model/runtime and try again.", error));
        }
        finally
        {
            if (pcm is not null) Array.Clear(pcm);
            buffer.Clear();
            lock (gate)
            {
                captureDeadline?.Dispose();
                stopDeadline?.Dispose();
                captureDeadline = stopDeadline = null;
                DisposeCancellation();
            }
        }
    }

    private void ReportInputStopped(Exception? error)
    {
        lock (gate)
        {
            if (inputClosed || error is not null && stopFailureReported) return;
            if (error is null)
            {
                inputClosed = true;
                stopDeadline?.Dispose();
                stopDeadline = null;
            }
            else
            {
                stopFailureReported = true;
                phase = Phase.Cancelled;
                buffer.Clear();
                if (!cancellationDisposed) cancellation.Cancel();
            }
            DiagnosticLog.Record("microphone_input_stopped", new { confirmed = error is null });
            // Hardware acknowledgement must survive transcript cancellation, including a late stop.
            InputStopped?.Invoke(error);
        }
    }

    private void CloseRecorder()
    {
        lock (recorderGate)
        {
            var input = recorder;
            recorder = null;
            if (input is null) return;
            input.DataAvailable -= OnDataAvailable;
            input.RecordingStopped -= OnRecordingStopped;
            input.Dispose();
        }
    }

    private void Publish(Action action)
    {
        lock (gate)
        {
            if (phase is Phase.Cancelled or Phase.Completed) return;
            action();
        }
    }

    private void Complete(Exception? error)
    {
        lock (gate)
        {
            if (phase is Phase.Cancelled or Phase.Completed) return;
            phase = Phase.Completed;
            Completed?.Invoke(error);
        }
    }

    public void Cancel()
    {
        bool stop, neverStarted;
        lock (gate)
        {
            if (phase == Phase.Cancelled) return;
            neverStarted = phase == Phase.New;
            stop = phase is Phase.Recording or Phase.Stopping;
            phase = Phase.Cancelled;
            captureDeadline?.Dispose();
            captureDeadline = null;
            buffer.Clear();
            if (!cancellationDisposed) cancellation.Cancel();
            if (worker is null) DisposeCancellation();
        }
        if (stop) RequestRecorderStop();
        else if (neverStarted) ReportInputStopped(null);
    }

    private void DisposeCancellation()
    {
        if (cancellationDisposed) return;
        cancellation.Dispose();
        cancellationDisposed = true;
    }

    public void Dispose() => Cancel();
}

internal sealed class MicrophoneStopUnconfirmedException : InvalidOperationException
{
    public MicrophoneStopUnconfirmedException()
        : base("Microphone stop was not confirmed. Close MSGuide to release the audio process before recording again.")
    {
    }
}
