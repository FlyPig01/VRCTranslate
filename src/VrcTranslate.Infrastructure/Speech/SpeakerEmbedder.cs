using SherpaOnnx;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Voiceprints from the bundled CAM++ speaker-embedding model. The model expects
/// 16 kHz mono audio, which is what the capture pipeline already produces, and
/// returns an empty vector when the clip is too short to be sure.
/// </summary>
public sealed class SpeakerEmbedder : IDisposable
{
    public const int RequiredSampleRate = 16_000;

    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly object _gate = new();
    private bool _disposed;

    public SpeakerEmbedder(string modelPath, int? threads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = Math.Clamp(threads ?? Math.Max(1, Environment.ProcessorCount / 2), 1, 32),
            Debug = 0,
            Provider = "cpu",
        };
        _extractor = new SpeakerEmbeddingExtractor(config);
        Dimension = _extractor.Dim;
    }

    public int Dimension { get; }

    public float[] Embed(ReadOnlyMemory<float> samples, int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sampleRate != RequiredSampleRate)
        {
            throw new ArgumentException("说话人嵌入需要 16 kHz 音频。", nameof(sampleRate));
        }

        if (samples.IsEmpty) return [];

        lock (_gate)
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(sampleRate, samples.ToArray());
            stream.InputFinished();
            return _extractor.IsReady(stream) ? _extractor.Compute(stream) : [];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _extractor.Dispose();
        }
    }
}