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
        capture.Emit(Enumerable.Repeat(0.2f, 1_600).ToArray());
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

    private static LocalSpeechService CreateSpeechService() => new(
        new FakeModelManager(),
        new FakeRecognizer());

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
