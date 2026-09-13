using System.Net.Http.Json;
using System.Text.Json;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// DeepSeek chat-completions translation provider. The official endpoint and
/// model names are preset; only the API key is required. The default
/// deepseek-flash model (DeepSeek-V4.1-Flash) is natively multimodal and
/// enables thinking mode by default, which adds seconds of latency and no
/// quality for translation, so thinking is explicitly disabled unless the
/// user picks a reasoner model.
/// </summary>
public sealed class DeepSeekTranslationProvider : ITranslationProvider
{
    public const string DefaultEndpoint = "https://api.deepseek.com";
    public const string DefaultModel = "deepseek-flash";

    private readonly HttpClient _httpClient;

    public DeepSeekTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();

    public string Id => "deepseek";

    public async Task<TranslationProviderResponse> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CredentialReference) || request.CredentialReference == "本地配置")
            throw new InvalidOperationException("请先配置 DeepSeek API 密钥。");

        using var message = new HttpRequestMessage(HttpMethod.Post, BuildChatEndpoint(request.Endpoint));
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.CredentialReference);
        message.Content = JsonContent.Create(BuildRequestBody(request));
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek 返回 {(int)response.StatusCode}：{payload}");
        using var json = JsonDocument.Parse(payload);
        var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("DeepSeek 返回了空结果。");
        return new TranslationProviderResponse(text.Trim(), request.SourceLanguage);
    }

    /// <summary>Request body kept internal for unit testing of the thinking switch.</summary>
    internal static Dictionary<string, object?> BuildRequestBody(TranslationProviderRequest request)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel : request.Model.Trim();
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(request) },
                new { role = "user", content = request.Text }
            }
        };

        if (WantsThinking(model))
        {
            // Thinking mode is the default on hybrid models; a reasoner model
            // is an explicit opt-in, and thinking mode rejects temperature.
            body["thinking"] = new Dictionary<string, string?> { ["type"] = "enabled" };
        }
        else
        {
            body["thinking"] = new Dictionary<string, string?> { ["type"] = "disabled" };
            body["temperature"] = 0.1;
        }

        return body;
    }

    private static bool WantsThinking(string model) =>
        model.Contains("reasoner", StringComparison.OrdinalIgnoreCase);

    private static string BuildSystemPrompt(TranslationProviderRequest request)
    {
        var source = string.IsNullOrWhiteSpace(request.SourceLanguage)
            ? "Detect the source language"
            : $"The source language is {request.SourceLanguage}";
        return $"Translate the user's text into {request.TargetLanguage}. {source}. Return only the translation, without explanations.";
    }

    private static Uri BuildChatEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + "/chat/completions";
        }

        return builder.Uri;
    }
}
