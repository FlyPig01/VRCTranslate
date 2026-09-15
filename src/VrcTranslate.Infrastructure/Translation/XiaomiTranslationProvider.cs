using System.Net.Http.Json;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// Xiaomi MiMo chat-completions translation provider. MiMo speaks the same
/// OpenAI-compatible protocol as DeepSeek (including the `thinking` switch), so
/// only the defaults differ: the platform endpoint, the model id and the error
/// label. The default model is the non-reasoning `mimo-v2.5`; the `-pro` model
/// spends seconds on reasoning that a one-sentence translation never uses.
/// </summary>
public sealed class XiaomiTranslationProvider : ITranslationProvider
{
    public const string DefaultEndpoint = "https://api.xiaomimimo.com/v1";
    public const string DefaultModel = "mimo-v2.5";

    /// <summary>Provider id as written into profiles, routes and the registry.</summary>
    public const string ProviderId = "xiaomi";

    /// <summary>Model id that turns the platform's thinking mode on; kept as a name, not a default.</summary>
    public const string ProModel = "mimo-v2.5-pro";

    private readonly HttpClient _httpClient;

    public XiaomiTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();

    public string Id => ProviderId;

    public async Task<TranslationProviderResponse> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CredentialReference) || request.CredentialReference == "本地配置")
            throw new InvalidOperationException("请先配置小米 MiMo API 密钥。");

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsTranslation.BuildChatEndpoint(request.Endpoint));
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.CredentialReference);
        message.Content = JsonContent.Create(ChatCompletionsTranslation.BuildRequest(request, DefaultModel));
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"小米 MiMo 返回 {(int)response.StatusCode}：{payload}");
        return new TranslationProviderResponse(
            ChatCompletionsTranslation.ReadTranslation(payload, "小米 MiMo"),
            request.SourceLanguage);
    }

    /// <summary>Request body kept internal for unit testing of the thinking switch.</summary>
    internal static Dictionary<string, object?> BuildRequestBody(TranslationProviderRequest request) =>
        ChatCompletionsTranslation.BuildRequest(request, DefaultModel);
}
