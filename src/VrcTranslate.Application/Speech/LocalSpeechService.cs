using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Speech;

/// <summary>
/// Application boundary for the optional local speech component. Pages use
/// this service instead of knowing about sherpa-onnx, model paths, or download
/// mechanics.
/// </summary>
public sealed class LocalSpeechService : IAsyncDisposable
{
    private readonly ILocalSpeechModelManager _modelManager;
    private readonly ILocalSpeechRecognizer _recognizer;
    private readonly ISpeakerIdentifier? _speakers;
    private ITencentSpeechRecognizer? _tencent;
    private TencentAsrSettings? _tencentSettings;
    private readonly object _tencentSync = new();

    public LocalSpeechService(
        ILocalSpeechModelManager modelManager,
        ILocalSpeechRecognizer recognizer,
        ISpeakerIdentifier? speakers = null)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _speakers = speakers;
    }

    /// <summary>Speaker separation, or null when this build has none wired up.</summary>
    public ISpeakerIdentifier? Speakers => _speakers;

    /// <summary>Last cloud failure, for diagnostics/UI; never contains credentials.</summary>
    public string? LastCloudError { get; private set; }

    /// <summary>Raised when a cloud failure falls back to the local recognizer.</summary>
    public event EventHandler<string>? CloudFallback;

    public TencentAsrSettings? TencentSettings => _tencentSettings;

    public void ConfigureTencent(ITencentSpeechRecognizer? recognizer, TencentAsrSettings? settings)
    {
        lock (_tencentSync)
        {
            _tencent?.Dispose();
            _tencent = recognizer;
            _tencentSettings = settings;
            LastCloudError = null;
        }
    }

    public bool HasTencent => _tencent is not null && _tencentSettings?.IsConfigured == true;

    /// <summary>
    /// Whether captions carry speaker labels. On by default: other-player captions
    /// always show who spoke, while the own-voice session keeps its labels off.
    /// </summary>
    public bool SpeakerLabelsEnabled { get; set; } = true;

    public string ModelId => _recognizer.ModelId;

    public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => _recognizer.SupportedLanguages;

    public LocalSpeechModelStatus GetModelStatus() => _modelManager.GetStatus();

    public Task<LocalSpeechModelStatus> InstallModelAsync(
        IProgress<LocalSpeechModelProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _modelManager.InstallAsync(progress, cancellationToken);

    public Task RemoveModelAsync(CancellationToken cancellationToken = default) =>
        _modelManager.RemoveAsync(cancellationToken);

    public Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken = default) =>
        RecognizeAsync(request, useCloudAsr: false, cancellationToken);

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        bool useCloudAsr,
        CancellationToken cancellationToken = default)
    {
        ITencentSpeechRecognizer? cloud;
        TencentAsrSettings? settings;
        lock (_tencentSync)
        {
            cloud = _tencent;
            settings = _tencentSettings;
        }

        if (useCloudAsr && cloud is not null && settings?.IsConfigured == true)
        {
            try
            {
                var result = await cloud.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
                LastCloudError = null;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LastCloudError = exception.Message;
                CloudFallback?.Invoke(this, exception.Message);
                if (!settings.FallbackToLocal) throw;
            }
        }

        return await _recognizer.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task TestTencentAsync(
        ITencentSpeechRecognizer recognizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        await recognizer.TestAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the model for a session before its first sentence arrives.</summary>
    public Task PrepareAsync(
        string sourceLanguage,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(sourceLanguage, useCloudAsr: false, cancellationToken);

    public Task PrepareAsync(
        string sourceLanguage,
        bool useCloudAsr,
        CancellationToken cancellationToken = default)
    {
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out var language))
        {
            throw new ArgumentException(
                "本地语音只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        if (useCloudAsr && HasTencent) return Task.CompletedTask;
        return _recognizer.PrepareAsync(language, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_tencentSync)
        {
            _tencent?.Dispose();
            _tencent = null;
        }
        if (_speakers is IAsyncDisposable asyncSpeakers) await asyncSpeakers.DisposeAsync().ConfigureAwait(false);
        else if (_speakers is IDisposable disposableSpeakers) disposableSpeakers.Dispose();
        await _recognizer.DisposeAsync().ConfigureAwait(false);
    }
}
