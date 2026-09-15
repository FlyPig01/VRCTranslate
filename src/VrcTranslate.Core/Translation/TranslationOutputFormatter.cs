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

    /// <summary>
    /// The text actually sent to the VRChat chatbox for the user's own message:
    /// the translations, optionally followed by the original Chinese text. The
    /// recognized line is the part a chat partner usually does not need, so it is
    /// the one thing the reader can leave out; the translations - including the
    /// optional second one - always stay.
    /// </summary>
    public static string FormatForChatbox(
        string? originalText,
        string? primaryTranslation,
        string? secondaryTranslation,
        bool includeOriginal)
    {
        // With nothing translated the original is the only content there is:
        // dropping it as well would send an empty chatbox line and erase what the
        // user just typed instead of showing it. A missing translation therefore
        // always falls back to the recognized text.
        var translatedSomething = !string.IsNullOrWhiteSpace(primaryTranslation) ||
                                  !string.IsNullOrWhiteSpace(secondaryTranslation);
        var payload = Format(
            includeOriginal || !translatedSomething ? originalText : null,
            primaryTranslation,
            secondaryTranslation);
        // The chatbox has one length limit, so the optional original can never
        // push the payload past it unseen.
        return TrimForOsc(payload);
    }

    public static string FormatForChatbox(TextTranslationBatchResult result, bool includeOriginal)
    {
        ArgumentNullException.ThrowIfNull(result);
        return FormatForChatbox(
            result.Request.Text,
            result.Primary.TranslatedText,
            result.Secondary?.TranslatedText,
            includeOriginal);
    }

    private static void AddIfPresent(ICollection<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }
}
