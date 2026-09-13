namespace VrcTranslate.Core.Translation;

/// <summary>
/// Builds the compact message shown in the own-input preview and sent to the
/// VRChat Chatbox. Keeping this in Core makes every entry point use the same
/// ordering and separator, regardless of whether the source came from text or
/// speech recognition.
/// </summary>
public static class TranslationOutputFormatter
{
    public const string Separator = " / ";
    public const int OscChatboxMaxUtf16Length = 144;

    /// <summary>
    /// Formats output as primary translation, optional secondary translation,
    /// then the original Chinese text. Empty optional values are omitted.
    /// </summary>
    public static string Format(
        string? originalText,
        string? primaryTranslation,
        string? secondaryTranslation = null)
    {
        var parts = new List<string>(capacity: 3);
        AddIfPresent(parts, primaryTranslation);
        AddIfPresent(parts, secondaryTranslation);
        AddIfPresent(parts, originalText);
        return string.Join(Separator, parts);
    }

    public static string Format(TextTranslationBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Format(
            result.Request.Text,
            result.Primary.TranslatedText,
            result.Secondary?.TranslatedText);
    }

    /// <summary>
    /// Applies the VRChat Chatbox UTF-16 limit without cutting a surrogate pair.
    /// </summary>
    public static string TrimForOsc(
        string? text,
        int maxUtf16Length = OscChatboxMaxUtf16Length)
    {
        if (maxUtf16Length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf16Length));
        }

        var value = text?.Trim() ?? string.Empty;
        if (value.Length <= maxUtf16Length) return value;

        var length = maxUtf16Length;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    public static string FormatForOsc(TextTranslationBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return TrimForOsc(Format(result));
    }

    private static void AddIfPresent(ICollection<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }
}
