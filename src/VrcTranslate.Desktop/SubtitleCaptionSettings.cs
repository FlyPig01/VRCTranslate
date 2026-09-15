using System.Text.Json;
using VrcTranslate.Application.Subtitles;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Desktop;

/// <summary>
/// The caption presentation option lives in the existing voice settings document,
/// so the voice page and the caption surface share one persisted value. The page
/// keeps ownership of the rest of the file; this type maps that single property.
/// </summary>
internal static class SubtitleCaptionSettings
{
    /// <summary>Property name inside <c>v2-voice-settings.json</c>.</summary>
    public const string ContentPropertyName = "SubtitleContent";

    /// <summary>Persisted tag for "仅译文".</summary>
    public const string TranslatedOnlyTag = "translated-only";

    /// <summary>Persisted tag for "译文 + 原文".</summary>
    public const string TranslatedWithOriginalTag = "translated-with-original";

    /// <summary>The long-standing caption shape: translation first, recognized line below.</summary>
    public const SubtitleContentMode Default = SubtitleContentMode.TranslatedWithOriginal;

    public static string ToTag(SubtitleContentMode mode) =>
        mode == SubtitleContentMode.TranslatedOnly ? TranslatedOnlyTag : TranslatedWithOriginalTag;

    public static SubtitleContentMode FromTag(string? tag) =>
        string.Equals(tag?.Trim(), TranslatedOnlyTag, StringComparison.OrdinalIgnoreCase)
            ? SubtitleContentMode.TranslatedOnly
            : Default;

    /// <summary>
    /// Reads the persisted option. A missing, hand-edited or unreadable document
    /// keeps the default instead of blocking the caption surface.
    /// </summary>
    public static SubtitleContentMode Read()
    {
        try
        {
            var path = PortableStorage.GetPath(AppDataFiles.VoiceSettings);
            if (!File.Exists(path))
            {
                return Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(ContentPropertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? FromTag(value.GetString())
                : Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return Default;
        }
    }
}
