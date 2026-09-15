using System.Net;
using VrcTranslate.Infrastructure.Translation;
using Xunit;
using Abstractions = VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Connection-test behaviour: the provider error signatures must turn into
/// actionable Chinese hints, and the tester must stay opt-in (a failure is a
/// result, never an exception the save path would see).
/// </summary>
public sealed class TranslationConnectionTests
{
    [Theory]
    [InlineData("tencent", "腾讯云翻译失败：[AuthFailure.SignatureFailure] sig mismatch", "SecretId 与 SecretKey 不匹配")]
    [InlineData("tencent", "腾讯云翻译失败：[AuthFailure.SecretIdNotFound] no such id", "SecretId 不存在或已删除")]
    [InlineData("tencent", "腾讯云翻译失败：[FailedOperation.NoFreeAmount] quota gone", "免费额度已用完")]
    [InlineData("tencent", "腾讯云翻译失败：[LimitExceeded] too fast", "免费额度已用完")]
    [InlineData("tencent", "腾讯云翻译失败：[FailedOperation.UserNotRegistered] enroll first", "还没有开通「机器翻译 TMT」")]
    [InlineData("aliyun", "阿里云翻译失败：[InvalidAccessKeyId.NotFound] no key", "AccessKey ID 不存在")]
    // 该适配器把非 200 的 RPC 响应抛成 HTTP 异常，错误码只在响应体里——提示必须也能从这种消息里认出来。
    [InlineData("aliyun", "阿里云翻译返回 404：{Code:InvalidAccessKeyId.NotFound,Message:Specified access key is not found.}", "AccessKey ID 不存在")]
    [InlineData("aliyun", "阿里云翻译失败：[SignatureDoesNotMatch] bad sig", "AccessKey Secret 不匹配")]
    [InlineData("aliyun", "阿里云翻译失败：[Throttling.User] slow down", "触发限流")]
    [InlineData("aliyun", "阿里云翻译失败：[Forbidden] no permission", "子账号未授权")]
    [InlineData("aliyun", "阿里云翻译失败：[InvalidAction] unknown action", "子账号未授权")]
    [InlineData("deepseek", "DeepSeek 返回 401：Authentication Fails", "API Key 无效或已被撤销")]
    [InlineData("xiaomi", "小米 MiMo 返回 402：余额不足", "余额不足")]
    [InlineData("deepseek", "DeepSeek 返回 429：rate limit", "触发限流")]
    [InlineData("deepseek", "DeepSeek 返回 400：invalid model", "请求被拒绝")]
    [InlineData("xiaomi", "小米 MiMo 返回 403：forbidden", "账号无权调用该模型")]
    [InlineData("xiaomi", "小米 MiMo 返回 401：bad key", "API Key 无效")]
    public void Hints_map_provider_error_signatures(string providerId, string message, string expected)
    {
        Assert.True(TranslationErrorHints.TryDescribe(providerId, new InvalidOperationException(message), out var hint));
        Assert.Contains(expected, hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_errors_have_no_hint_instead_of_a_wrong_one()
    {
        Assert.False(TranslationErrorHints.TryDescribe("tencent", new InvalidOperationException("别的错误"), out _));
    }

    [Fact]
    public void Network_failures_map_to_a_generic_hint_for_every_provider()
    {
        foreach (var providerId in new[] { "tencent", "aliyun", "deepseek", "xiaomi" })
        {
            Assert.True(TranslationErrorHints.TryDescribe(providerId, new HttpRequestException("No such host"), out var hint));
            Assert.Contains("网络不通", hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Echo_provider_round_trips_as_a_successful_test()
    {
        var request = new Abstractions.TranslationProviderRequest(
            "你好", "en", "zh-CN", "本地回显", new Uri("https://localhost/echo"), "本地配置", "test", "echo");
        var result = await TranslationConnectionTester.TestAsync(new EchoTranslationProvider(), request);

        Assert.True(result.Succeeded);
        Assert.Equal("你好", result.Translation);
        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task Tester_carries_hint_and_raw_detail_on_failure()
    {
        var throwing = new ThrowingProvider(new InvalidOperationException("DeepSeek 返回 401：bad key"));
        var request = new Abstractions.TranslationProviderRequest(
            "你好", "en", null, "x", new Uri("https://localhost"), "k", "t", "deepseek");

        var result = await TranslationConnectionTester.TestAsync(throwing, request);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Hint);
        Assert.Contains("401", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aliyun_provider_now_reports_the_machine_code_in_its_error()
    {
        var handler = new FixedResponseHandler(
            "{\"Code\":\"InvalidAccessKeyId.NotFound\",\"Message\":\"指定的Access Key不存在\"}", HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var provider = new AliyunTranslationProvider(client);
        var request = new Abstractions.TranslationProviderRequest(
            "hello", "en", null, "general", new Uri("https://example.test"), "access-id", "t", "aliyun",
            "cn-hangzhou", new Dictionary<string, string> { ["secret"] = "access-secret" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(request));

        Assert.Contains("[InvalidAccessKeyId.NotFound]", error.Message, StringComparison.Ordinal);
        Assert.Contains("指定的Access Key不存在", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("professional", null, "professional")]
    [InlineData("general", null, "general")]
    [InlineData(null, "professional", "professional")]
    [InlineData("PROFESSIONAL", null, "professional")]
    [InlineData("", null, "general")]
    [InlineData(null, null, "general")]
    // 显式 scene 优先于 Model：老档案里 scene 才是权威。
    [InlineData("professional", "general", "general")]
    public void Aliyun_scene_follows_the_profile_edition(string? model, string? configuredScene, string expected)
    {
        Assert.Equal(expected, AliyunTranslationProvider.ResolveScene(model, configuredScene));
    }

    [Theory]
    [InlineData("general", "Action=TranslateGeneral", "Scene=general")]
    [InlineData("professional", "Action=Translate", "Scene=social")]
    public async Task Aliyun_request_uses_the_action_and_domain_of_the_selected_edition(
        string model, string expectedAction, string expectedScene)
    {
        var handler = new CapturingHandler("{\"Code\":\"200\",\"Data\":{\"Translated\":\"你好\"}}");
        using var client = new HttpClient(handler);
        var provider = new AliyunTranslationProvider(client);
        var request = new Abstractions.TranslationProviderRequest(
            "hello", "zh-CN", null, model, new Uri("https://example.test"), "access-id", "t", "aliyun",
            "cn-hangzhou", new Dictionary<string, string> { ["secret"] = "access-secret" });

        await provider.TranslateAsync(request);

        // 通用版与专业版是不同的 Action；专业版的 Scene 必须是领域值（professional 会被拒 10004）。
        Assert.Contains(expectedAction, handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(expectedScene, handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene=professional", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tencent_signed_content_type_matches_the_sent_header()
    {
        // 签名覆盖 content-type 头的完整值：StringContent 会写成 "application/json; charset=utf-8"，
        // 一旦签名只写 "application/json"，服务端只会回 AuthFailure.SignatureFailure（实测踩过）。
        var handler = new CapturingHandler("{\"Response\":{\"TargetText\":\"你好\"}}", captureContentType: true);
        using var client = new HttpClient(handler);
        var provider = new TencentTranslationProvider(client);
        var request = new Abstractions.TranslationProviderRequest(
            "hello", "zh", "en", "TextTranslate", new Uri("https://tmt.tencentcloudapi.com"), "AKIDEXAMPLE", "t", "tencent",
            "ap-beijing", new Dictionary<string, string> { ["secret"] = "secret-value" });

        await provider.TranslateAsync(request);

        // 请求体必须带 ProjectId（少了它同样是 SignatureFailure）。
        Assert.Contains("\"ProjectId\":0", handler.LastBody, StringComparison.Ordinal);
        // 实发的 content-type 必须与签名里用的那一个逐字相同（含 charset）。
        Assert.Equal(TencentTranslationProvider.SignedContentType, handler.SentContentType);
    }

    private sealed class CapturingHandler(string payload, bool captureContentType = false) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = string.Empty;
        public string SentContentType { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (captureContentType)
            {
                SentContentType = request.Content.Headers.ContentType?.ToString() ?? string.Empty;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }

    private sealed class ThrowingProvider(Exception exception) : ITranslationProvider
    {
        public string Id => "throwing";

        public Task<Abstractions.TranslationProviderResponse> TranslateAsync(
            Abstractions.TranslationProviderRequest request, CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class FixedResponseHandler(string payload, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(payload) });
        }
    }
}
