namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// One saved translation service as it is written to the profile document:
/// everything the editor needs, with credentials kept as a reference instead of
/// a secret. It lives beside the provider registry because deciding whether a
/// saved profile can still be served is provider knowledge, not UI state.
/// </summary>
public sealed record TranslationProfileRecord(
    string Id,
    string DisplayName,
    string Provider,
    string Model,
    string Endpoint,
    string CredentialReference,
    string SourceLanguage,
    string TargetLanguage,
    string Region,
    IReadOnlyDictionary<string, string> Options);
