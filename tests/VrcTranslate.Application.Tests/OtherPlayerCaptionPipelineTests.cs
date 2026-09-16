using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Captions;
using VrcTranslate.Application.Translation;
using VrcTranslate.Core.Translation;
using Xunit;

namespace VrcTranslate.Application.Tests;

/// <summary>
/// D28/D29 acceptance: other-player captions queue instead of dropping, commit in
/// caption order, and remain local to the subtitle surface.
/// </summary>
public sealed class OtherPlayerCaptionPipelineTests : IAsyncDisposable
{
    private readonly ScriptedProvider _provider = new();
    private readonly RecordingSurface _surface = new();
    private readonly OtherPlayerCaptionPipeline _pipeline;

    public OtherPlayerCaptionPipelineTests()
    {
        _pipeline = new OtherPlayerCaptionPipeline(
            new TranslationService(_provider, new PassThroughGuard()),
            _surface);
    }

    public async ValueTask DisposeAsync() => await _pipeline.DisposeAsync();

    private OtherPlayerCaptionPipeline CreatePipeline(int concurrency, int capacity) => new(
        new TranslationService(_provider, new PassThroughGuard()),
        _surface,
        new OtherPlayerCaptionPipelineOptions { MaxConcurrentTranslations = concurrency, QueueCapacity = capacity });

