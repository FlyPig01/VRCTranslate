using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Tencent Cloud one-sentence recognition (SentenceRecognition) over the
/// TC3-HMAC-SHA256 JSON API. It accepts the short VAD segments already produced
/// by the capture session, so speaker labels and the existing caption pipeline
/// remain unchanged while cloud accuracy is evaluated.
/// </summary>
public sealed class TencentAsrClient : ITencentSpeechRecognizer
{
    private const string Endpoint = "https://asr.tencentcloudapi.com";
    private const string Host = "asr.tencentcloudapi.com";
    private const string Service = "asr";
    private const string Action = "SentenceRecognition";
    private const string Version = "2019-06-14";
    private const string Region = "ap-shanghai";

    private readonly TencentAsrSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public TencentAsrClient(TencentAsrSettings settings, HttpClient? http = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
            throw new TencentAsrException("NotConfigured", "腾讯云 ASR 尚未配置 AppID/SecretId/SecretKey。");
        if (!IsSupportedEngine(_settings.Engine))
            throw new TencentAsrException("InvalidEngine", "不支持的腾讯云识别语言。");

        var stopwatch = Stopwatch.StartNew();
        var wav = ToWav(request.Samples.Span, request.SampleRate);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ProjectId"] = 0,
            ["SubServiceType"] = 2,
            ["EngSerViceType"] = _settings.Engine,
            ["SourceType"] = 1,
            ["VoiceFormat"] = "wav",
            ["Data"] = Convert.ToBase64String(wav),
            ["DataLen"] = wav.Length,
            ["WordInfo"] = 0,
            ["FilterDirty"] = 0,
            ["FilterModal"] = 1,
            ["FilterPunc"] = 0,
            ["ConvertNumMode"] = 1,
        });

        var text = await SendAsync(payload, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return new SpeechRecognitionResult(
            request.RequestId,
            text,
            request.SourceLanguage,
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Sends a short silent sample only to verify the signature/credentials.
    /// It never logs the request body or the keys.
    /// </summary>
    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        var silence = new float[16_000];
        var request = new SpeechRecognitionRequest(silence, 16_000, "ja", "tencent-test");
        try
        {
            _ = await RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TencentAsrException exception) when (!exception.IsAuthenticationFailure)
        {
            // A silent sample may legitimately produce no speech/recognition
            // error. Reaching the API without an auth failure proves the
            // credentials and signature are accepted.
        }
    }

    private async Task<string> SendAsync(string payload, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        const string contentType = "application/json; charset=utf-8";

        var hashedPayload = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var canonicalHeaders = $"content-type:{contentType}\nhost:{Host}\nx-tc-action:{Action.ToLowerInvariant()}\n";
        var signedHeaders = "content-type;host;x-tc-action";
        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{hashedPayload}";
        var credentialScope = $"{date}/{Service}/tc3_request";
        var stringToSign =
            $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + _settings.SecretKey), date);
        var secretService = HmacSha256(secretDate, Service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = Hex(HmacSha256(secretSigning, stringToSign));
        var authorization =
            $"TC3-HMAC-SHA256 Credential={_settings.SecretId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("X-TC-Action", Action);
        message.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("X-TC-Version", Version);
        message.Headers.TryAddWithoutValidation("X-TC-Region", Region);
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TencentAsrException($"HTTP{(int)response.StatusCode}", "腾讯云请求失败，请检查网络后重试。");

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("Response", out var result))
            throw new TencentAsrException("InvalidResponse", "腾讯云返回格式异常。");
        if (result.TryGetProperty("Error", out var error))
        {
            var code = error.TryGetProperty("Code", out var c) ? c.GetString() ?? "Error" : "Error";
            var errorMessage = error.TryGetProperty("Message", out var m) ? m.GetString() ?? "腾讯云识别失败。" : "腾讯云识别失败。";
            throw new TencentAsrException(code, errorMessage);
        }

        return result.TryGetProperty("Result", out var text) ? text.GetString() ?? string.Empty : string.Empty;
    }

    private static bool IsSupportedEngine(string? engine) =>
        TencentAsrEngines.IsSentenceRecognitionEngine(engine);

    private static byte[] ToWav(ReadOnlySpan<float> samples, int sampleRate)
    {
        var dataLength = samples.Length * sizeof(short);
        var buffer = new byte[44 + dataLength];
        var span = buffer.AsSpan();
        WriteAscii(span[..4], "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], 36 + dataLength);
        WriteAscii(span[8..12], "WAVE");
        WriteAscii(span[12..16], "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..22], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..24], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..32], sampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..34], sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..36], 16);
        WriteAscii(span[36..40], "data");
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], dataLength);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = Math.Clamp(samples[i], -1f, 1f);
            var pcm = (short)Math.Round(value * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], pcm);
        }

        return buffer;
    }

    private static void WriteAscii(Span<byte> target, string value) =>
        Encoding.ASCII.GetBytes(value, target);

    private static byte[] HmacSha256(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
