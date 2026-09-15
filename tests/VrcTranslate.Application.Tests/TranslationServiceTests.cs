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
    public async Task Translates_both_configured_targets_concurrently()
    {
        // 每个目标都要 300 ms；串行会 ≥600 ms，并行应当接近 300 ms。
        var provider = new DelayedTargetEchoProvider(TimeSpan.FromMilliseconds(300));
        var service = new TranslationService(provider, new PassThroughGuard());
        var request = new TextTranslationRequest("你好", CreateRoute(), TextTranslationSource.ManualText, sourceLanguageHint: "zh-CN");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.TranslateManyAsync(request, new TranslationTargetSet("en-US", "ja-JP"));

        stopwatch.Stop();
        Assert.Equal(2, provider.StartedPeak);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 450,
            $"两个目标应当是并行请求的，实际耗时 {stopwatch.ElapsedMilliseconds} ms。");
        // 并行不能改变顺序：主/副译文仍然按配置顺序。
        Assert.Equal(["你好 -> en-US", "你好 -> ja-JP"], result.Results.Select(item => item.TranslatedText));
        Assert.Equal("你好 -> en-US / 你好 -> ja-JP / 你好", result.FormattedText);
    }

    [Fact]
    public async Task Keeps_the_primary_translation_when_only_the_second_target_fails()
    {
        var service = new TranslationService(new FailingTargetProvider("ja-JP"), new PassThroughGuard());
        var request = new TextTranslationRequest("你好", CreateRoute(), TextTranslationSource.SpeechRecognition, sourceLanguageHint: "zh-CN");

        var result = await service.TranslateManyAsync(request, new TranslationTargetSet("en-US", "ja-JP"));

        // 副目标失败不再让整句失败：结果收缩成单目标，用户仍然看到并发出主译文。
        Assert.Single(result.Results);
        Assert.Equal("en-US", result.Targets.PrimaryLanguage);
        Assert.Null(result.Targets.SecondaryLanguage);
        Assert.Equal("你好 -> en-US", result.Primary.TranslatedText);
        Assert.Equal("你好 -> en-US / 你好", result.FormattedText);
    }

    [Fact]
    public async Task Fails_the_whole_batch_when_the_primary_target_fails()
    {
        var service = new TranslationService(new FailingTargetProvider("en-US"), new PassThroughGuard());
        var request = new TextTranslationRequest("你好", CreateRoute(), TextTranslationSource.ManualText, sourceLanguageHint: "zh-CN");

        // 主译文是这条消息的主体，没有它就没什么可发，必须让调用方看到失败。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateManyAsync(request, new TranslationTargetSet("en-US", "ja-JP")));
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

    /// <summary>每个请求都等待固定时间，并记录同时进行中的请求峰值。</summary>
    private sealed class DelayedTargetEchoProvider : ITranslationProvider
    {
        private readonly TimeSpan _delay;
        private int _active;
        private int _peak;

        public DelayedTargetEchoProvider(TimeSpan delay) => _delay = delay;

        public int StartedPeak => _peak;
        public string Id => "delayed-target-echo";

        public async Task<TranslationProviderResponse> TranslateAsync(
            TranslationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedMax(ref _peak, active);
            try
            {
                await Task.Delay(_delay, cancellationToken);
                return new TranslationProviderResponse($"{request.Text} -> {request.TargetLanguage}");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }
    }

    /// <summary>只让某一个目标语言失败，用来验证"主目标失败"和"副目标失败"两种语义。</summary>
    private sealed class FailingTargetProvider : ITranslationProvider
    {
        private readonly string _failingTarget;

        public FailingTargetProvider(string failingTarget) => _failingTarget = failingTarget;

        public string Id => "failing-target";

        public Task<TranslationProviderResponse> TranslateAsync(
            TranslationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(request.TargetLanguage, _failingTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"target {_failingTarget} is unavailable");
            }

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
