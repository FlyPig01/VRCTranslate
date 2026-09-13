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

    /// <summary>
    /// Whether captions carry speaker labels. Off by default: the feature costs an
    /// extra embedding per sentence and only pays off once the user wants names.
    /// </summary>
    public bool SpeakerLabelsEnabled { get; set; }

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
        _recognizer.RecognizeAsync(request, cancellationToken);

    /// <summary>Loads the model for a session before its first sentence arrives.</summary>
    public Task PrepareAsync(
        string sourceLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out var language))
        {
            throw new ArgumentException(
                "本地语音只接受自动检测、简体中文、英语、日语或韩语。", nameof(sourceLanguage));
        }

        return _recognizer.PrepareAsync(language, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_speakers is IAsyncDisposable asyncSpeakers) await asyncSpeakers.DisposeAsync().ConfigureAwait(false);
        else if (_speakers is IDisposable disposableSpeakers) disposableSpeakers.Dispose();
        await _recognizer.DisposeAsync().ConfigureAwait(false);
    }
}
