namespace VrcTranslate.Application.Translation;

/// <summary>The provider returned something that cannot be shown as a caption.</summary>
public sealed class TranslationOutputRejectedException : InvalidOperationException
{
    public TranslationOutputRejectedException(string reason) : base(reason) => Reason = reason;

    public string Reason { get; }
}

/// <summary>
/// Last line of defence between a provider reply and the subtitle surface:
/// removes cosmetic wrappers and rejects obvious non-translations or runaway
/// explanations. It must never turn a legitimate short caption into an error;
/// the length check therefore has a hard cap and a generous ratio.
/// </summary>
public static class TranslationOutputGuard
{
    private static readonly string[] Prefixes =
    [
        "译文：", "译文:", "翻译：", "翻译:", "Translation:", "Translated text:"
    ];

    private static readonly string[] MetaPhrases =
    [
        "作为AI", "作为 AI", "以下是翻译", "以下是译文", "我无法翻译",
        "I cannot translate", "I'm unable to translate"
    ];

    public static string Clean(string? translatedText, string sourceText)
    {
        var text = translatedText?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new TranslationOutputRejectedException("翻译服务返回了空结果。");

        foreach (var prefix in Prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        text = TrimWrappingQuotes(text);
        if (text.Contains('\n') || text.Contains('\r'))
        {
            text = string.Join(' ', text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        text = TrimWrappingQuotes(text.Trim());
        if (text.Length == 0) throw new TranslationOutputRejectedException("翻译服务返回了空结果。");

        foreach (var phrase in MetaPhrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                throw new TranslationOutputRejectedException("翻译服务返回了解释性文字，而不是译文。");
            }
        }

        var sourceLength = CountNonWhitespace(sourceText);
        var translatedLength = CountNonWhitespace(text);
        if (translatedLength > 600 ||
            (sourceLength >= 8 && translatedLength > (sourceLength * 5) + 80))
        {
            throw new TranslationOutputRejectedException("翻译结果异常地长，已放弃显示。");
        }

        return text;
    }

    private static int CountNonWhitespace(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : value.Count(character => !char.IsWhiteSpace(character));

    private static string TrimWrappingQuotes(string text)
    {
        if (text.Length < 2) return text;
        var pairs = new (char Open, char Close)[]
        {
            ('"', '"'), ('“', '”'), ('「', '」'), ('『', '』'), ('\'', '\'')
        };
        foreach (var (open, close) in pairs)
        {
            if (text[0] == open && text[^1] == close)
            {
                return text[1..^1].Trim();
            }
        }

        return text;
    }
}
