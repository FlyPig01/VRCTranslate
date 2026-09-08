using System.Diagnostics;
using System.Text;
using Whisper.net;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// C# local speech recognizer backed by whisper.cpp through Whisper.net.
/// The recognizer accepts 16 kHz mono PCM samples and never calls the Windows
/// SpeechRecognizer API. A single processor is reused per language to avoid
/// reloading the model for every sentence.
/// </summary>
public sealed class WhisperLocalSpeechRecognizer : ILocalSpeechRecognizer
{
    private readonly LocalSpeechModelManager _modelManager;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private readonly int _threads;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _processorLanguage;
    private bool _disposed;

    public WhisperLocalSpeechRecognizer(
        LocalSpeechModelManager modelManager,
        int? threads = null)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _threads = Math.Clamp(threads ?? Math.Max(1, Environment.ProcessorCount / 2), 1, 32);
    }

    public string ModelId => LocalSpeechModelCatalog.ModelId;

    public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechModelCatalog.Languages;

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
            var processor = EnsureProcessor(request.SourceLanguage);
            var text = new StringBuilder();
            var detectedLanguage = request.SourceLanguage;
            await foreach (var segment in processor.ProcessAsync(request.Samples, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    text.Append(segment.Text);
                    if (string.Equals(detectedLanguage, "auto", StringComparison.Ordinal) &&
                        LocalSpeechLanguages.TryNormalize(segment.Language, out var segmentLanguage) &&
                        !string.Equals(segmentLanguage, "auto", StringComparison.Ordinal))
                    {
                        detectedLanguage = segmentLanguage;
                    }
                }
                finally
                {
                    processor.Return(segment);
                }
            }

            var normalized = NormalizeText(text.ToString());
            return new SpeechRecognitionResult(
                request.RequestId,
                normalized,
                detectedLanguage,
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
            _processor?.Dispose();
            _processor = null;
            _factory?.Dispose();
            _factory = null;
            _processorLanguage = null;
        }
        finally
        {
            _recognitionLock.Release();
            _recognitionLock.Dispose();
        }
    }

    private WhisperProcessor EnsureProcessor(string language)
    {
        if (_processor is not null && string.Equals(_processorLanguage, language, StringComparison.Ordinal))
        {
            return _processor;
        }

        try
        {
            _processor?.Dispose();
            _factory ??= WhisperFactory.FromPath(_modelManager.ModelPath);
            var builder = _factory.CreateBuilder()
                .WithThreads(_threads)
                .WithNoContext()
                .WithSingleSegment();
            if (string.Equals(language, "auto", StringComparison.Ordinal))
            {
                builder.WithLanguageDetection();
            }
            else
            {
                builder.WithLanguage(language);
            }

            _processor = builder.Build();
            _processorLanguage = language;
            return _processor;
        }
        catch (Exception exception)
        {
            _processor = null;
            _processorLanguage = null;
            throw new InvalidOperationException("本地语音模型加载失败，请重新安装本地组件。", exception);
        }
    }

    private static string NormalizeText(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WhisperLocalSpeechRecognizer));
    }
}
