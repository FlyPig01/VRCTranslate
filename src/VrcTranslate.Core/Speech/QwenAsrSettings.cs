using System.Text.Json.Serialization;

namespace VrcTranslate.Core.Speech;

/// <summary>Alibaba Cloud Bailian Qwen LiveTranslate settings.</summary>
public sealed class QwenAsrSettings
{
    public bool Enabled { get; set; }

    /// <summary>Bailian workspace id; it is part of the WebSocket host.</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>cn-beijing or ap-southeast-1.</summary>
    public string Region { get; set; } = "cn-beijing";

    public string Model { get; set; } = "qwen3.8-livetranslate-flash-realtime";

    /// <summary>Subtitle output language; the app always shows Simplified Chinese.</summary>
    public string TargetLanguage { get; set; } = "zh";

    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(WorkspaceId) &&
        !string.IsNullOrWhiteSpace(Model);

    public QwenAsrSettings Clone() => new()
    {
        Enabled = Enabled,
        WorkspaceId = WorkspaceId,
        Region = Region,
        Model = Model,
        TargetLanguage = TargetLanguage,
        ApiKey = ApiKey,
    };
}
