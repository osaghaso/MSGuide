using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using NAudio.Wave;
using Whisper.net;

namespace MSGuide.Desktop;

internal static class WhisperTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Whisper regression failed: {name}.");
    }

    public static void Run()
    {
        Check(WhisperSpeechModel.CleanTranscript(" [BLANK_AUDIO] ") == "",
            "non-speech marker is not a question");
        Check(WhisperSpeechModel.CleanTranscript("Fix my team's camera. [BLANK_AUDIO]") == "Fix my team's camera.",
            "remove non-speech annotation without rewriting recognized words");
        byte[] endpoints = [0, 128, 0, 0, 255, 127];
        float[] converted = WhisperSpeechModel.ConvertPcm(endpoints);
        Check(converted[0] == -1 && converted[1] == 0 && converted[2] == 32767 / 32768f,
            "little-endian signed PCM16 conversion");
        Array.Clear(converted);
        Check(WhisperSpeechModel.AudioLevel(new byte[640]) == 0, "digital silence meter");
        Check(WhisperSpeechModel.AudioLevel(CreateTone(0.02f, 1)) >
            WhisperSpeechModel.AudioLevel(CreateTone(0.002f, 1)), "meter shows relative input amplitude");

        var buffer = new WhisperPcmBuffer();
        byte[] excess = new byte[WhisperSpeechModel.MaxPcmBytes + 640];
        Array.Fill<byte>(excess, 15);
        Check(buffer.Append(excess) && buffer.Count == 960000, "30 second capture ceiling");
        byte[] taken = buffer.Take();
        Check(taken.Length == 960000 && taken.All(value => value == 15), "bounded capture preserves samples");
        Check(buffer.Count == 0 && buffer.Take().Length == 0 && buffer.Append(endpoints), "capture can be consumed only once");
        Array.Clear(taken);
        Array.Clear(excess);

        var cancelledBuffer = new WhisperPcmBuffer();
        cancelledBuffer.Append(endpoints);
        cancelledBuffer.Clear();
        Check(cancelledBuffer.Count == 0 && cancelledBuffer.Take().Length == 0, "cancelled capture is discarded");
        float[] silent = new float[16000];
        Check(!WhisperSpeechModel.HasAudibleSignal(silent), "silence rejected before inference");
        Array.Fill(silent, 0.05f);
        Check(!WhisperSpeechModel.HasAudibleSignal(silent), "DC offset is not speech");
        Array.Clear(silent);
        float[] soft = WhisperSpeechModel.ConvertPcm(CreateTone(0.0015f, 1));
        Check(WhisperSpeechModel.HasAudibleSignal(soft), "soft signal is not gated out");
        WhisperSpeechModel.Normalize(soft);
        Check(soft.Max() < 0.013f && soft.Max() > 0.01f, "gain is bounded and consistent");
        Array.Clear(soft);

        byte[] cancelled = CreateTone(0.01f, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool observed = false;
        try { WhisperSpeechModel.TranscribeAsync(cancelled, cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { observed = true; }
        Check(observed && cancelled.All(value => value == 0), "pre-cancelled inference clears owned PCM without native work");
        byte[] quiet = CreateAmbient();
        Check(WhisperSpeechModel.TranscribeAsync(quiet, CancellationToken.None).GetAwaiter().GetResult().Count == 0
            && quiet.All(value => value == 0), "very quiet ambient noise yields no words and is cleared");

        using var recognizer = new WhisperDictationRecognizer();
        recognizer.Cancel();
        recognizer.Cancel();
        recognizer.Finish();
        bool restartRejected = false;
        try { recognizer.Start(); }
        catch (InvalidOperationException) { restartRejected = true; }
        Check(restartRejected, "cancelled adapter cannot start or open a microphone");
        Check(recognizer.FinishTimeout <= TimeSpan.FromSeconds(45), "bounded finishing deadline");
        RunLifecycleAsync().GetAwaiter().GetResult();
    }

    private static async Task RunLifecycleAsync()
    {
        var input = new MemoryWaveIn { FinalPcm = CreateTone(0.02f, 1) };
        int ended = 0, recognized = 0, completed = 0, calls = 0;
        byte[]? owned = null;
        using var recognizer = new WhisperDictationRecognizer(() => input, (pcm, _) =>
        {
            calls++;
            owned = pcm;
            Check(input.Disposed && ended == 1, "microphone closes and InputEnded precedes inference");
            Check(pcm.Length == 64000, "normal Stop drains final recorder bytes");
            return Task.FromResult<IReadOnlyList<WhisperSegment>>([new("test result", 0.8f)]);
        });
        recognizer.InputStopped += error => { Check(error is null, "input stop acknowledged"); ended++; };
        recognizer.Recognized += (_, _) => recognized++;
        recognizer.Completed += error =>
        {
            Check(error is null, "normal completion has no error");
            completed++;
            recognizer.Cancel();
        };
        recognizer.Start();
        input.Emit(CreateTone(0.02f, 1));
        recognizer.Finish();
        recognizer.Finish();
        await recognizer.Cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(input.Stops == 1 && calls == 1 && recognized == 1 && completed == 1,
            "stop and reentrant completion cancellation are single-shot");
        Check(owned is not null && owned.All(value => value == 0), "adapter clears finished PCM");

        var boundedInput = new MemoryWaveIn();
        bool boundedInferred = false;
        using var bounded = new WhisperDictationRecognizer(() => boundedInput, (pcm, _) =>
        {
            Check(pcm.Length == 960000 && boundedInput.Disposed, "automatic stop preserves only 30 seconds");
            boundedInferred = true;
            return Task.FromResult<IReadOnlyList<WhisperSegment>>([]);
        });
        bounded.Start();
        boundedInput.Emit(new byte[1000000]);
        await bounded.Cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(boundedInput.Stops == 1 && boundedInferred, "full capture automatically closes microphone");

        var cancelledInput = new MemoryWaveIn();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[]? processingPcm = null;
        int cancelledEvents = 0;
        using var cancelled = new WhisperDictationRecognizer(() => cancelledInput, async (pcm, token) =>
        {
            processingPcm = pcm;
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Array.Empty<WhisperSegment>();
        });
        cancelled.Recognized += (_, _) => cancelledEvents++;
        cancelled.Completed += _ => cancelledEvents++;
        cancelled.Start();
        cancelledInput.Emit(CreateTone(0.02f, 1));
        cancelled.Finish();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancelled.Cancel();
        cancelled.Dispose();
        Check(processingPcm is not null && processingPcm.Any(value => value != 0),
            "cancellation never clears memory still used by inference");
        release.SetResult();
        await cancelled.Cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(cancelledEvents == 0 && processingPcm!.All(value => value == 0),
            "cancel suppresses late callbacks and clears memory after inference exits");

        var reentrantInput = new MemoryWaveIn();
        int unexpected = 0;
        using var reentrant = new WhisperDictationRecognizer(() => reentrantInput, (_, _) =>
        {
            unexpected++;
            return Task.FromResult<IReadOnlyList<WhisperSegment>>([]);
        });
        reentrant.InputStopped += _ => reentrant.Cancel();
        reentrant.Completed += _ => unexpected++;
        reentrant.Start();
        reentrantInput.Emit(CreateTone(0.02f, 1));
        reentrant.Finish();
        await reentrant.Cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(reentrantInput.Disposed && unexpected == 0, "reentrant InputEnded cancellation prevents inference");

        var stalledInput = new MemoryWaveIn { NotifyStopped = false };
        var stopFailure = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int stalledInferences = 0;
        using var stalled = new WhisperDictationRecognizer(() => stalledInput, (_, _) =>
        {
            stalledInferences++;
            return Task.FromResult<IReadOnlyList<WhisperSegment>>([]);
        });
        stalled.InputStopped += error => { if (error is not null) stopFailure.TrySetResult(error); };
        stalled.Start();
        stalled.Finish();
        var stopError = await stopFailure.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        Check(stalledInput.Active && !stalledInput.Disposed && stopError is MicrophoneStopUnconfirmedException
            && stalledInferences == 0, "missing stop acknowledgement retains hardware uncertainty without inference");
        stalledInput.AcknowledgeStopped();
        await stalled.Cleanup.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Check(stalledInput.Disposed && stalledInferences == 0, "late stop cannot revive timed-out inference");

        using var releaseStop = new ManualResetEventSlim();
        var blockedInput = new MemoryWaveIn { BlockStop = releaseStop };
        var reported = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int blockedInference = 0, blockedInputEnded = 0;
        using var blocked = new WhisperDictationRecognizer(() => blockedInput, (_, _) =>
        {
            blockedInference++;
            return Task.FromResult<IReadOnlyList<WhisperSegment>>([]);
        });
        blocked.InputStopped += error =>
        {
            if (error is null) blockedInputEnded++;
            else reported.TrySetResult(error);
        };
        blocked.Start();
        blocked.Finish();
        try
        {
            var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            Check(error is MicrophoneStopUnconfirmedException && blockedInputEnded == 0 && blockedInference == 0,
                "a blocked native stop reports unknown microphone state without waiting on its lock");
        }
        finally { releaseStop.Set(); }
        await blocked.Cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(blockedInput.Disposed, "native stop cleanup completes when the driver returns");
    }

    public static async Task RunSyntheticAsync()
    {
        WhisperSpeechModel.EnsureAvailable();
        string[] phrases = ["Please open the meeting settings.", "Please open the meeting settings.", "Check my Teams camera."];
        string[][] expectedWords = [["open", "meeting", "settings"], ["open", "meeting", "settings"], ["check", "teams", "camera"]];
        WhisperFactory? firstFactory = null;
        for (int i = 0; i < phrases.Length; i++)
        {
            byte[] pcm = Synthesize(phrases[i]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var watch = Stopwatch.StartNew();
            var segments = await Task.Run(() => WhisperSpeechModel.TranscribeAsync(pcm, deadline.Token, factory =>
            {
                firstFactory ??= factory;
                Check(ReferenceEquals(firstFactory, factory), "warm recordings reuse the loaded model");
            }));
            string text = string.Join(" ", segments.Select(segment => segment.Text));
            Console.WriteLine($"Whisper public synthetic phrase {i + 1} ({(i == 0 ? "cold" : "warm")}): {text} ({watch.Elapsed.TotalSeconds:F2}s).");
            string[] words = text.Split([' ', '.', ',', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new string(word.Where(char.IsLetter).ToArray())).ToArray();
            Check(expectedWords[i].All(expected => words.Contains(expected, StringComparer.OrdinalIgnoreCase)),
                $"synthetic phrase {i + 1} contains expected general words");
            Check(pcm.All(value => value == 0), "synthetic PCM is cleared after inference");
        }

        byte[] softPcm = Synthesize(phrases[0]);
        for (int offset = 0; offset < softPcm.Length; offset += 2)
            BinaryPrimitives.WriteInt16LittleEndian(softPcm.AsSpan(offset, 2),
                (short)(BinaryPrimitives.ReadInt16LittleEndian(softPcm.AsSpan(offset, 2)) / 32));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            var watch = Stopwatch.StartNew();
            var segments = await Task.Run(() => WhisperSpeechModel.TranscribeAsync(softPcm, deadline.Token));
            string text = string.Join(" ", segments.Select(segment => segment.Text));
            Check(expectedWords[0].All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)),
                "soft synthetic speech survives the silence gate and gain");
            Check(softPcm.All(value => value == 0), "soft synthetic PCM is cleared");
            Console.WriteLine($"Whisper public soft synthetic phrase: {text} ({watch.Elapsed.TotalSeconds:F2}s).");
        }

        byte[][] nonSpeech = [new byte[32000], CreateAmbient()];
        foreach (byte[] pcm in nonSpeech)
            Check((await WhisperSpeechModel.TranscribeAsync(pcm, CancellationToken.None)).Count == 0,
                "synthetic silence/quiet ambient noise produces no fabricated words");
        Console.WriteLine("Whisper synthetic silence and quiet ambient noise: no words.");

        byte[] cancellable = Synthesize(phrases[0]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var cancellationWatch = Stopwatch.StartNew();
        bool observed = false, inferenceStarted = false;
        try
        {
            await Task.Run(() => WhisperSpeechModel.TranscribeAsync(cancellable, cancellation.Token, _ =>
            {
                inferenceStarted = true;
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
            }));
        }
        catch (OperationCanceledException) { observed = true; }
        Check(inferenceStarted && observed && cancellable.All(value => value == 0),
            "native in-flight cancellation waits for cleanup and clears PCM");
        Console.WriteLine($"Whisper synthetic cancellation: cleaned up ({cancellationWatch.Elapsed.TotalSeconds:F2}s).");
        await RunFactoryShutdownAsync(firstFactory!);
    }

    private static async Task RunFactoryShutdownAsync(WhisperFactory firstFactory)
    {
        byte[] activePcm = CreateTone(0.02f, 1), queuedPcm = CreateTone(0.02f, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var active = Task.Run(() => WhisperSpeechModel.TranscribeAsync(activePcm, deadline.Token, factory =>
        {
            Check(ReferenceEquals(firstFactory, factory), "cancellation preserves the warm model");
            entered.SetResult();
            Check(release.Wait(TimeSpan.FromSeconds(10)), "shutdown regression releases the active processor");
        }));
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = WhisperSpeechModel.TranscribeAsync(queuedPcm, deadline.Token);
            shutdown = WhisperSpeechModel.ShutdownAsync();
            Check(!shutdown.IsCompleted && !active.IsCompleted && activePcm.Any(value => value != 0),
                "shutdown waits for the active processor without clearing its audio");
            bool cancelled = false;
            try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && queuedPcm.All(value => value == 0),
                "shutdown cancels queued inference and clears its audio");
        }
        finally { release.Set(); }
        bool activeCancelled = false;
        try { await active.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { activeCancelled = true; }
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10));
        await WhisperSpeechModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        bool disposed = false;
        try { firstFactory.CreateBuilder(); }
        catch (ObjectDisposedException) { disposed = true; }
        Check(disposed && activeCancelled && activePcm.All(value => value == 0),
            "idempotent shutdown disposes the model only after processor and audio cleanup");
        byte[] latePcm = CreateTone(0.02f, 1);
        bool lateCancelled = false;
        try { await WhisperSpeechModel.TranscribeAsync(latePcm, CancellationToken.None); }
        catch (OperationCanceledException) { lateCancelled = true; }
        Check(lateCancelled && latePcm.All(value => value == 0), "shutdown cannot reload the model");
        Console.WriteLine("Whisper warm model reuse, cancellation and shutdown: passed.");
    }

    private static byte[] Synthesize(string phrase)
    {
        using var memory = new MemoryStream();
        try
        {
            using (var synthesizer = new SpeechSynthesizer())
            {
                synthesizer.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, CultureInfo.GetCultureInfo("en-US"));
                synthesizer.SetOutputToAudioStream(memory,
                    new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synthesizer.Speak(phrase);
                synthesizer.SetOutputToNull();
            }
            return memory.ToArray();
        }
        finally
        {
            if (memory.TryGetBuffer(out var storage)) Array.Clear(storage.Array!);
        }
    }

    private static byte[] CreateTone(float amplitude, int seconds)
    {
        var pcm = new byte[WhisperSpeechModel.SampleRate * seconds * 2];
        for (int i = 0; i < pcm.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2),
                (short)(32767 * amplitude * Math.Sin(2 * Math.PI * 220 * i / WhisperSpeechModel.SampleRate)));
        return pcm;
    }

    private static byte[] CreateAmbient()
    {
        var pcm = new byte[32000];
        var random = new Random(1234);
        for (int i = 0; i < pcm.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i, 2), (short)random.Next(-3, 4));
        return pcm;
    }

    internal sealed class MemoryWaveIn : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        internal int Stops { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Active { get; private set; }
        internal byte[]? FinalPcm { get; init; }
        internal bool NotifyStopped { get; init; } = true;
        internal ManualResetEventSlim? BlockStop { get; init; }
        public void StartRecording() => Active = true;
        internal void Emit(byte[] pcm) => DataAvailable?.Invoke(this, new WaveInEventArgs(pcm, pcm.Length));
        public void StopRecording()
        {
            Stops++;
            BlockStop?.Wait();
            if (FinalPcm is not null) Emit(FinalPcm);
            if (NotifyStopped) AcknowledgeStopped();
        }
        internal void AcknowledgeStopped()
        {
            Active = false;
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }
        public void Dispose() => Disposed = true;
    }
}
