namespace VrcTranslate.Core.Translation;

public sealed record TranslationProfile
{
    public TranslationProfile(
        string profileId,
        string displayName,
        string provider,
        string model,
        Uri endpoint,
        string credentialReference,
        bool supportsAutoDetection = true,
        bool isVerified = false,
        DateTimeOffset? lastVerifiedAt = null,
        string region = "",
        IReadOnlyDictionary<string, string>? options = null)
    {
        ProfileId = Required(profileId, nameof(profileId));
        DisplayName = Required(displayName, nameof(displayName));
        Provider = Required(provider, nameof(provider));
        Model = Required(model, nameof(model));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        CredentialReference = Required(credentialReference, nameof(credentialReference));
        SupportsAutoDetection = supportsAutoDetection;
        IsVerified = isVerified;
        LastVerifiedAt = lastVerifiedAt;
        Region = region?.Trim() ?? string.Empty;
        Options = options is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);

        if (isVerified && lastVerifiedAt is null)
        {
            throw new ArgumentException("A verified profile must include its verification time.", nameof(lastVerifiedAt));
        }
    }

    public string ProfileId { get; }
    public string DisplayName { get; }
    public string Provider { get; }
    public string Model { get; }
    public Uri Endpoint { get; }
    public string CredentialReference { get; }
    public bool SupportsAutoDetection { get; }
    public bool IsVerified { get; }
    public DateTimeOffset? LastVerifiedAt { get; }
    public string Region { get; }
    public IReadOnlyDictionary<string, string> Options { get; }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
    }
}
