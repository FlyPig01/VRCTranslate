namespace VrcTranslate.Core.Translation;

public enum SourceLanguageMode
{
    AutoDetect,
    Fixed,
    FollowRecognizer
}

/// <summary>Defines the language direction shared by text and speech translation.</summary>
public sealed record LanguagePolicy
{
    public LanguagePolicy(SourceLanguageMode sourceMode, string targetLanguage, string? sourceLanguage = null)
    {
        if (!Enum.IsDefined(sourceMode))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceMode));
        }

        TargetLanguage = NormalizeLanguage(targetLanguage, nameof(targetLanguage));
        SourceMode = sourceMode;
        SourceLanguage = sourceMode == SourceLanguageMode.Fixed
            ? NormalizeLanguage(sourceLanguage, nameof(sourceLanguage))
            : null;

        if (sourceMode == SourceLanguageMode.Fixed && SourceLanguage is null)
        {
            throw new ArgumentException("A fixed source language is required.", nameof(sourceLanguage));
        }

        if (sourceMode != SourceLanguageMode.Fixed && sourceLanguage is not null)
        {
            throw new ArgumentException("SourceLanguage is only valid for a fixed source mode.", nameof(sourceLanguage));
        }
    }

    public SourceLanguageMode SourceMode { get; }

    public string? SourceLanguage { get; }

    public string TargetLanguage { get; }

    public string? ResolveSourceLanguage(string? recognizedLanguage = null)
    {
        return SourceMode switch
        {
            SourceLanguageMode.Fixed => SourceLanguage,
            SourceLanguageMode.FollowRecognizer or SourceLanguageMode.AutoDetect => recognizedLanguage is null
                ? null
                : NormalizeLanguage(recognizedLanguage, nameof(recognizedLanguage)),
            _ => null
        };
    }

    private static string NormalizeLanguage(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A language tag is required.", parameterName);
        }

        var normalized = value.Trim().Replace('_', '-');
        if (normalized.Any(char.IsWhiteSpace) || normalized.Length > 35)
        {
            throw new ArgumentException("The language tag is invalid.", parameterName);
        }

        return normalized;
    }
}
