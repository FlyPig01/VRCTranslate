using System.Diagnostics;
using NAudio.Wave;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// One startup check of the local speech stack: load the shipped recognition
/// model, run a known speech sample through the speech gate and the recognizer,
/// and report which half failed. The check exists because a machine whose model
/// cannot load used to show exactly the same thing as a silent room: an empty
/// caption window with no explanation. It also warms the recognizer, so the
/// first real sentence does not pay for the model load.
/// </summary>
public sealed class LocalSpeechSelfTest
{
    /// <summary>Sample shipped beside the application for the check.</summary>
    public const string BundledAudioRelativePath = "Resources/self-test-zh.wav";

    private const int SampleRate = 16_000;

    private readonly LocalSpeechService _speech;
    private readonly Func<ISpeechSegmenter> _segmenterFactory;

    public LocalSpeechSelfTest(LocalSpeechService speech, Func<ISpeechSegmenter>? segmenterFactory = null)
    {
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _segmenterFactory = segmenterFactory ?? LocalSpeechSegmenterFactory.CreateDefault;
    }

    /// <summary>Runs the check against the known sample; never throws for an infrastructure fault.</summary>
    public async Task<LocalSpeechSelfTestResult> RunAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        var started = Stopwatch.StartNew();
        var status = _speech.GetModelStatus();
        if (status.State != LocalSpeechModelState.Ready)
        {
            return new LocalSpeechSelfTestResult(
                ModelReady: false,
                VadUsable: false,
                AsrUsable: false,
                Message: status.Message ?? "本地语音模型未就绪",
                Detail: $"state={status.State} path={status.FilePath}",
                Duration: started.Elapsed);
        }

        float[] samples;
        try
        {
            samples = ReadMono16kSamples(audioPath);
        }
        catch (Exception exception)
        {
            return new LocalSpeechSelfTestResult(
                ModelReady: true,
                VadUsable: false,
                AsrUsable: false,
                Message: "自检音频不可用",
                Detail: exception.ToString(),
                Duration: started.Elapsed);
        }

        var vadUsable = CheckSegments(samples, out var vadDetail);

        string? detail = vadDetail;
        try
        {
            await _speech.PrepareAsync("auto", cancellationToken).ConfigureAwait(false);
            var result = await _speech.RecognizeAsync(
                new SpeechRecognitionRequest(samples, SampleRate, "auto"),
                cancellationToken).ConfigureAwait(false);
            var text = result.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return new LocalSpeechSelfTestResult(
                    ModelReady: true,
                    VadUsable: vadUsable,
                    AsrUsable: false,
                    Message: "模型能加载，但测试语音没有识别出文字",
                    Detail: detail,
                    Duration: started.Elapsed);
            }

            return new LocalSpeechSelfTestResult(
                ModelReady: true,
                VadUsable: vadUsable,
                AsrUsable: true,
                Message: "自检通过",
                Detail: detail,
                Duration: started.Elapsed);
        }
        catch (Exception exception)
        {
            return new LocalSpeechSelfTestResult(
                ModelReady: true,
                VadUsable: vadUsable,
                AsrUsable: false,
                Message: "本地语音模型无法运行",
                Detail: Join(detail, exception.ToString()),
                Duration: started.Elapsed);
        }
    }

    /// <summary>
    /// Runs the shipped gate over the sample. A model that loads but never emits
    /// a segment is reported as unusable so the live session keeps the energy
    /// gate; the ASR half is still tested independently.
    /// </summary>
    private bool CheckSegments(float[] samples, out string? detail)
    {
        detail = null;
        ISpeechSegmenter? segmenter = null;
        try
        {
            segmenter = _segmenterFactory();
            if (segmenter is not SileroVadSegmenter)
            {
                detail = "native VAD unavailable; the energy gate is used";
                return false;
            }

            var produced = false;
            for (var offset = 0; offset < samples.Length; offset += 1_600)
            {
                var count = Math.Min(1_600, samples.Length - offset);
                if (segmenter.Append(new ReadOnlyMemory<float>(samples, offset, count)).Count > 0)
                {
                    produced = true;
                }
            }

            if (segmenter.Flush().Count > 0) produced = true;
            if (!produced) detail = "VAD produced no segment on the test sample";
            return produced;
        }
        catch (Exception exception)
        {
            detail = exception.ToString();
            return false;
        }
        finally
        {
            if (segmenter is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { /* diagnostics must not fail the check */ }
            }
        }
    }

    private static float[] ReadMono16kSamples(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != SampleRate ||
            reader.WaveFormat.Channels != 1 ||
            reader.WaveFormat.BitsPerSample != 16)
        {
            throw new InvalidDataException(
                $"Self-test audio must be 16 kHz mono 16-bit PCM; got {reader.WaveFormat}.");
        }

        var provider = reader.ToSampleProvider();
        var samples = new List<float>((int)Math.Max(0, reader.Length / 2));
        var buffer = new float[4_096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        if (samples.Count < SampleRate)
        {
            throw new InvalidDataException("Self-test audio is shorter than one second.");
        }

        return samples.ToArray();
    }

    private static string? Join(string? first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first + Environment.NewLine + second;
}
