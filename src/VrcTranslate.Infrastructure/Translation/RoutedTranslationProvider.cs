using System.Net.Http.Json;
using System.Text.Json;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>Dispatches a route to the provider selected in its profile.</summary>
public sealed class RoutedTranslationProvider : ITranslationProvider
{
    private readonly TranslationProviderCatalog _catalog;

    public RoutedTranslationProvider(TranslationProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Id => "routed";

    public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var providerId = NormalizeProviderId(request.ProviderId);
        return _catalog.Get(providerId).TranslateAsync(request with { ProviderId = providerId }, cancellationToken);
    }

    private static string NormalizeProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return providerId.Trim().ToLowerInvariant() switch
        {
            // Profiles saved by earlier builds pointed at DeepSeek through the
            // generic OpenAI-compatible mode; they become DeepSeek profiles.
            "openai_compatible" or "openai-compatible" or "multimodal_openai" or "multimodal-openai" => "deepseek",
            "deep-l" => "deepl",
            _ => providerId.Trim()
        };
    }
}

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;

    public DeepLTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();

    public string Id => "deepl";

    public async Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CredentialReference) || request.CredentialReference == "本地配置")
            throw new InvalidOperationException("请先配置 API 密钥。");

        using var message = new HttpRequestMessage(HttpMethod.Post, BuildTranslateEndpoint(request.Endpoint));
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("DeepL-Auth-Key", request.CredentialReference);
        message.Content = JsonContent.Create(new { text = new[] { request.Text }, target_lang = ToDeepLTargetLanguage(request.TargetLanguage) });
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepL 返回 {(int)response.StatusCode}：{payload}");
        using var json = JsonDocument.Parse(payload);
        var text = json.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("DeepL 返回了空结果。");
        return new TranslationProviderResponse(text.Trim(), request.SourceLanguage);
    }

    private static Uri BuildTranslateEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/translate", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + "/translate";
        }

        return builder.Uri;
    }

    private static string ToDeepLTargetLanguage(string targetLanguage)
    {
        var normalized = targetLanguage.Trim().Replace('_', '-');
        var lower = normalized.ToLowerInvariant();
        return lower switch
        {
            "zh-cn" or "zh-sg" or "zh-hans" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "ZH",
            "zh" => "ZH",
            "en" => "EN",
            "en-us" => "EN-US",
            "en-gb" => "EN-GB",
            "pt" or "pt-br" => "PT-BR",
            "pt-pt" => "PT-PT",
            "es" or "es-es" => "ES",
            _ => normalized.Split('-', 2)[0].ToUpperInvariant()
        };
    }
}

/// <summary>
/// Keeps a legacy provider visible in migrated profiles until its native V2
/// adapter is installed. It fails at the operation boundary with an actionable
/// message instead of silently replacing the user's selected service.
/// </summary>
public sealed class UnavailableTranslationProvider : ITranslationProvider
{
    private readonly string _displayName;

    public UnavailableTranslationProvider(string id, string displayName)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Provider id is required.", nameof(id)) : id.Trim();
        _displayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
    }

    public string Id { get; }

    public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new InvalidOperationException($"{_displayName}适配器正在迁移中，请先选择已可用的服务或完成该服务的配置。");
    }
}
