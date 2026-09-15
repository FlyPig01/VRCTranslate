using SherpaOnnx;
using VrcTranslate.Application.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Speech gate backed by sherpa-onnx Silero VAD. Unlike an RMS gate it scores
/// "is this a voice", so VRChat background music and effects no longer hold
/// the gate open (flooding the recognizer with noise) or mask quiet talkers.
/// The native detector already enforces min-speech/min-silence/max-speech
/// timing; this wrapper only adapts it to the ISpeechSegmenter contract.
/// </summary>
public sealed class SileroVadSegmenter : ISpeechSegmenter, IDisposable
{
    private readonly object _sync = new();
    private readonly VoiceActivityDetector _vad;
    private readonly int _minimumSpeechSamples;

    public SileroVadSegmenter(
        string modelPath,
        int sampleRate = 16_000,
        float threshold = 0.5f,
        float minSpeechSeconds = 0.24f,
        float minSilenceSeconds = 0.6f,
        float maxSpeechSeconds = 12f,
        float bufferSizeSeconds = 60f)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentException("缺少 Silero VAD 模型路径。", nameof(modelPath));
        if (sampleRate != 16_000 && sampleRate != 8_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Silero VAD 只支持 8 kHz 或 16 kHz。");
        }

        _vad = new VoiceActivityDetector(
            new VadModelConfig
            {
                SileroVad = new SileroVadModelConfig
                {
                    Model = modelPath,
                    Threshold = threshold,
                    MinSpeechDuration = minSpeechSeconds,
                    MinSilenceDuration = minSilenceSeconds,
                    MaxSpeechDuration = maxSpeechSeconds,
                    WindowSize = 512,
                },
                SampleRate = sampleRate,
                NumThreads = 1,
                Provider = "cpu",
                Debug = 0,
            },
            bufferSizeSeconds);
        SampleRate = sampleRate;
        _minimumSpeechSamples = (int)(sampleRate * minSpeechSeconds);
    }

    public int SampleRate { get; }

    public IReadOnlyList<ReadOnlyMemory<float>> Append(ReadOnlyMemory<float> chunk)
    {
        if (chunk.IsEmpty) return [];
        lock (_sync)
        {
            _vad.AcceptWaveform(chunk.ToArray());
            return DrainCompleted();
        }
    }

    public IReadOnlyList<ReadOnlyMemory<float>> Flush()
    {
        lock (_sync)
        {
            _vad.Flush();
            return DrainCompleted();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _vad.Dispose();
        }
    }

    private List<ReadOnlyMemory<float>> DrainCompleted()
    {
        var completed = new List<ReadOnlyMemory<float>>();
        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();
            if (segment.Samples is { Length: > 0 } samples &&
                samples.Length >= _minimumSpeechSamples)
            {
                completed.Add(samples);
            }
        }
        return completed;
    }
}
