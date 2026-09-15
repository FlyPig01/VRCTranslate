using System.Text.Json;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// Shared wire format of the chat-completions translation services this build
/// ships (DeepSeek and Xiaomi MiMo). Both speak the OpenAI chat-completions
/// protocol with the same `thinking` switch and the same response shape, so the
/// request body, the endpoint and the reply parsing live here once and each
/// provider only names its own defaults and error label.
/// <para>
/// Thinking mode is what a hybrid reasoning model turns on by default; for
/// translation it adds seconds of latency and no quality, so it is explicitly
/// disabled unless the configured model is a reasoner.
/// </para>
/// </summary>
internal static class ChatCompletionsTranslation
{
    /// <summary>Request body kept internal for unit testing of the thinking switch.</summary>
    public static Dictionary<string, object?> BuildRequest(
        TranslationProviderRequest request,
        string defaultModel)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? defaultModel : request.Model.Trim();
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

    public static Uri BuildChatEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + "/chat/completions";
        }

        return builder.Uri;
    }

    /// <summary>
    /// Reads the translated text out of a successful reply. A reply without a
    /// usable choice is named by the caller's service label, so the message a
    /// user sees says which service answered badly.
    /// </summary>
    public static string ReadTranslation(string payload, string serviceLabel)
    {
        using var json = JsonDocument.Parse(payload);
        var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"{serviceLabel} 返回了空结果。");
        return text.Trim();
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
}
