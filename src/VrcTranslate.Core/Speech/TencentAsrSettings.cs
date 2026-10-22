using System.Text.Json.Serialization;

namespace VrcTranslate.Core.Speech;

/// <summary>
/// User-facing Tencent Cloud ASR configuration. The secret is intentionally
/// excluded from JSON serialization; the desktop/infrastructure layer stores it
/// with Windows DPAPI.
/// </summary>
public sealed class TencentAsrSettings
{
    public bool Enabled { get; set; }

    /// <summary>Tencent engine id, for example 16k_ja. Kept internal; the UI maps friendly names.</summary>
    public string Engine { get; set; } = "16k_ja";

    public bool FallbackToLocal { get; set; } = true;

    public string AppId { get; set; } = string.Empty;

    public string SecretId { get; set; } = string.Empty;

    [JsonIgnore]
    public string SecretKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) &&
        !string.IsNullOrWhiteSpace(SecretId) &&
        !string.IsNullOrWhiteSpace(SecretKey);

    public TencentAsrSettings Clone() => new()
    {
        Enabled = Enabled,
        Engine = Engine,
        FallbackToLocal = FallbackToLocal,
        AppId = AppId,
        SecretId = SecretId,
        SecretKey = SecretKey,
    };
}

public static class TencentAsrEngines
{
    public const string Chinese = "16k_zh";
    public const string English = "16k_en";
    public const string Japanese = "16k_ja";
    public const string Korean = "16k_ko";

    /// <summary>
    /// One-sentence recognition's Chinese/English/Cantonese engine. The real-time
    /// API uses 16k_zh_en for a similar role, but that engine is billed as a
    /// large-model engine and is not accepted by SentenceRecognition.
    /// </summary>
    public const string ChineseEnglish = "16k_zh-PY";

    public const string Cantonese = "16k_yue";

    // Real-time-only engines, kept here for the next phase. They are never
    // offered by the one-sentence UI because SentenceRecognition rejects them.
    public const string RealtimeChineseEnglish = "16k_zh_en";
    public const string EnglishGame = "16k_en_game";
    public const string MultiLanguage = "16k_multi_lang";

    /// <summary>Engines accepted by the one-sentence (SentenceRecognition) REST API.</summary>
    public static bool IsSentenceRecognitionEngine(string? engine) => engine is
        Chinese or English or Japanese or Korean or ChineseEnglish or Cantonese;

    /// <summary>
    /// Maps values written by older builds to an engine the current one-sentence
    /// API accepts. Unknown/empty values fall back to Japanese, the historical
    /// default.
    /// </summary>
    public static string NormalizeForSentenceRecognition(string? engine) => engine switch
    {
        _ when IsSentenceRecognitionEngine(engine) => engine!,
        RealtimeChineseEnglish => ChineseEnglish,
        MultiLanguage => ChineseEnglish,
        EnglishGame => English,
        _ => Japanese,
    };

    public static string DisplayName(string? engine) => engine switch
    {
        Chinese => "中文（普通话/方言）",
        English => "英语",
        Japanese => "日语",
        Korean => "韩语",
        ChineseEnglish => "中英粤混合",
        Cantonese => "粤语",
        RealtimeChineseEnglish => "中英混合（仅实时）",
        EnglishGame => "英语（游戏语音，仅实时）",
        MultiLanguage => "多语种自动（仅实时，付费）",
        _ => "日语",
    };
}
