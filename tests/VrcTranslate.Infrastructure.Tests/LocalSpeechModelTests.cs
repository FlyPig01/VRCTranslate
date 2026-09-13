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

    [Fact]
    public void Bundled_model_is_ready_without_user_copy()
    {
        var bundledPath = Path.Combine(_directory, "bundle", LocalSpeechModelCatalog.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
        File.WriteAllBytes(bundledPath, new byte[1_048_576]);
        var manager = new LocalSpeechModelManager(modelDirectory: null, bundledModelPath: bundledPath);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.Ready, status.State);
        Assert.True(status.IsBundled);
        Assert.Equal(bundledPath, status.FilePath);
        Assert.Equal(bundledPath, manager.ModelPath);
        Assert.Equal(1_048_576, status.InstalledBytes);
    }

    [Fact]
    public async Task Remove_never_deletes_the_bundled_model()
    {
        var bundledPath = Path.Combine(_directory, "bundle", LocalSpeechModelCatalog.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
        File.WriteAllBytes(bundledPath, new byte[1_048_576]);
        Directory.CreateDirectory(_directory);
        var userPath = Path.Combine(_directory, LocalSpeechModelCatalog.FileName);
        File.WriteAllBytes(userPath, new byte[128]);
        var manager = new LocalSpeechModelManager(_directory, bundledModelPath: bundledPath);

        await manager.RemoveAsync();

        Assert.False(File.Exists(userPath));
        Assert.True(File.Exists(bundledPath));
        Assert.Equal(LocalSpeechModelState.Ready, manager.GetStatus().State);
    }

    [Fact]
    public async Task Install_is_a_no_op_when_the_bundled_model_is_present()
    {
        var bundledPath = Path.Combine(_directory, "bundle", LocalSpeechModelCatalog.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
        File.WriteAllBytes(bundledPath, new byte[1_048_576]);
        var manager = new LocalSpeechModelManager(_directory, bundledModelPath: bundledPath);

        var status = await manager.InstallAsync();

        Assert.Equal(LocalSpeechModelState.Ready, status.State);
        Assert.True(status.IsBundled);
        Assert.False(File.Exists(Path.Combine(_directory, LocalSpeechModelCatalog.FileName)));
    }

    [Fact]
    public void Invalid_bundled_file_without_user_copy_reports_not_installed()
    {
        var bundledPath = Path.Combine(_directory, "bundle", LocalSpeechModelCatalog.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
        File.WriteAllBytes(bundledPath, new byte[128]);
        var manager = new LocalSpeechModelManager(_directory, bundledModelPath: bundledPath);

        var status = manager.GetStatus();

        // A placeholder bundled file must fall back to the on-demand download
        // flow instead of being reported as unusable state.
        Assert.Equal(LocalSpeechModelState.NotInstalled, status.State);
        Assert.False(status.IsBundled);
    }

    [Fact]
    public async Task Real_bundled_asset_loads_through_whisper_when_present()
    {
        var assetPath = FindRepositoryAsset();
        if (assetPath is null)
        {
            // The asset is optional in source control; this test only guards
            // clones that imported it via Import-BundledSpeechModel.ps1.
            return;
        }

        var manager = new LocalSpeechModelManager(_directory, bundledModelPath: assetPath);
        await using var recognizer = new WhisperLocalSpeechRecognizer(manager, threads: 1);
        var request = new SpeechRecognitionRequest(new float[8_000], 16_000, "en");

        var result = await recognizer.RecognizeAsync(request);

        Assert.Equal(request.RequestId, result.RequestId);
    }

    private static string? FindRepositoryAsset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            var candidate = Path.Combine(
                directory.FullName, "assets", "models", "speech", LocalSpeechModelCatalog.FileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
