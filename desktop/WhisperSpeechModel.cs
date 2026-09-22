using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using Whisper.net;

namespace MSGuide.Desktop;

internal readonly record struct WhisperSegment(string Text, float Probability);

internal static class WhisperSpeechModel
{
    internal const int SampleRate = 16000;
    internal const int MaxPcmBytes = SampleRate * 2 * 30;
    internal const long ModelSizeBytes = 487614201;
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private static readonly CancellationTokenSource ShutdownCancellation = new();
    private static WhisperFactory? factory;

    internal static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MSGuide", "models", "ggml-small.en.bin");

    internal static void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(ShutdownCancellation.IsCancellationRequested, typeof(WhisperSpeechModel));
        if (!File.Exists(ModelPath))
            throw new FileNotFoundException(
                "The local Whisper speech model is missing. Run scripts\\Install-MSGuideSpeechModel.ps1 -AcceptDownload to install it.");
        if (new FileInfo(ModelPath).Length != ModelSizeBytes)
            throw new InvalidDataException(
                "The local Whisper speech model is incomplete. Run scripts\\Install-MSGuideSpeechModel.ps1 -AcceptDownload to repair it.");
    }

    internal static async Task ShutdownAsync()
    {
        ShutdownCancellation.Cancel();
        await InferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            factory?.Dispose();
            factory = null;
        }
        finally { InferenceGate.Release(); }
    }

    // Takes ownership. Neither PCM nor converted samples survive this operation.
    internal static async Task<IReadOnlyList<WhisperSegment>> TranscribeAsync(
        byte[] pcm, CancellationToken cancellationToken, Action<WhisperFactory>? inferenceStarting = null)
    {
        float[]? samples = null;
        bool entered = false;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownCancellation.Token);
            cancellationToken = lifetime.Token;
            cancellationToken.ThrowIfCancellationRequested();
            if (pcm.Length > MaxPcmBytes || (pcm.Length & 1) != 0)
                throw new ArgumentException("Expected at most 30 seconds of 16 kHz mono PCM16 audio.", nameof(pcm));
            samples = ConvertPcm(pcm);
            bool audible = HasAudibleSignal(samples);
            cancellationToken.ThrowIfCancellationRequested();
            if (!audible) return Array.Empty<WhisperSegment>();
            Normalize(samples);
            await InferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            if (factory is null)
            {
                // ponytail: one CPU model per process; the inference gate also protects its lifetime.
                var loadClock = Stopwatch.StartNew();
                factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { UseGpu = false });
                DiagnosticLog.Record("whisper_model_loaded", new { elapsedMs = loadClock.ElapsedMilliseconds });
            }
            cancellationToken.ThrowIfCancellationRequested();
            var processorClock = Stopwatch.StartNew();
            var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithNoContext()
                .WithThreads(Math.Min(Environment.ProcessorCount, 6))
                .WithTemperature(0)
                .WithTemperatureInc(0)
                .WithProbabilities()
                .WithoutStringPool()
                .Build();
            // App exit may synchronously wait for shutdown on the UI thread.
            await using var processorLifetime = processor.ConfigureAwait(false);
            DiagnosticLog.Record("whisper_processor_created", new { elapsedMs = processorClock.ElapsedMilliseconds });
            var segments = new List<WhisperSegment>();
            inferenceStarting?.Invoke(factory);
            var inferenceClock = Stopwatch.StartNew();
            bool finished = false;
            try
            {
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string text = CleanTranscript(segment.Text);
                    if (text.Length != 0)
                        segments.Add(new WhisperSegment(text,
                            float.IsFinite(segment.Probability) ? Math.Clamp(segment.Probability, 0, 1) : 0));
                }
                cancellationToken.ThrowIfCancellationRequested();
                finished = true;
                return segments;
            }
            finally
            {
                DiagnosticLog.Record("whisper_inference_finished", new
                { elapsedMs = inferenceClock.ElapsedMilliseconds, finished, cancelled = cancellationToken.IsCancellationRequested });
            }
        }
        finally
        {
            // Async processor disposal above waits for native work before these arrays are cleared.
            if (samples is not null) Array.Clear(samples);
            Array.Clear(pcm);
            if (entered) InferenceGate.Release();
        }
    }

    internal static string CleanTranscript(string text) =>
        text.Replace("[BLANK_AUDIO]", "", StringComparison.Ordinal).Trim();

    internal static float[] ConvertPcm(ReadOnlySpan<byte> pcm)
    {
        if ((pcm.Length & 1) != 0) throw new ArgumentException("PCM16 requires complete samples.", nameof(pcm));
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;
        return samples;
    }

    internal static bool HasAudibleSignal(ReadOnlySpan<float> samples)
    {
        // Measure AC energy before gain. This rejects near-digital silence/DC, not quiet speech.
        const int frameSize = SampleRate / 50;
        int audibleFrames = 0;
        for (int start = 0; start < samples.Length; start += frameSize)
        {
            var frame = samples.Slice(start, Math.Min(frameSize, samples.Length - start));
            double sum = 0, squares = 0;
            foreach (float sample in frame)
            {
                sum += sample;
                squares += sample * sample;
            }
            double variance = squares / frame.Length - Math.Pow(sum / frame.Length, 2);
            if (variance >= 0.0005 * 0.0005 && ++audibleFrames >= 4) return true;
        }
        return false;
    }

    internal static void Normalize(Span<float> samples)
    {
        if (samples.Length == 0) return;
        double sum = 0;
        foreach (float sample in samples) sum += sample;
        float mean = (float)(sum / samples.Length);
        float peak = 0;
        foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample - mean));
        float gain = peak == 0 ? 1 : Math.Min(8, 0.8f / peak);
        for (int i = 0; i < samples.Length; i++) samples[i] = Math.Clamp((samples[i] - mean) * gain, -1, 1);
    }

    internal static int AudioLevel(ReadOnlySpan<byte> pcm)
    {
        double squares = 0;
        int count = pcm.Length / 2;
        if (count == 0) return 0;
        for (int i = 0; i < count; i++)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768.0;
            squares += sample * sample;
        }
        double rms = Math.Sqrt(squares / count);
        return rms == 0 ? 0 : (int)Math.Clamp((20 * Math.Log10(rms) + 60) * 100 / 60, 0, 100);
    }
}

internal sealed class WhisperPcmBuffer
{
    private readonly object gate = new();
    private readonly byte[] storage = new byte[WhisperSpeechModel.MaxPcmBytes];
    private int count;
    private bool closed;

    internal int Count { get { lock (gate) return count; } }

    internal bool Append(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (closed) return true;
            int length = Math.Min(data.Length & ~1, storage.Length - count);
            data[..length].CopyTo(storage.AsSpan(count));
            count += length;
            return count == storage.Length;
        }
    }

    internal byte[] Take()
    {
        lock (gate)
        {
            if (closed) return [];
            byte[] result = storage.AsSpan(0, count).ToArray();
            Clear();
            return result;
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            closed = true;
            Array.Clear(storage);
            count = 0;
        }
    }
}
