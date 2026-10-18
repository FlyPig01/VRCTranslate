using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class LocalSpeechSelfTestTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vrctranslate-self-test-" + Guid.NewGuid().ToString("N"));

    public LocalSpeechSelfTestTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Passing_check_warms_the_model_and_reports_usable_asr()
    {
        var recognizer = new FakeRecognizer(text: "自检文本");
        var selfTest = new LocalSpeechSelfTest(
            new LocalSpeechService(new FakeModelManager(LocalSpeechModelState.Ready), recognizer),
            () => new FakeSegmenter());

        var result = await selfTest.RunAsync(WriteWav());

        Assert.True(result.ModelReady);
        Assert.True(result.AsrUsable);
        Assert.True(result.IsUsable);
        Assert.False(result.VadUsable);
        Assert.Equal("自检通过", result.Message);
        Assert.Equal(1, recognizer.PrepareCalls);
        Assert.Equal(1, recognizer.RecognizeCalls);
        Assert.Contains("VAD", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_model_fails_before_touching_the_recognizer()
    {
        var recognizer = new FakeRecognizer(text: "unused");
        var selfTest = new LocalSpeechSelfTest(
            new LocalSpeechService(new FakeModelManager(LocalSpeechModelState.NotInstalled), recognizer),
            () => new FakeSegmenter());

        var result = await selfTest.RunAsync(WriteWav());

        Assert.False(result.ModelReady);
        Assert.False(result.IsUsable);
        Assert.Equal(0, recognizer.PrepareCalls);
        Assert.Equal(0, recognizer.RecognizeCalls);
    }

    [Fact]
    public async Task Failing_recognizer_is_reported_with_the_technical_detail()
    {
        var recognizer = new FakeRecognizer(failure: new InvalidOperationException("model boom"));
        var selfTest = new LocalSpeechSelfTest(
            new LocalSpeechService(new FakeModelManager(LocalSpeechModelState.Ready), recognizer),
            () => new FakeSegmenter());

        var result = await selfTest.RunAsync(WriteWav());

        Assert.False(result.AsrUsable);
        Assert.False(result.IsUsable);
        Assert.Equal("本地语音模型无法运行", result.Message);
        Assert.Contains("model boom", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_recognition_is_a_failure_not_a_pass()
    {
        var recognizer = new FakeRecognizer(text: "   ");
        var selfTest = new LocalSpeechSelfTest(
            new LocalSpeechService(new FakeModelManager(LocalSpeechModelState.Ready), recognizer),
            () => new FakeSegmenter());

        var result = await selfTest.RunAsync(WriteWav());

        Assert.True(result.ModelReady);
        Assert.False(result.AsrUsable);
        Assert.Contains("没有识别出文字", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audio_with_the_wrong_format_is_reported_as_unusable()
    {
        var recognizer = new FakeRecognizer(text: "unused");
        var selfTest = new LocalSpeechSelfTest(
            new LocalSpeechService(new FakeModelManager(LocalSpeechModelState.Ready), recognizer),
            () => new FakeSegmenter());

        var result = await selfTest.RunAsync(WriteWav(channels: 2));

        Assert.True(result.ModelReady);
        Assert.False(result.AsrUsable);
        Assert.Equal("自检音频不可用", result.Message);
        Assert.Equal(0, recognizer.RecognizeCalls);
    }

    private string WriteWav(int seconds = 2, short channels = 1, int sampleRate = 16_000)
    {
        var path = Path.Combine(_directory, $"selftest-{Guid.NewGuid():N}.wav");
        var dataLength = sampleRate * seconds * channels * 2;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeModelManager(LocalSpeechModelState state) : ILocalSpeechModelManager
    {
        public LocalSpeechModelStatus GetStatus() => new(
            state, "fake", "fake", Path.GetTempPath(), 1, state == LocalSpeechModelState.Ready ? "ready" : "not ready");

        public Task<LocalSpeechModelStatus> InstallAsync(
            IProgress<LocalSpeechModelProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(GetStatus());

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRecognizer(string? text = null, Exception? failure = null) : ILocalSpeechRecognizer
    {
        public int PrepareCalls { get; private set; }

        public int RecognizeCalls { get; private set; }

        public string ModelId => "fake";

        public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechLanguages.Supported;

        public Task PrepareAsync(string sourceLanguage, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.CompletedTask;
        }

        public Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default)
        {
            RecognizeCalls++;
            if (failure is not null) return Task.FromException<SpeechRecognitionResult>(failure);
            return Task.FromResult(new SpeechRecognitionResult(
                request.RequestId, text ?? "自检文本", request.SourceLanguage, TimeSpan.Zero));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSegmenter : ISpeechSegmenter
    {
        public int SampleRate => 16_000;

        public IReadOnlyList<ReadOnlyMemory<float>> Append(ReadOnlyMemory<float> chunk) => [];

        public IReadOnlyList<ReadOnlyMemory<float>> Flush() => [];

        public void Reset() { }
    }
}
