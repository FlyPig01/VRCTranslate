using System.Text.Json.Serialization;

namespace VrcTranslate.Core.Speech;

/// <summary>Which Tencent Cloud ASR product the user selected.</summary>
public enum TencentAsrMode
{
    /// <summary>Short WAV clips through the one-sentence REST API.</summary>
    Sentence,

    /// <summary>A continuous WebSocket stream with stable sentence results.</summary>
    Realtime
}

/// <summary>
/// User-facing Tencent Cloud ASR configuration. The secret is intentionally
/// excluded from JSON serialization; the desktop/infrastructure layer stores it
/// with Windows DPAPI.
/// </summary>
public sealed class TencentAsrSettings
{
    public bool Enabled { get; set; }

    /// <summary>Sentence or real-time. Older files without this property mean sentence.</summary>
    public TencentAsrMode Mode { get; set; } = TencentAsrMode.Sentence;

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
        Mode = Mode,
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

    /// <summary>Real-time-only Chinese/English engine (large-model 1.0, paid).</summary>
    public const string RealtimeChineseEnglish = "16k_zh_en";

    /// <summary>Real-time-only English game engine (standard, free tier).</summary>
    public const string EnglishGame = "16k_en_game";

    /// <summary>Real-time-only 15-language auto engine (large-model 1.0, paid).</summary>
    public const string MultiLanguage = "16k_multi_lang";

    /// <summary>Engines accepted by the one-sentence (SentenceRecognition) REST API.</summary>
    public static bool IsSentenceRecognitionEngine(string? engine) => engine is
        Chinese or English or Japanese or Korean or ChineseEnglish or Cantonese;

    /// <summary>Engines accepted by the real-time WebSocket API.</summary>
    public static bool IsRealtimeEngine(string? engine) => engine is
        Chinese or English or Japanese or Korean or Cantonese or
        RealtimeChineseEnglish or EnglishGame or MultiLanguage;

    /// <summary>Real-time engines billed as large-model 1.0; they have no free quota.</summary>
    public static bool IsPaidRealtimeEngine(string? engine) =>
        engine is RealtimeChineseEnglish or MultiLanguage;

    /// <summary>Maps any saved value to an engine the selected API accepts.</summary>
    public static string Normalize(string? engine, TencentAsrMode mode)
    {
        if (mode == TencentAsrMode.Realtime)
        {
            return engine switch
            {
                _ when IsRealtimeEngine(engine) => engine!,
                ChineseEnglish => Chinese,
                _ => Japanese,
            };
        }

        return engine switch
        {
            _ when IsSentenceRecognitionEngine(engine) => engine!,
            RealtimeChineseEnglish => ChineseEnglish,
            MultiLanguage => ChineseEnglish,
            EnglishGame => English,
            _ => Japanese,
        };
    }

    /// <summary>Engines offered by the UI for one mode, in display order.</summary>
    public static IReadOnlyList<string> Selectable(TencentAsrMode mode) => mode == TencentAsrMode.Realtime
        ? [Chinese, English, Japanese, Korean, Cantonese, RealtimeChineseEnglish, MultiLanguage]
        : [Chinese, English, Japanese, Korean, ChineseEnglish, Cantonese];

    /// <summary>Caption source language for a fixed engine; auto for mixed-language engines.</summary>
    public static string SourceLanguage(string? engine) => engine switch
    {
        Japanese => "ja",
        Korean => "ko",
        English or EnglishGame => "en",
        Chinese => "zh",
        _ => "auto",
    };

    public static string DisplayName(string? engine) => engine switch
    {
        Chinese => "中文（普通话/方言）",
        English => "英语",
        Japanese => "日语",
        Korean => "韩语",
        ChineseEnglish => "中英粤混合",
        Cantonese => "粤语",
        RealtimeChineseEnglish => "中英混合",
        EnglishGame => "英语（游戏语音）",
        MultiLanguage => "多语种自动",
        _ => "日语",
    };
}
