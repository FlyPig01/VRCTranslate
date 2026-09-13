using SherpaOnnx;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Locates speaker changes with the pyannote segmentation model. This is the
/// expensive part of speaker separation, so callers only run it for a segment
/// that already looked like it holds two voices.
/// </summary>
public sealed class SpeakerDiarizationSplitter : IDisposable
{
    private readonly OfflineSpeakerDiarization _diarization;
    private readonly object _gate = new();
    private bool _disposed;

    public SpeakerDiarizationSplitter(
        string segmentationModelPath,
        string embeddingModelPath,
        int? threads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentationModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelPath);
        var threadCount = Math.Clamp(threads ?? Math.Max(1, Environment.ProcessorCount / 2), 1, 32);
        var config = new OfflineSpeakerDiarizationConfig
        {
            Segmentation = new OfflineSpeakerSegmentationModelConfig
            {
                Pyannote = new OfflineSpeakerSegmentationPyannoteModelConfig { Model = segmentationModelPath },
                NumThreads = threadCount,
                Debug = 0,
                Provider = "cpu",
            },
            Embedding = new SpeakerEmbeddingExtractorConfig
            {
                Model = embeddingModelPath,
                NumThreads = threadCount,
                Debug = 0,
                Provider = "cpu",
            },
            Clustering = new FastClusteringConfig { NumClusters = -1, Threshold = 0.5f, ComputeConfidence = 0 },
            MinDurationOn = 0.3f,
            MinDurationOff = 0.5f,
        };
        _diarization = new OfflineSpeakerDiarization(config);
    }

    /// <summary>
    /// Sample offsets where the speaker changes. Only the boundaries are used, so
    /// the caller can split its own range and never lose audio that the model
    /// chose not to report.
    /// </summary>
    public IReadOnlyList<int> FindChangePoints(ReadOnlyMemory<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.IsEmpty) return [];

        lock (_gate)
        {
            var segments = _diarization.Process(samples.ToArray());
            var sampleRate = _diarization.SampleRate > 0 ? _diarization.SampleRate : SpeakerEmbedder.RequiredSampleRate;
            var points = new List<int>();
            for (var i = 1; i < segments.Length; i++)
            {
                if (segments[i].Speaker == segments[i - 1].Speaker) continue;
                var offset = (int)MathF.Round(segments[i].Start * sampleRate);
                if (offset <= 0 || offset >= samples.Length) continue;
                points.Add(offset);
            }

            return points;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _diarization.Dispose();
        }
    }
}