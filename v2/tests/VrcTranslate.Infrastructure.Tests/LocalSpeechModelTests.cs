using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class LocalSpeechModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vrctranslate-local-speech-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Missing_model_is_reported_without_loading_native_runtime()
    {
        var manager = new LocalSpeechModelManager(_directory);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.NotInstalled, status.State);
        Assert.Equal(LocalSpeechModelCatalog.ModelId, status.ModelId);
        Assert.False(File.Exists(status.FilePath));
    }

    [Fact]
    public void Partial_model_is_not_reported_as_ready()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, LocalSpeechModelCatalog.FileName);
        File.WriteAllBytes(path, new byte[128]);
        var manager = new LocalSpeechModelManager(_directory);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.Invalid, status.State);
        Assert.Contains("不完整", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_is_idempotent_and_honors_cancellation()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, LocalSpeechModelCatalog.FileName);
        File.WriteAllBytes(path, new byte[128]);
        var manager = new LocalSpeechModelManager(_directory);

        await manager.RemoveAsync();
        await manager.RemoveAsync();

        Assert.False(File.Exists(path));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.RemoveAsync(cancelled.Token));
    }

    [Fact]
    public async Task Recognizer_reports_missing_model_without_falling_back_to_system_api()
    {
        var manager = new LocalSpeechModelManager(_directory);
        await using var recognizer = new WhisperLocalSpeechRecognizer(manager, threads: 1);
        var request = new SpeechRecognitionRequest(new float[16_000], 16_000, "en");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recognizer.RecognizeAsync(request));

        Assert.Contains("未安装", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
