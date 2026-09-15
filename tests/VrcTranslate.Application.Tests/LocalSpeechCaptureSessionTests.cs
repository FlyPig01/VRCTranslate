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
        var capture = new FakeCapture();
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
        var capture = new FakeCapture();
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
        var capture = new FakeCapture();
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
            new FakeCapture(),
            new LocalSpeechService(new FakeModelManager(), recognizer),
            "ja");

        await session.StartAsync();

        var prepared = await recognizer.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ja", prepared);
    }

    [Fact]
    public async Task Speaker_labels_are_attached_when_the_feature_is_on()
    {
        var capture = new FakeCapture();
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

        Assert.Equal("spk-test", recognized.SpeakerId);
        Assert.Equal("小明", recognized.SpeakerLabel);
        Assert.True(recognized.HasSpeaker);
    }

    [Fact]
    public async Task Speaker_labels_cost_nothing_while_the_feature_is_off()
    {
        var capture = new FakeCapture();
        var speakers = new FakeSpeakerIdentifier { Available = true };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers);
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
        Assert.Equal(0, speakers.ChangeChecks);
    }

    [Fact]
    public async Task A_suspected_speaker_change_produces_one_caption_per_voice()
    {
        var capture = new FakeCapture();
        var speakers = new FakeSpeakerIdentifier
        {
            Available = true,
            SuspectChange = true,
            Spans = [new SpeechSpan(0, 1_000), new SpeechSpan(1_000, 2_000)],
        };
        var service = new LocalSpeechService(new FakeModelManager(), new FakeRecognizer(), speakers)
        {
            SpeakerLabelsEnabled = true,
        };
        await using var session = new LocalSpeechCaptureSession(
            capture, service, "en", new SpeechSegmenter(16_000, 0.01f, 100, 2_000));
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

    private static LocalSpeechService CreateSpeechService() => new(
        new FakeModelManager(),
        new FakeRecognizer());

    /// <summary>Speaker separation whose answers the test chooses.</summary>
    private sealed class FakeSpeakerIdentifier : ISpeakerIdentifier
    {
        public bool Available { get; init; }
        public bool SuspectChange { get; init; }
        public IReadOnlyList<SpeechSpan> Spans { get; init; } = [];
        public int IdentifyCalls { get; private set; }
        public int ChangeChecks { get; private set; }

        public bool IsAvailable => Available;

        public IReadOnlyList<SpeakerIdentity> Speakers => [];

        public SpeakerMatch? Identify(ReadOnlyMemory<float> samples, int sampleRate)
        {
            IdentifyCalls++;
            return new SpeakerMatch(
                new SpeakerIdentity("spk-test", "A", "小明"), 0.9f, SpeakerMatchKind.Session);
        }

        public bool IsSpeakerChangeSuspected(ReadOnlyMemory<float> samples, int sampleRate)
        {
            ChangeChecks++;
            return SuspectChange;
        }

        public IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate) => Spans;

        public void Rename(string speakerId, string? name) { }

        public bool Merge(string sourceId, string targetId) => false;

        public bool Forget(string speakerId) => false;

        public void Clear() { }
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

    private sealed class FakeCapture : IAudioCapture
    {
        public AudioCaptureMode Mode => AudioCaptureMode.Microphone;
        public int SampleRate => 16_000;
        public event EventHandler<AudioSamplesEventArgs>? SamplesReady;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Emit(float[] samples, int sampleRate = 16_000) =>
            SamplesReady?.Invoke(this, new AudioSamplesEventArgs(samples, sampleRate));

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
