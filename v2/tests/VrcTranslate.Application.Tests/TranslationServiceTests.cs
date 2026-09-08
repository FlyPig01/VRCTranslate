using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Translation;
using VrcTranslate.Core.Translation;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class TranslationServiceTests
{
    [Fact]
    public async Task Uses_one_route_for_manual_and_speech_text()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider, new PassThroughGuard());
        var route = CreateRoute();

        var manual = await service.TranslateAsync(new TextTranslationRequest("hello", route, TextTranslationSource.ManualText));
        var speech = await service.TranslateAsync(new TextTranslationRequest("world", route, TextTranslationSource.SpeechRecognition));

        Assert.Equal("HELLO", manual.TranslatedText);
        Assert.Equal("WORLD", speech.TranslatedText);
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal("model", request.Model));
    }

    [Fact]
    public async Task Translates_own_message_to_both_configured_targets()
    {
        var provider = new TargetEchoProvider();
        var service = new TranslationService(provider, new PassThroughGuard());
        var request = new TextTranslationRequest("你好", CreateRoute(), TextTranslationSource.ManualText, sourceLanguageHint: "zh-CN");

        var result = await service.TranslateManyAsync(request, new TranslationTargetSet("en-US", "ja-JP"));

        Assert.Equal(["你好 -> en-US", "你好 -> ja-JP"], result.Results.Select(item => item.TranslatedText));
        Assert.Equal(["en-US", "ja-JP"], provider.Requests.Select(item => item.TargetLanguage));
        Assert.All(provider.Requests, item => Assert.Equal("zh-CN", item.SourceLanguage));
        Assert.Equal("你好 -> en-US / 你好 -> ja-JP / 你好", result.FormattedText);
    }

    [Fact]
    public async Task Formats_a_single_own_message_target_without_an_empty_second_segment()
    {
        var service = new TranslationService(new TargetEchoProvider(), new PassThroughGuard());
        var request = new TextTranslationRequest("你好", CreateRoute(), TextTranslationSource.ManualText,
            sourceLanguageHint: "zh-CN");

        var result = await service.TranslateManyAsync(request, new TranslationTargetSet("en-US"));

        Assert.Equal("你好 -> en-US / 你好", result.FormattedText);
        Assert.DoesNotContain(" /  / ", result.FormattedText);
    }

    [Fact]
    public async Task Rejects_translation_that_changes_invariants()
    {
        var service = new TranslationService(new FakeProvider { Translation = "changed" }, new InvalidGuard());

        await Assert.ThrowsAsync<TranslationInvariantException>(() => service.TranslateAsync(
            new TextTranslationRequest("https://example.test", CreateRoute(), TextTranslationSource.ManualText)));
    }

    [Fact]
    public async Task Retries_transient_provider_failure_using_route_policy()
    {
        var provider = new FlakyProvider();
        var route = CreateRoute(new TranslationRetryPolicy(maxRetries: 1, requestTimeout: TimeSpan.FromSeconds(1)));
        var service = new TranslationService(provider, new PassThroughGuard());

        var result = await service.TranslateAsync(new TextTranslationRequest("hello", route, TextTranslationSource.ManualText));

        Assert.Equal("HELLO", result.TranslatedText);
        Assert.Equal(2, provider.Attempts);
    }

    [Fact]
    public async Task Converts_final_provider_timeout_to_a_readable_timeout_error()
    {
        var route = CreateRoute(new TranslationRetryPolicy(maxRetries: 0, requestTimeout: TimeSpan.FromSeconds(1)));
        var service = new TranslationService(new TimeoutProvider(), new PassThroughGuard());

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            service.TranslateAsync(new TextTranslationRequest("hello", route, TextTranslationSource.ManualText)));

        Assert.Contains("timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TranslationRoute CreateRoute(TranslationRetryPolicy? retryPolicy = null) => new(
        "route-1",
        "Default",
        new TranslationProfile("profile-1", "Local", "provider", "model", new Uri("https://example.test"), "credential"),
        new LanguagePolicy(SourceLanguageMode.AutoDetect, "zh-CN"),
        retryPolicy: retryPolicy);

    private sealed class FakeProvider : ITranslationProvider
    {
        public List<TranslationProviderRequest> Requests { get; } = [];
        public string? Translation { get; init; }
        public string Id => "fake";

        public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new TranslationProviderResponse(Translation ?? request.Text.ToUpperInvariant()));
        }
    }

    private sealed class TargetEchoProvider : ITranslationProvider
    {
        public List<TranslationProviderRequest> Requests { get; } = [];
        public string Id => "target-echo";

        public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new TranslationProviderResponse($"{request.Text} -> {request.TargetLanguage}"));
        }
    }

    private sealed class PassThroughGuard : IInvariantProtectionGuard
    {
        public ProtectedText Protect(string sourceText, InvariantProtectionPolicy policy) => new(sourceText, []);
        public InvariantValidationResult Validate(ProtectedText protectedText, string translatedText) => InvariantValidationResult.Valid;
    }

    private sealed class InvalidGuard : IInvariantProtectionGuard
    {
        public ProtectedText Protect(string sourceText, InvariantProtectionPolicy policy) => new(sourceText, []);
        public InvariantValidationResult Validate(ProtectedText protectedText, string translatedText) =>
            InvariantValidationResult.Invalid([new InvariantViolation(InvariantKind.Url, protectedText.Text, translatedText, "changed")]);
    }

    private sealed class FlakyProvider : ITranslationProvider
    {
        public int Attempts { get; private set; }
        public string Id => "flaky";

        public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) throw new InvalidOperationException("temporary failure");
            return Task.FromResult(new TranslationProviderResponse(request.Text.ToUpperInvariant()));
        }
    }

    private sealed class TimeoutProvider : ITranslationProvider
    {
        public string Id => "timeout";

        public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }
}
