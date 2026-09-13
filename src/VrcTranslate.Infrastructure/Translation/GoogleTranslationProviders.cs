using System.Net.Http.Json;
using System.Text.Json;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

public sealed class GoogleFreeTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    public GoogleFreeTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();
    public string Id => "google-free";

    public async Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : request.SourceLanguage;
        var endpoint = request.Endpoint.ToString().TrimEnd('/');
        var uri = $"{endpoint}/translate_a/single?client=gtx&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(request.TargetLanguage)}&dt=t&q={Uri.EscapeDataString(request.Text)}";
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var translated = string.Concat(json.RootElement[0].EnumerateArray().Select(part => part[0].GetString() ?? string.Empty));
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Google 翻译返回了空结果。");
        var detected = json.RootElement.GetArrayLength() > 2 ? json.RootElement[2].GetString() : source;
        return new TranslationProviderResponse(translated.Trim(), detected);
    }
}

public sealed class GoogleCloudTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    public GoogleCloudTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();
    public string Id => "google-cloud";

    public async Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CredentialReference) || request.CredentialReference == "本地配置")
            throw new InvalidOperationException("请先配置 Google Cloud API 密钥。");
        var endpoint = request.Endpoint.ToString().TrimEnd('/');
        var uri = $"{endpoint}/language/translate/v2?key={Uri.EscapeDataString(request.CredentialReference)}";
        var payload = new { q = new[] { request.Text }, target = request.TargetLanguage, source = string.IsNullOrWhiteSpace(request.SourceLanguage) ? null : request.SourceLanguage, format = "text" };
        using var response = await _httpClient.PostAsJsonAsync(uri, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var translation = json.RootElement.GetProperty("data").GetProperty("translations")[0];
        var translated = translation.GetProperty("translatedText").GetString();
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Google Cloud 翻译返回了空结果。");
        var detected = translation.TryGetProperty("detectedLanguageCode", out var detectedElement) ? detectedElement.GetString() : request.SourceLanguage;
        return new TranslationProviderResponse(System.Net.WebUtility.HtmlDecode(translated).Trim(), detected);
    }
}
