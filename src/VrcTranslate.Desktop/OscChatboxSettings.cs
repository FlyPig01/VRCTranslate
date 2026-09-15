using System.Text.Json;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Desktop;

/// <summary>
/// The one presentation choice of an outgoing chatbox message: whether the
/// recognized original text travels with its translation. It lives in the same
/// user settings document as the OSC host and port, so the settings page and
/// every send path read one file and one property.
/// </summary>
internal static class OscChatboxSettings
{
    /// <summary>Property name inside <c>v2-user-settings.json</c>.</summary>
    public const string IncludeOriginalPropertyName = "IncludeOriginalInOsc";

    /// <summary>
    /// Shipped behaviour: the chatbox line carries translation, optional second
    /// translation and then the original text. Turning the original off stays a
    /// deliberate user choice rather than a silent change.
    /// </summary>
    public const bool DefaultIncludeOriginal = true;

    /// <summary>
    /// Reads the persisted choice. A missing, hand-edited or unreadable document
    /// keeps the shipped behaviour instead of blocking a send.
    /// </summary>
    public static bool ReadIncludeOriginal()
    {
        try
        {
            var path = PortableStorage.GetPath(AppDataFiles.UserSettings);
            if (!File.Exists(path)) return DefaultIncludeOriginal;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(IncludeOriginalPropertyName, out var value) &&
                   value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : DefaultIncludeOriginal;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return DefaultIncludeOriginal;
        }
    }
}
