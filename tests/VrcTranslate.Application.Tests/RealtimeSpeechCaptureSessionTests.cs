using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class RealtimeSpeechCaptureSessionTests
{
    [Fact]
    public async Task Capture_audio_is_streamed_and_a_stable_sentence_is_published()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var realtime = new FakeRealtimeRecognizer();
        var service = CreateService();
        service.ConfigureTencent(null, CreateSettings(), new FakeRealtimeFactory(realtime));
        await using var session = new RealtimeSpeechCaptureSession(capture, service, "ja");
        var result = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) => result.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(Enumerable.Repeat(0.2f, 4_800).ToArray());
        realtime.Emit(new TencentRealtimeResult(
            "こんにちは",
            "msg-1",
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(2_200),
            Enumerable.Repeat(0.2f, 32_000).ToArray()));

        var recognized = await result.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("こんにちは", recognized.Text);
        Assert.Equal("ja", recognized.SourceLanguage);
        Assert.True(realtime.AppendedSamples >= 4_800);
        Assert.True(session.IsStarted);
    }

    [Fact]
    public async Task Interim_text_is_forwarded_and_cleared_before_the_final_caption()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var realtime = new FakeRealtimeRecognizer();
        var service = CreateService();
        service.ConfigureTencent(null, CreateSettings(), new FakeRealtimeFactory(realtime));
        await using var session = new RealtimeSpeechCaptureSession(capture, service, "ja");
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PartialReady += (_, partial) =>
        {
            if (string.IsNullOrEmpty(partial.Text)) cleared.TrySetResult();
            else shown.TrySetResult(partial.Text);
        };
        session.ResultReady += (_, result) => final.TrySetResult(result);

        await session.StartAsync();
        realtime.EmitPartial(new TencentRealtimePartial("こんにちは", 0, "m1"));
        Assert.Equal("こんにちは", await shown.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        realtime.Emit(new TencentRealtimeResult(
            "こんにちは", "m1", TimeSpan.Zero, TimeSpan.FromSeconds(1), ReadOnlyMemory<float>.Empty));
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("こんにちは", (await final.Task.WaitAsync(TimeSpan.FromSeconds(2))).Text);
    }

    [Fact]
    public async Task Word_timestamps_split_a_sentence_at_a_speaker_change()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var realtime = new FakeRealtimeRecognizer();
        var speakers = new SplitSpeakerIdentifier();
        var service = new LocalSpeechService(new FakeModelManager(), new FakeLocalRecognizer(), speakers);
        service.ConfigureTencent(null, CreateSettings(), new FakeRealtimeFactory(realtime));
        await using var session = new RealtimeSpeechCaptureSession(
            capture, service, "en", new SingleSegmentSegmenter());
        var results = new List<SpeechRecognitionResult>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultReady += (_, value) =>
        {
            lock (results) results.Add(value);
            if (results.Count >= 2) done.TrySetResult();
        };

        await session.StartAsync();
        capture.Emit(new float[64_000]);
        await Task.Delay(300);
        realtime.Emit(new TencentRealtimeResult(
            "hello world",
            "msg-2",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            Enumerable.Repeat(0.2f, 64_000).ToArray(),
            [
                new TencentRealtimeWord("hello", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                new TencentRealtimeWord("world", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)),
            ]));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, results.Count);
        Assert.Equal("hello", results[0].Text);
        Assert.Equal("world", results[1].Text);
        Assert.Equal("spk-A", results[0].SpeakerId);
        Assert.Equal("spk-B", results[1].SpeakerId);
    }

    [Fact]
    public async Task Wrong_sample_rate_faults_without_throwing_from_the_capture_callback()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var service = CreateService();
        service.ConfigureTencent(null, CreateSettings(), new FakeRealtimeFactory(new FakeRealtimeRecognizer()));
        await using var session = new RealtimeSpeechCaptureSession(capture, service, "ja");
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Faulted += (_, value) => fault.TrySetResult(value);

        await session.StartAsync();
        capture.Emit(new float[100], 8_000);

        var error = await fault.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("16 kHz", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_failure_switches_to_local_without_failing_the_session()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var realtime = new FakeRealtimeRecognizer
        {
            StartFailure = new TencentAsrException("Realtime4002", "鉴权失败"),
        };
        var service = CreateService();
        var fallback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CloudFallback += (_, message) => fallback.TrySetResult(message);
        service.ConfigureTencent(null, CreateSettings(), new FakeRealtimeFactory(realtime));
        var session = new RealtimeSpeechCaptureSession(capture, service, "ja");

        await session.StartAsync();

        Assert.True(session.IsStarted);
        Assert.Contains("鉴权失败", await fallback.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Stop_completes_the_connection()
    {
        var capture = new FakeAudioCapture(AudioCaptureRequest.SystemLoopback());
        var realtime = new FakeRealtimeRecognizer();
        var service = CreateService();
        service.ConfigureTencent(null, CreateSettings(), new FakeRealtimeFactory(realtime));
        var session = new RealtimeSpeechCaptureSession(capture, service, "ja");

        await session.StartAsync();
        await session.StopAsync();
        await session.DisposeAsync();

        Assert.True(realtime.Completed);
        Assert.Equal(1, capture.StopCount);
    }

    private static LocalSpeechService CreateService() =>
        new(new FakeModelManager(), new FakeLocalRecognizer());

    private static TencentAsrSettings CreateSettings() => new()
    {
        Enabled = true,
        Mode = TencentAsrMode.Realtime,
        Engine = TencentAsrEngines.Japanese,
        AppId = "1250000000",
        SecretId = "AKIDtest",
        SecretKey = "test",
    };

    private sealed class SingleSegmentSegmenter : ISpeechSegmenter
    {
        private bool _emitted;

        public int SampleRate => 16_000;

        public IReadOnlyList<ReadOnlyMemory<float>> Append(ReadOnlyMemory<float> chunk)
        {
            if (_emitted || chunk.Length == 0) return [];
            _emitted = true;
            return [new float[chunk.Length]];
        }

        public IReadOnlyList<ReadOnlyMemory<float>> Flush() => [];
        public void Reset() => _emitted = false;
    }

    private sealed class SplitSpeakerIdentifier : ISpeakerIdentifier
    {
        private int _identifyCalls;

        public bool IsAvailable => true;
        public IReadOnlyList<SpeakerIdentity> Speakers => [];

        public event EventHandler? SpeakersChanged
        {
            add { }
            remove { }
        }

        public SpeakerMatch? Identify(
            ReadOnlyMemory<float> samples,
            int sampleRate,
            string? previousSpeakerId = null,
            bool allowCreate = true)
        {
            var index = Interlocked.Increment(ref _identifyCalls) - 1;
            var speaker = index % 2 == 0
                ? new SpeakerIdentity("spk-A", "A", null)
                : new SpeakerIdentity("spk-B", "B", null);
            return new SpeakerMatch(speaker, 0.9f, SpeakerMatchKind.Session);
        }

        public IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate) =>
        [
            new SpeechSpan(0, samples.Length / 2, 0),
            new SpeechSpan(samples.Length / 2, samples.Length, 1),
        ];

        public bool Rename(string speakerId, string? name) => false;
        public bool Reject(string speakerId) => false;
        public bool Merge(string sourceId, string targetId) => false;
        public bool Forget(string speakerId) => false;
        public void Clear() { }
    }

    private sealed class FakeRealtimeFactory(ITencentRealtimeRecognizer recognizer) : ITencentRealtimeRecognizerFactory
    {
        public ITencentRealtimeRecognizer Create(TencentAsrSettings settings) => recognizer;
    }

    private sealed class FakeRealtimeRecognizer : ITencentRealtimeRecognizer
    {
        public event EventHandler<TencentRealtimeResult>? ResultReady;

        public event EventHandler<TencentRealtimePartial>? PartialReady;

        public event EventHandler<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public int AppendedSamples { get; private set; }
        public bool Started { get; private set; }
        public bool Completed { get; private set; }
        public Exception? StartFailure { get; init; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (StartFailure is not null) throw StartFailure;
            Started = true;
            return Task.CompletedTask;
        }

        public void Append(ReadOnlyMemory<float> samples, int sampleRate) => AppendedSamples += samples.Length;

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public Task TestAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

        public void Emit(TencentRealtimeResult result) => ResultReady?.Invoke(this, result);

        public void EmitPartial(TencentRealtimePartial partial) => PartialReady?.Invoke(this, partial);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeModelManager : ILocalSpeechModelManager
    {
        public LocalSpeechModelStatus GetStatus() => new(
            LocalSpeechModelState.Ready, "fake", "Fake", "fake.bin", 10, "ready");

        public Task<LocalSpeechModelStatus> InstallAsync(
            IProgress<LocalSpeechModelProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(GetStatus());

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeLocalRecognizer : ILocalSpeechRecognizer
    {
        public string ModelId => "fake";
        public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechLanguages.Supported;

        public Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpeechRecognitionResult(
                request.RequestId, "local", request.SourceLanguage, TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
