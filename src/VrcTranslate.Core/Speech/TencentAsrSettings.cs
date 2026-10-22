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
    public const string ChineseEnglish = "16k_zh_en";
    public const string Cantonese = "16k_yue";
    public const string EnglishGame = "16k_en_game";
    public const string MultiLanguage = "16k_multi_lang";

    /// <summary>Engines covered by the monthly 5-hour standard real-time package.</summary>
    public static bool IsFreeTier(string? engine) => engine is
        Chinese or English or Japanese or Korean or ChineseEnglish or Cantonese or EnglishGame;

    public static string DisplayName(string? engine) => engine switch
    {
        Chinese => "中文（普通话/方言）",
        English => "英语",
        Japanese => "日语",
        Korean => "韩语",
        ChineseEnglish => "中英混合",
        Cantonese => "粤语",
        EnglishGame => "英语（游戏语音）",
        MultiLanguage => "多语种自动（英/日/韩等）",
        _ => "日语",
    };
}
