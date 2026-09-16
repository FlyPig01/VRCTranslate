using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class LocalSpeechCaptureSessionTests
{
    [Fact]
    public async Task Captured_sentence_is_segmented_and_sent_to_local_service()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        await using var session = new LocalSpeechCaptureSession(
            capture,
            CreateSpeechService(),
            "en",
            new SpeechSegmenter(16_000, 0.01f, 100, 2_000));
        var result = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) => result.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        var recognized = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("test", recognized.Text);
        Assert.Equal("en", recognized.SourceLanguage);
        Assert.True(session.IsStarted);
    }

    [Fact]
    public async Task Unexpected_sample_rate_is_reported_without_crashing_capture_callback()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        await using var session = new LocalSpeechCaptureSession(capture, CreateSpeechService());
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Faulted += (_, value) => fault.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(new float[100], 8_000);

        var error = await fault.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("16 kHz", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captured_audio_reports_a_measured_level_for_the_visualizer()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        await using var session = new LocalSpeechCaptureSession(capture, CreateSpeechService());
        var level = new TaskCompletionSource<AudioLevelEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.LevelChanged += (_, value) => level.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.5f, 400).ToArray());

        var measured = await level.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange(measured.Rms, 0.49f, 0.51f);
        Assert.InRange(measured.Peak, 0.49f, 0.51f);
    }

    [Fact]
    public async Task Starting_a_session_warms_the_recognizer_up_for_its_language()
    {
        var recognizer = new RecordingRecognizer();
        await using var session = new LocalSpeechCaptureSession(
            new FakeAudioCapture(AudioCaptureRequest.Microphone()),
            new LocalSpeechService(new FakeModelManager(), recognizer),
            "ja");

        await session.StartAsync();

        var prepared = await recognizer.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ja", prepared);
    }

    [Fact]
    public async Task Speaker_labels_are_attached_when_the_feature_is_on()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        var speakers = new FakeSpeakerIdentifier { Available = true };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers)
        {
            SpeakerLabelsEnabled = true,
        };
        await using var session = new LocalSpeechCaptureSession(
            capture, service, "en", new SpeechSegmenter(16_000, 0.01f, 100, 2_000),
            new SpeechCaptureProcessingOptions { SplitAtSpeakerChanges = true, AttachSpeakerLabels = true });
        var result = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) => result.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        var recognized = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("spk-test", recognized.SpeakerId);
        Assert.Equal("小明", recognized.SpeakerLabel);
        Assert.True(recognized.HasSpeaker);
    }

    [Fact]
    public async Task An_own_voice_session_skips_splitting_and_labels_entirely()
    {
        // 自身语音两项都关（D29.5）：即使全局标签开关开着，也不做切分不做识别。
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        var speakers = new FakeSpeakerIdentifier { Available = true };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers)
        {
            SpeakerLabelsEnabled = true,
        };
        await using var session = new LocalSpeechCaptureSession(
            capture, service, "en", new SpeechSegmenter(16_000, 0.01f, 100, 2_000));
        var result = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) => result.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        var recognized = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(recognized.SpeakerLabel);
        Assert.False(recognized.HasSpeaker);
        Assert.Equal(0, speakers.IdentifyCalls);
        Assert.Equal(0, speakers.SplitCalls);
    }

    [Fact]
    public async Task Other_player_sessions_split_at_speaker_changes_without_labels()
    {
        // 换人切分是多人对话的能力（D29）：标签关着也要切；标签开关只控制命名。
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        var speakers = new FakeSpeakerIdentifier
        {
            Available = true,
            Spans = [new SpeechSpan(0, 1_000), new SpeechSpan(1_000, 2_000)],
        };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers)
        {
            SpeakerLabelsEnabled = false,
        };
        await using var session = new LocalSpeechCaptureSession(
            capture, service, "en", new SpeechSegmenter(16_000, 0.01f, 100, 2_000),
            new SpeechCaptureProcessingOptions { SplitAtSpeakerChanges = true, AttachSpeakerLabels = false });
        var results = new List<SpeechRecognitionResult>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) =>
        {
            lock (results)
            {
                results.Add(value);
                if (results.Count == 2) both.TrySetResult();
            }
        };

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        await both.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.Null(item.SpeakerLabel));
        Assert.True(speakers.SplitCalls >= 1, "标签关闭时也必须执行换人切分");
        Assert.Equal(0, speakers.IdentifyCalls);
    }

    [Fact]
    public async Task A_failing_splitter_degrades_to_one_whole_caption()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        var speakers = new FakeSpeakerIdentifier { Available = true, ThrowOnSplit = true };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers);
        await using var session = new LocalSpeechCaptureSession(
            capture, service, "en", new SpeechSegmenter(16_000, 0.01f, 100, 2_000),
            new SpeechCaptureProcessingOptions { SplitAtSpeakerChanges = true });
        var result = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) => result.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        var recognized = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("test", recognized.Text);
    }

    [Fact]
    public async Task A_reported_speaker_change_produces_one_caption_per_voice()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone());
        var speakers = new FakeSpeakerIdentifier
        {
            Available = true,
            Spans = [new SpeechSpan(0, 1_000), new SpeechSpan(1_000, 2_000)],
        };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers)
        {
            SpeakerLabelsEnabled = true,
        };
        await using var session = new LocalSpeechCaptureSession(
            capture, service, "en", new SpeechSegmenter(16_000, 0.01f, 100, 2_000),
            new SpeechCaptureProcessingOptions { SplitAtSpeakerChanges = true, AttachSpeakerLabels = true });
        var results = new List<SpeechRecognitionResult>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) =>
        {
            lock (results)
            {
                results.Add(value);
                if (results.Count == 2) both.TrySetResult();
            }
        };

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        await both.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.Equal("小明", item.SpeakerLabel));
    }

    [Fact]
    public async Task A_source_boundary_drops_the_unclosed_sentence()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        await using var session = new LocalSpeechCaptureSession(
            capture,
            CreateSpeechService(),
            "en",
            new SpeechSegmenter(16_000, 0.01f, 100, 2_000));
        var results = new List<SpeechRecognitionResult>();
        session.ResultReady += (_, value) => { lock (results) results.Add(value); };

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, new ProcessIdentity(7)),
            generation: 1,
            isBoundary: true);
        // This silence would have closed the sentence that started before the switch.
        capture.Emit(new float[2_000]);

        await Task.Delay(100);
        Assert.Empty(results);

        // The session stays usable for the new source.
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);

        await TestWait.UntilAsync(() => results.Count == 1, "切换来源后未再产生字幕");
        Assert.Equal("test", results[0].Text);
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, session.SourceState.Kind);
        Assert.Equal(7, session.SourceState.ProcessIdentity!.ProcessId);
    }

    [Fact]
    public async Task A_relabelled_source_keeps_the_pending_sentence()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        await using var session = new LocalSpeechCaptureSession(
            capture,
            CreateSpeechService(),
            "en",
            new SpeechSegmenter(16_000, 0.01f, 100, 2_000));
        var result = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) => result.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        // System audio becoming the compatibility fallback keeps the same stream,
        // so the half sentence recorded so far is still valid.
        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.SystemLoopbackFallback),
            generation: 1,
            isBoundary: false);
        capture.Emit(new float[2_000]);

        var recognized = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("test", recognized.Text);
        Assert.True(session.SourceState.IsCompatibilityMode);
    }

    [Fact]
    public async Task An_own_voice_session_reports_the_microphone_source()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.Microphone("mic-1"));
        await using var session = new LocalSpeechCaptureSession(capture, CreateSpeechService());
        var changes = new List<AudioSourceChangedEventArgs>();
        session.SourceChanged += (_, args) => changes.Add(args);

        // 决策 1：自身语音必须报告 Microphone，不得复用回环来源值。
        Assert.Equal(AudioCaptureSourceKind.Microphone, session.SourceState.Kind);

        await session.StartAsync();
        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.Microphone),
            generation: 0,
            isBoundary: false);

        Assert.Equal(AudioCaptureSourceKind.Microphone, session.SourceState.Kind);
        var change = Assert.Single(changes);
        Assert.Equal(AudioCaptureSourceKind.Microphone, change.State.Kind);
        Assert.NotEqual(AudioCaptureSourceKind.SystemLoopback, session.SourceState.Kind);
    }

    [Fact]
    public async Task The_fallback_notice_is_owed_once_per_fallback_episode()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        await using var session = new LocalSpeechCaptureSession(capture, CreateSpeechService());
        await session.StartAsync();

        // Nothing to announce while the system mix is simply the system mix.
        Assert.False(session.ConsumeFallbackNotice());

        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.SystemLoopbackFallback),
            generation: 1,
            isBoundary: false);
        Assert.True(session.ConsumeFallbackNotice());

        // 后续重试失败不再提示：同一段降级只承担一次提示。
        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.SystemLoopbackFallback),
            generation: 2,
            isBoundary: false);
        Assert.False(session.ConsumeFallbackNotice());

        // 恢复后重新武装，下一次真正降级仍然只提示一次。
        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, new ProcessIdentity(7)),
            generation: 3,
            isBoundary: true);
        Assert.False(session.ConsumeFallbackNotice());
        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.SystemLoopbackFallback),
            generation: 4,
            isBoundary: false);
        Assert.True(session.ConsumeFallbackNotice());
        Assert.False(session.ConsumeFallbackNotice());
    }

    [Fact]
    public async Task A_result_that_finishes_after_the_boundary_is_not_published()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var recognizer = new GatedRecognizer();
        await using var session = new LocalSpeechCaptureSession(
            capture,
            new LocalSpeechService(new FakeModelManager(), recognizer),
            "en",
            new SpeechSegmenter(16_000, 0.01f, 100, 2_000));
        var results = new List<SpeechRecognitionResult>();
        session.ResultReady += (_, value) => { lock (results) results.Add(value); };

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        capture.Emit(new float[2_000]);
        await TestWait.UntilAsync(() => recognizer.Started == 1, "识别未开始");

        capture.RaiseSourceChanged(
            new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, new ProcessIdentity(7)),
            generation: 2,
            isBoundary: true);
        recognizer.Complete();

        await Task.Delay(100);
        Assert.Empty(results);
    }

    private static LocalSpeechService CreateSpeechService() => new(
        new FakeModelManager(),
        new FakeRecognizer());

    /// <summary>Speaker separation whose answers the test chooses.</summary>
    private sealed class FakeSpeakerIdentifier : ISpeakerIdentifier
    {
        public bool Available { get; init; }
        public bool ThrowOnSplit { get; init; }
        public IReadOnlyList<SpeechSpan> Spans { get; init; } = [];
        public int IdentifyCalls { get; private set; }
        public int SplitCalls { get; private set; }

        public bool IsAvailable => Available;

        public IReadOnlyList<SpeakerIdentity> Speakers => [];

        public SpeakerMatch? Identify(ReadOnlyMemory<float> samples, int sampleRate)
        {
            IdentifyCalls++;
            return new SpeakerMatch(
                new SpeakerIdentity("spk-test", "A", "小明"), 0.9f, SpeakerMatchKind.Session);
        }

        public IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate)
        {
            SplitCalls++;
            if (ThrowOnSplit) throw new InvalidOperationException("分割模型不可用");
            return Spans;
        }

        public void Rename(string speakerId, string? name) { }

        public bool Merge(string sourceId, string targetId) => false;

        public bool Forget(string speakerId) => false;

        public void Clear() { }
    }

    /// <summary>Recognizer that only answers when the test releases it.</summary>
    private sealed class GatedRecognizer : ILocalSpeechRecognizer
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public string ModelId => "fake";

        public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechLanguages.Supported;

        public async Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            await _gate.Task.ConfigureAwait(false);
            return new SpeechRecognitionResult(request.RequestId, "迟到结果", request.SourceLanguage, TimeSpan.Zero);
        }

        public void Complete() => _gate.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRecognizer : ILocalSpeechRecognizer
    {
        public TaskCompletionSource<string> Prepared { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ModelId => "fake";
        public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechLanguages.Supported;

        public Task PrepareAsync(string sourceLanguage, CancellationToken cancellationToken = default)
        {
            Prepared.TrySetResult(sourceLanguage);
            return Task.CompletedTask;
        }

        public Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new SpeechRecognitionResult(request.RequestId, "test", request.SourceLanguage, TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeModelManager : ILocalSpeechModelManager
    {
        public LocalSpeechModelStatus GetStatus() => new(
            LocalSpeechModelState.Ready, "fake", "fake", "fake", 1, "ready");

        public Task<LocalSpeechModelStatus> InstallAsync(
            IProgress<LocalSpeechModelProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(GetStatus());

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRecognizer : ILocalSpeechRecognizer
    {
        public string ModelId => "fake";
        public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechLanguages.Supported;

        public Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new SpeechRecognitionResult(request.RequestId, "test", request.SourceLanguage, TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
