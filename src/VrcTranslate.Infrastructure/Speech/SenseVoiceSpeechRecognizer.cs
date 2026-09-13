using System.Diagnostics;
using SherpaOnnx;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// C# local speech recognizer backed by sherpa-onnx SenseVoiceSmall INT8.
/// The recognizer accepts 16 kHz mono PCM samples. SenseVoice covers Chinese,
/// English, Japanese, Korean, and Cantonese-accented Mandarin; the "auto"
/// request language maps to the model's own language detection. One instance is
/// kept per requested language because model load dominates startup cost.
/// </summary>
public sealed class SenseVoiceSpeechRecognizer : ILocalSpeechRecognizer
{
    private readonly LocalSpeechModelManager _modelManager;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private string? _recognizerLanguage;
    private bool _disposed;

    public SenseVoiceSpeechRecognizer(LocalSpeechModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    }

    public string ModelId => LocalSpeechModelCatalog.ModelId;

    public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechModelCatalog.Languages;

    public async Task PrepareAsync(string sourceLanguage, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var status = _modelManager.GetStatus();
        if (status.State != LocalSpeechModelState.Ready)
        {
            throw new InvalidOperationException(status.Message ?? "本地语音模型未安装。");
        }

        await _recognitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureRecognizer(sourceLanguage, status.FilePath);
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (request.SampleRate != 16_000)
        {
            throw new ArgumentException("本地语音识别需要 16 kHz 音频。", nameof(request));
        }

        var status = _modelManager.GetStatus();
        if (status.State != LocalSpeechModelState.Ready)
        {
            throw new InvalidOperationException(status.Message ?? "本地语音模型未安装。");
        }

        await _recognitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed();
            var recognizer = EnsureRecognizer(request.SourceLanguage, status.FilePath);
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(request.SampleRate, request.Samples.ToArray());
            recognizer.Decode(stream);
            var recognized = stream.Result.Text;
            var normalized = NormalizeText(recognized);
            return new SpeechRecognitionResult(
                request.RequestId,
                normalized,
                ResolveDetectedLanguage(request.SourceLanguage),
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("本地语音识别失败，请检查模型文件或重新安装本地组件。", exception);
        }
        finally
        {
            _recognitionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _recognitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
            _recognizerLanguage = null;
        }
        finally
        {
            _recognitionLock.Release();
            _recognitionLock.Dispose();
        }
    }

    private OfflineRecognizer EnsureRecognizer(string language, string modelDirectory)
    {
        // The pinned language is baked into the loaded model, so the instance is
        // keyed on the exact request language: switching from English to
        // Japanese has to rebuild it. "auto" lets SenseVoice run its own
        // language identification.
        if (_recognizer is not null && string.Equals(_recognizerLanguage, language, StringComparison.Ordinal))
        {
            return _recognizer;
        }

        try
        {
            _recognizer?.Dispose();
            var senseVoiceLanguage = string.Equals(language, "auto", StringComparison.Ordinal)
                ? string.Empty
                : language;
            var config = new OfflineRecognizerConfig
            {
                FeatConfig = new FeatureConfig { SampleRate = 16_000, FeatureDim = 80 },
                ModelConfig = new OfflineModelConfig
                {
                    SenseVoice = new OfflineSenseVoiceModelConfig
                    {
                        Model = Path.Combine(modelDirectory, LocalSpeechModelCatalog.ModelFileName),
                        Language = senseVoiceLanguage,
                        UseInverseTextNormalization = 1
                    },
                    Tokens = Path.Combine(modelDirectory, LocalSpeechModelCatalog.TokensFileName),
                    NumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                    Provider = "cpu",
                    Debug = 0
                }
            };
            _recognizer = new OfflineRecognizer(config);
            _recognizerLanguage = language;
            return _recognizer;
        }
        catch (Exception exception)
        {
            _recognizer = null;
            _recognizerLanguage = null;
            throw new InvalidOperationException("本地语音模型加载失败，请重新安装本地组件。", exception);
        }
    }

    private static string ResolveDetectedLanguage(string requestLanguage) =>
        // SenseVoice output does not expose its language guess through the C#
        // API; the request language is passed through and translation routes
        // keep their own auto-detection.
        requestLanguage;

    // SenseVoice can emit line breaks around event tags; captions are a single
    // line, so every whitespace run collapses to one space.
    private static string NormalizeText(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SenseVoiceSpeechRecognizer));
    }
}
