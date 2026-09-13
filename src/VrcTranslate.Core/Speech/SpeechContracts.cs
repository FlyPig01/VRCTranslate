namespace VrcTranslate.Core.Speech;

/// <summary>
/// Languages intentionally exposed by the bundled local speech model.
/// Language tags are kept independent from the translation route so an ASR
/// model can validate its own input before any translation is requested.
/// </summary>
public sealed record SpeechLanguageOption(string Code, string DisplayName)
{
    public static readonly SpeechLanguageOption English = new("en", "English");
    public static readonly SpeechLanguageOption Japanese = new("ja", "日本語");
    public static readonly SpeechLanguageOption Korean = new("ko", "한국어");
}

public static class LocalSpeechLanguages
{
    /// <summary>
    /// Languages shown for other-player captions. Own voice is handled as
    /// Simplified Chinese internally and deliberately has no language picker.
    /// </summary>
    public static IReadOnlyList<SpeechLanguageOption> Supported { get; } =
        [SpeechLanguageOption.English, SpeechLanguageOption.Japanese, SpeechLanguageOption.Korean];

    public const string SelfChinese = "zh";

    public static bool TryNormalize(string? value, out string language)
    {
        var normalized = value?.Trim().Replace('_', '-').ToLowerInvariant();
        language = normalized switch
        {
            "auto" or "auto-detect" => "auto",
            "en" or "en-us" or "en-gb" => "en",
            "ja" or "ja-jp" => "ja",
            "ko" or "ko-kr" => "ko",
            // The selector for other voices intentionally omits Chinese, but
            // the user's own voice is fixed to Simplified Chinese.
            "zh" or "zh-cn" => SelfChinese,
            _ => string.Empty
        };
        return language.Length > 0;
    }

    public static bool IsSupported(string? value) => TryNormalize(value, out _);
}

public sealed record SpeechRecognitionRequest
{
    public SpeechRecognitionRequest(
        ReadOnlyMemory<float> samples,
        int sampleRate,
        string sourceLanguage,
        string? requestId = null)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("Speech samples are required.", nameof(samples));
        }

        if (sampleRate is < 8_000 or > 48_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 48 kHz.");
        }

        if (!LocalSpeechLanguages.TryNormalize(sourceLanguage, out var normalizedLanguage))
        {
            throw new ArgumentException("The local model accepts automatic detection, Chinese, English, Japanese, or Korean.", nameof(sourceLanguage));
        }

        Samples = samples;
        SampleRate = sampleRate;
        SourceLanguage = normalizedLanguage;
        RequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId.Trim();
    }

    public ReadOnlyMemory<float> Samples { get; }
    public int SampleRate { get; }
    public string SourceLanguage { get; }
    public string RequestId { get; }
}

public sealed record SpeechRecognitionResult(
    string RequestId,
    string Text,
    string SourceLanguage,
    TimeSpan Elapsed);

public enum LocalSpeechModelState
{
    NotInstalled,
    Installing,
    Ready,
    Invalid
}

public sealed record LocalSpeechModelStatus(
    LocalSpeechModelState State,
    string ModelId,
    string DisplayName,
    string FilePath,
    long InstalledBytes,
    string? Message = null,
    bool IsBundled = false);

public sealed record LocalSpeechModelProgress(
    long BytesReceived,
    long? TotalBytes,
    string ModelId)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1)
        : null;
}

public interface ILocalSpeechModelManager
{
    LocalSpeechModelStatus GetStatus();

    Task<LocalSpeechModelStatus> InstallAsync(
        IProgress<LocalSpeechModelProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(CancellationToken cancellationToken = default);
}

public interface ILocalSpeechRecognizer : IAsyncDisposable
{
    string ModelId { get; }

    IReadOnlyList<SpeechLanguageOption> SupportedLanguages { get; }

    Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the model for <paramref name="sourceLanguage"/> ahead of the first
    /// sentence. Loading a multi-hundred-megabyte local model takes noticeably
    /// longer than one recognition, so a session warms the recognizer up on
    /// start; implementations without a load step keep this no-op default.
    /// </summary>
    Task PrepareAsync(string sourceLanguage, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
