using VrcTranslate.Application.Speech;
using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class LocalSpeechServiceTests
{
    [Fact]
    public async Task Service_forwards_model_and_recognition_operations()
    {
        var manager = new FakeModelManager();
        var recognizer = new FakeRecognizer();
        await using var service = new LocalSpeechService(manager, recognizer);
        var progress = new Progress<LocalSpeechModelProgress>();

        var installed = await service.InstallModelAsync(progress);
        var result = await service.RecognizeAsync(new SpeechRecognitionRequest(
            new float[1600], 16_000, "ja"));

        Assert.Equal(LocalSpeechModelState.Ready, installed.State);
        Assert.Equal("ja", result.SourceLanguage);
        Assert.Equal(1, manager.InstallCalls);
        Assert.Equal(1, recognizer.RecognizeCalls);
    }

    private sealed class FakeModelManager : ILocalSpeechModelManager
    {
        public int InstallCalls { get; private set; }

        public LocalSpeechModelStatus GetStatus() => new(
            LocalSpeechModelState.Ready, "fake", "Fake", "fake.bin", 10, "ready");

        public Task<LocalSpeechModelStatus> InstallAsync(
            IProgress<LocalSpeechModelProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            return Task.FromResult(GetStatus());
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRecognizer : ILocalSpeechRecognizer
    {
        public int RecognizeCalls { get; private set; }
        public string ModelId => "fake";
        public IReadOnlyList<SpeechLanguageOption> SupportedLanguages => LocalSpeechLanguages.Supported;

        public Task<SpeechRecognitionResult> RecognizeAsync(
            SpeechRecognitionRequest request,
            CancellationToken cancellationToken = default)
        {
            RecognizeCalls++;
            return Task.FromResult(new SpeechRecognitionResult(
                request.RequestId, "test", request.SourceLanguage, TimeSpan.Zero));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
