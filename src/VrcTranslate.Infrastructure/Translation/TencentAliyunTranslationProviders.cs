using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>腾讯云机器翻译（TC3-HMAC-SHA256）适配器。</summary>
public sealed class TencentTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;

    public TencentTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();

    public string Id => "tencent";

    public async Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var secretId = RequireCredential(Option(request, "secret_id") ?? request.CredentialReference, "腾讯云 SecretId");
        var secretKey = Option(request, "secret_key", "secret");
        if (string.IsNullOrWhiteSpace(secretKey) || secretKey.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请配置腾讯云 SecretKey（档案的附加密钥）。");

        var options = request.Options;
        var region = FirstNonEmpty(request.Region, Option(options, "region"), "ap-beijing");
        var service = FirstNonEmpty(Option(options, "service"), "tmt");
        var action = FirstNonEmpty(Option(options, "action"), request.Model, "TextTranslate");
        var version = FirstNonEmpty(Option(options, "version"), "2018-03-21");
        var host = request.Endpoint.IsDefaultPort ? request.Endpoint.Host : request.Endpoint.Authority;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = new Dictionary<string, object?>
        {
            ["SourceText"] = request.Text,
            ["Source"] = MapTencentLanguage(request.SourceLanguage, true),
            ["Target"] = MapTencentLanguage(request.TargetLanguage, false)
        };
        if (int.TryParse(Option(options, "project_id"), out var projectId)) body["ProjectId"] = projectId;
        var payload = JsonSerializer.Serialize(body);
        var contentHash = Sha256Hex(payload);
        var canonicalHeaders = $"content-type:application/json\nhost:{host}\nx-tc-action:{action.ToLowerInvariant()}\nx-tc-region:{region}\nx-tc-timestamp:{timestamp}\nx-tc-version:{version}\n";
        var signedHeaders = "content-type;host;x-tc-action;x-tc-region;x-tc-timestamp;x-tc-version";
        var canonicalUri = string.IsNullOrEmpty(request.Endpoint.AbsolutePath) ? "/" : request.Endpoint.AbsolutePath;
        var canonicalQuery = request.Endpoint.Query.TrimStart('?');
        var canonicalRequest = $"POST\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{contentHash}";
        var credentialScope = $"{date}/{service}/tc3_request";
        var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";
        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = HmacSha256(secretDate, service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
        message.Headers.Host = host;
        message.Headers.TryAddWithoutValidation("X-TC-Action", action);
        message.Headers.TryAddWithoutValidation("X-TC-Version", version);
        message.Headers.TryAddWithoutValidation("X-TC-Region", region);
        message.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("Authorization", $"TC3-HMAC-SHA256 Credential={secretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
        message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"腾讯云翻译返回 {(int)response.StatusCode}：{responsePayload}");
        using var json = JsonDocument.Parse(responsePayload);
        if (json.RootElement.TryGetProperty("Response", out var error) && error.TryGetProperty("Error", out var apiError))
            throw new InvalidOperationException($"腾讯云翻译失败：{apiError.GetProperty("Message").GetString()}");
        var translated = json.RootElement.GetProperty("Response").GetProperty("TargetText").GetString();
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("腾讯云翻译返回了空结果。");
        return new TranslationProviderResponse(translated.Trim(), request.SourceLanguage);
    }

    private static string MapTencentLanguage(string? language, bool source)
    {
        if (source && string.IsNullOrWhiteSpace(language)) return "auto";
        var value = (language ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        return value switch
        {
            "zh-cn" or "zh-hans" or "zh" => "zh",
            "zh-tw" or "zh-hant" or "zh-hk" => "zh-TW",
            "en-us" or "en-gb" or "en" => "en",
            "ja-jp" or "ja" => "ja",
            "ko-kr" or "ko" => "ko",
            _ => value.Split('-', 2)[0]
        };
    }

    internal static string RequireCredential(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "本地配置" || value.StartsWith("vault:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"请先配置{name}。");
        return value.Trim();
    }

    internal static string? Option(TranslationProviderRequest request, params string[] keys) => Option(request.Options, keys);
    internal static string? Option(IReadOnlyDictionary<string, string>? options, params string[] keys)
    {
        if (options is null) return null;
        foreach (var key in keys)
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    internal static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))!.Trim();
    internal static byte[] HmacSha256(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    internal static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>阿里云机器翻译 RPC API（HMAC-SHA1）适配器。</summary>
public sealed class AliyunTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    public AliyunTranslationProvider(HttpClient? httpClient = null) => _httpClient = httpClient ?? new HttpClient();
    public string Id => "aliyun";

    public async Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accessKeyId = TencentTranslationProvider.RequireCredential(TencentTranslationProvider.Option(request, "access_key_id") ?? request.CredentialReference, "阿里云 AccessKey ID");
        var accessKeySecret = TencentTranslationProvider.Option(request, "access_key_secret", "secret");
        if (string.IsNullOrWhiteSpace(accessKeySecret) || accessKeySecret.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请配置阿里云 AccessKey Secret（档案的附加密钥）。");
        var options = request.Options;
        var scene = ResolveScene(request.Model, TencentTranslationProvider.Option(options, "scene"));
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = accessKeyId,
            ["Action"] = TencentTranslationProvider.FirstNonEmpty(TencentTranslationProvider.Option(options, "action"), "TranslateGeneral"),
            ["Format"] = "JSON",
            ["RegionId"] = TencentTranslationProvider.FirstNonEmpty(request.Region, TencentTranslationProvider.Option(options, "region"), "cn-hangzhou"),
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
            ["SignatureVersion"] = "1.0",
            ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["Version"] = TencentTranslationProvider.FirstNonEmpty(TencentTranslationProvider.Option(options, "version"), "2018-10-12"),
            ["SourceLanguage"] = MapAliyunLanguage(request.SourceLanguage, true),
            ["TargetLanguage"] = MapAliyunLanguage(request.TargetLanguage, false),
            ["SourceText"] = request.Text,
            ["Scene"] = scene
        };
        var canonicalized = string.Join("&", parameters.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{Encode(item.Key)}={Encode(item.Value)}"));
        var stringToSign = $"POST&%2F&{Encode(canonicalized)}";
        parameters["Signature"] = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(accessKeySecret + "&"), Encoding.UTF8.GetBytes(stringToSign)));
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Endpoint);
        message.Content = new FormUrlEncodedContent(parameters);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"阿里云翻译返回 {(int)response.StatusCode}：{responsePayload}");
        using var json = JsonDocument.Parse(responsePayload);
        if (json.RootElement.TryGetProperty("Code", out var code) && !string.Equals(code.GetString(), "200", StringComparison.OrdinalIgnoreCase) && !string.Equals(code.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            // The machine-readable code must ride along: the connection-test
            // hints match on it, and the raw message alone is often too vague.
            var errorMessage = json.RootElement.TryGetProperty("Message", out var msg) ? msg.GetString() : null;
            throw new InvalidOperationException($"阿里云翻译失败：[{code.GetString()}] {errorMessage}".Trim());
        }
        var translated = json.RootElement.TryGetProperty("Data", out var data) && data.TryGetProperty("Translated", out var translatedElement)
            ? translatedElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("阿里云翻译返回了空结果。");
        return new TranslationProviderResponse(translated.Trim(), request.SourceLanguage);
    }

    /// <summary>
    /// Which Aliyun machine-translation edition this profile uses. An explicit
    /// <c>scene</c> option wins; otherwise the profile's model carries it, because
    /// the translation page now saves the chosen edition there as well. Anything
    /// else is the general edition.
    /// </summary>
    internal static string ResolveScene(string? model, string? configuredScene) =>
        TencentTranslationProvider.FirstNonEmpty(
            configuredScene,
            string.Equals(model?.Trim(), "professional", StringComparison.OrdinalIgnoreCase) ? "professional" : null,
            "general");

    private static string MapAliyunLanguage(string? language, bool source)
    {
        if (source && string.IsNullOrWhiteSpace(language)) return "auto";
        var value = (language ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        return value switch
        {
            "zh-cn" or "zh-hans" or "zh" => "zh",
            "zh-tw" or "zh-hant" => "zh-tw",
            "en-us" or "en-gb" or "en" => "en",
            "ja-jp" or "ja" => "ja",
            "ko-kr" or "ko" => "ko",
            _ => value.Split('-', 2)[0]
        };
    }

    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);
}
