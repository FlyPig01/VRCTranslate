namespace VrcTranslate.Core.Translation;

/// <summary>
/// The output languages used for a user's own message. The first language is
/// always present; the second one is optional and is rendered beside it when
/// configured. Source text remains Simplified Chinese for this feature.
/// </summary>
public sealed record TranslationTargetSet
{
    public TranslationTargetSet(string primaryLanguage, string? secondaryLanguage = null)
    {
        PrimaryLanguage = Normalize(primaryLanguage, nameof(primaryLanguage));
        SecondaryLanguage = string.IsNullOrWhiteSpace(secondaryLanguage)
            ? null
            : Normalize(secondaryLanguage, nameof(secondaryLanguage));

        if (SecondaryLanguage is not null &&
            string.Equals(PrimaryLanguage, SecondaryLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The second target language must differ from the first.", nameof(secondaryLanguage));
        }
    }

    public string PrimaryLanguage { get; }

    public string? SecondaryLanguage { get; }

    public IReadOnlyList<string> Languages => SecondaryLanguage is null
        ? [PrimaryLanguage]
        : [PrimaryLanguage, SecondaryLanguage];

    public static TranslationTargetSet ChineseToEnglish { get; } = new("en-US");

    private static string Normalize(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A target language is required.", parameterName);
        }

        var normalized = value.Trim().Replace('_', '-');
        if (normalized.Any(char.IsWhiteSpace) || normalized.Length > 35)
        {
            throw new ArgumentException("The language tag is invalid.", parameterName);
        }

        return normalized;
    }
}