    [Fact]
    public async Task Pending_captions_appear_immediately_and_a_blocked_translation_drops_nothing()
    {
        await _pipeline.BeginSessionAsync();
        _provider.Gate("A");
        _provider.Gate("B");
        _provider.Gate("C");

        Assert.True(_pipeline.TrySubmit(Input("A")));
        Assert.True(_pipeline.TrySubmit(Input("B")));
        Assert.True(_pipeline.TrySubmit(Input("C")));

        // Recognition results publish synchronously - the whole point of D29 is
        // that a sentence is never lost to an in-flight predecessor.
        Assert.Equal(["A", "B", "C"], _surface.PendingSnapshot().Select(item => item.Text));

        _provider.Succeed("A", "译A");
        _provider.Succeed("B", "译B");
        await TestWait.UntilAsync(() => _provider.StartedCount == 3, "第三句未被调度");
        _provider.Succeed("C", "译C");

        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 3, "三句未全部落定");
        Assert.Equal(
            [("A", "译A"), ("B", "译B"), ("C", "译C")],
            _surface.FilledSnapshot().Select(item => (TextOf(item.Id), item.Translation)));
    }

    [Fact]
    public async Task Commits_follow_caption_order_even_when_translations_finish_out_of_order()
    {
        await _pipeline.BeginSessionAsync();
        foreach (var text in new[] { "A", "B", "C" }) _provider.Gate(text);
        foreach (var text in new[] { "A", "B", "C" }) Assert.True(_pipeline.TrySubmit(Input(text)));
        await TestWait.UntilAsync(() => _provider.StartedCount == 2, "前两句未开始");

        // B finishes first and frees its slot, so C starts running. B is recorded
        // but must not commit while A - the caption before it - is still open.
        _provider.Succeed("B", "译B");
        await TestWait.UntilAsync(() => _provider.StartedCount == 3, "第三句未被调度");
        Assert.Empty(_surface.FilledSnapshot());

        _provider.Succeed("A", "译A");
        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 2, "前两句未落定");
        Assert.Equal(["A", "B"], _surface.FilledSnapshot().Select(item => TextOf(item.Id)));

        _provider.Succeed("C", "译C");
        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 3, "第三句未落定");
        Assert.Equal(["A", "B", "C"], _surface.FilledSnapshot().Select(item => TextOf(item.Id)));
    }

    [Fact]
    public async Task Simultaneous_completions_never_reorder_local_subtitles()
    {
        await using var pipeline = CreatePipeline(concurrency: 16, capacity: 64);

        for (var round = 0; round < 10; round++)
        {
            await pipeline.BeginSessionAsync();
            var firstFilled = _surface.FilledSnapshot().Count;
            var texts = Enumerable.Range(0, 32)
                .Select(index => $"R{round:D2}-{index:D2}")
                .ToArray();
            foreach (var text in texts)
            {
                _provider.Gate(text);
                Assert.True(pipeline.TrySubmit(Input(text)));
            }

            var expectedStarted = ((round + 1) * texts.Length);
            await TestWait.UntilAsync(
                () => _provider.StartedCount >= expectedStarted - 16,
                $"第 {round + 1} 轮并发翻译未启动");
            Parallel.ForEach(texts, text => _provider.Succeed(text, $"译{text}"));
            await TestWait.UntilAsync(
                () => _surface.FilledSnapshot().Count == firstFilled + texts.Length,
                $"第 {round + 1} 轮字幕未全部落定");

            var actual = _surface.FilledSnapshot()
                .Skip(firstFilled)
                .Select(item => TextOf(item.Id));
            Assert.Equal(texts, actual);
        }
    }

    [Fact]
    public async Task One_failed_translation_ends_only_its_own_caption()
    {
        await _pipeline.BeginSessionAsync();
        _provider.Succeed("A", "译A");
        _provider.Fail("B", new InvalidOperationException("服务不可用"));
        _provider.Succeed("C", "译C");
        foreach (var text in new[] { "A", "B", "C" }) Assert.True(_pipeline.TrySubmit(Input(text)));

        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 3, "三句未全部落定");
        var filled = _surface.FilledSnapshot();
        Assert.Equal("译A", filled[0].Translation);
        Assert.Null(filled[1].Translation);
        Assert.Equal("译C", filled[2].Translation);
    }

    [Fact]
    public async Task Echo_profile_publishes_but_never_translates_or_sends()
    {
        await _pipeline.BeginSessionAsync();
        Assert.True(_pipeline.TrySubmit(Input("原声", provider: "echo")));

        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 1, "回显句未落定");
        Assert.Null(_surface.FilledSnapshot().Single().Translation);
        Assert.Empty(_provider.RequestsSnapshot());
    }

    [Fact]
    public async Task Same_text_reply_keeps_one_text_and_sends_nothing()
    {
        await _pipeline.BeginSessionAsync();
        _provider.Succeed("原声", " 原声 ");
        Assert.True(_pipeline.TrySubmit(Input("原声")));

        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 1, "同文句未落定");
        Assert.Null(_surface.FilledSnapshot().Single().Translation);
    }

    [Fact]
    public async Task A_real_translation_is_filled_locally_without_chatbox_truncation()
    {
        await _pipeline.BeginSessionAsync();
        var longTranslation = new string('好', 200);
        _provider.Succeed("短句", longTranslation);
        Assert.True(_pipeline.TrySubmit(Input("短句")));

        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 1, "译文未写入字幕");
        Assert.Equal(longTranslation, _surface.FilledSnapshot().Single().Translation);
    }

    [Fact]
    public async Task Queue_overflow_ends_that_caption_and_reports_diagnostics()
    {
        await using var pipeline = CreatePipeline(concurrency: 1, capacity: 1);
        await pipeline.BeginSessionAsync();
        _provider.Gate("A");
        _provider.Gate("B");
        OtherPlayerCaptionDiagnostics? reported = null;
        pipeline.Diagnostics += (_, args) => reported = args;

        Assert.True(pipeline.TrySubmit(Input("A")));
        await TestWait.UntilAsync(() => _provider.StartedCount == 1, "首句未开始");
        Assert.True(pipeline.TrySubmit(Input("B")));   // occupies the queue slot
        // 队列满：这句仍然发布并拿到终态，不是被静默丢掉——所以返回值是 true。
        Assert.True(pipeline.TrySubmit(Input("C")));

        Assert.Equal(["A", "B", "C"], _surface.PendingSnapshot().Select(item => item.Text));
        Assert.NotNull(reported);
        Assert.Equal("C", TextOf(Assert.IsType<OtherPlayerCaptionDiagnostics>(reported).CaptionId));

        _provider.Succeed("A", "译A");
        await TestWait.UntilAsync(() => _provider.StartedCount == 2, "第二句未被调度");
        _provider.Succeed("B", "译B");
        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 3, "三句未全部落定");

        var filled = _surface.FilledSnapshot();
        Assert.Equal("译A", filled[0].Translation);
        Assert.Equal("译B", filled[1].Translation);
        Assert.Null(filled[2].Translation);
    }

    [Fact]
    public async Task EndSession_cancels_stale_work_without_filling_or_sending()
    {
        await _pipeline.BeginSessionAsync();
        _provider.Gate("A");
        Assert.True(_pipeline.TrySubmit(Input("A")));
        await TestWait.UntilAsync(() => _provider.StartedCount == 1, "翻译未开始");

        await _pipeline.EndSessionAsync();
        _provider.Succeed("A", "译A");
        await Task.Delay(100);

        // A late result of a dead run cannot fill the local surface (D29.4).
        Assert.Empty(_surface.FilledSnapshot());
        Assert.False(_pipeline.TrySubmit(Input("B")));

        // A new run accepts and completes work again.
        await _pipeline.BeginSessionAsync();
        _provider.Succeed("Z", "译Z");
        Assert.True(_pipeline.TrySubmit(Input("Z")));
        await TestWait.UntilAsync(() => _surface.FilledSnapshot().Count == 1, "重启后未落定");
        Assert.Equal("译Z", _surface.FilledSnapshot().Single().Translation);
    }

    [Fact]
    public async Task Route_snapshot_and_source_hint_reach_the_provider()
    {
        await _pipeline.BeginSessionAsync();
        _provider.Succeed("こんにちは", "你好");
        Assert.True(_pipeline.TrySubmit(Input("こんにちは", hint: "ja")));

        await TestWait.UntilAsync(() => _provider.RequestsSnapshot().Count == 1, "翻译请求未发出");
        var request = _provider.RequestsSnapshot().Single();
        Assert.Equal("ja", request.SourceLanguage);
        Assert.Equal("real", request.ProviderId);
        Assert.Equal("model", request.Model);
    }

    private OtherPlayerCaptionInput Input(string text, string? hint = null, string provider = "real") =>
        new(text, "小明", hint, CreateRoute(provider));

    /// <summary>Maps a caption id back to the text it published.</summary>
    private string TextOf(long captionId)
    {
        var pending = _surface.PendingSnapshot().Single(item => item.Id == captionId);
        return pending.Text;
    }

    private static TranslationRoute CreateRoute(string provider) => new(
        "route-test",
        "测试路由",
        new TranslationProfile("profile-1", "档案", provider, "model", new Uri("https://example.test/"), "本地配置"),
        new LanguagePolicy(SourceLanguageMode.AutoDetect, "zh-CN"),
        retryPolicy: new TranslationRetryPolicy(0, TimeSpan.FromSeconds(5)));

    private sealed class PassThroughGuard : IInvariantProtectionGuard
    {
        public ProtectedText Protect(string sourceText, InvariantProtectionPolicy policy) => new(sourceText, []);
        public InvariantValidationResult Validate(ProtectedText protectedText, string translatedText) => InvariantValidationResult.Valid;
    }

    /// <summary>Provider whose answers the test releases by source text.</summary>
    private sealed class ScriptedProvider : ITranslationProvider
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, TaskCompletionSource<TranslationProviderResponse>> _gates = new();
        private int _startedCount;

        public string Id => "scripted";

        public int StartedCount
        {
            get
            {
                lock (_sync) return _startedCount;
            }
        }

        public TaskCompletionSource<TranslationProviderResponse> Gate(string text)
        {
            lock (_sync)
            {
                return _gates.TryGetValue(text, out var existing)
                    ? existing
                    : _gates[text] = new TaskCompletionSource<TranslationProviderResponse>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Succeed(string text, string translation) => Gate(text).TrySetResult(new TranslationProviderResponse(translation));

        public void Fail(string text, Exception exception) => Gate(text).TrySetException(exception);

        public Task<TranslationProviderResponse> TranslateAsync(
            TranslationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<TranslationProviderResponse> gate;
            lock (_sync)
            {
                _startedCount++;
                Requests.Add(request);
                gate = Gate(request.Text);
            }

            // 真实 HTTP 提供商尊重取消令牌（会话停止时立即中断）；替身不尊重
            // 会让 EndSessionAsync 的等待永远不结束。
            if (cancellationToken.CanBeCanceled)
            {
                _ = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            }

            return gate.Task;
        }

        public List<TranslationProviderRequest> Requests { get; } = new();

        public List<TranslationProviderRequest> RequestsSnapshot()
        {
            lock (_sync) return [.. Requests];
        }
    }

    private sealed class RecordingSurface : IOtherPlayerSubtitleSurface
    {
        private readonly object _sync = new();
        private readonly List<(long Id, string Text, string? Label)> _pending = [];
        private readonly List<(long Id, string? Translation)> _filled = [];

        public void PublishPending(long captionId, string text, string? speakerLabel)
        {
            lock (_sync) _pending.Add((captionId, text, speakerLabel));
        }

        public void FillTranslation(long captionId, string? translatedText)
        {
            lock (_sync) _filled.Add((captionId, translatedText));
        }

        public List<(long Id, string Text, string? Label)> PendingSnapshot()
        {
            lock (_sync) return [.. _pending];
        }

        public List<(long Id, string? Translation)> FilledSnapshot()
        {
            lock (_sync) return [.. _filled];
        }
    }

}
