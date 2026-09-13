using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class LocalSpeechModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vrctranslate-local-speech-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Sibling of the user model directory, mirroring the production layout
    /// where the bundled payload lives beside the executable and is never
    /// nested inside the removable per-user copy.
    /// </summary>
    private readonly string _bundledDirectory = Path.Combine(
        Path.GetTempPath(), "vrctranslate-local-speech-bundle-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Speaker_payload_is_ready_when_both_models_are_bundled()
    {
        var bundled = Path.Combine(_bundledDirectory, "speaker");
        Directory.CreateDirectory(bundled);
        WriteSizedFile(Path.Combine(bundled, LocalSpeechModelCatalog.SegmentationFileName), 1_540_506);
        WriteSizedFile(Path.Combine(bundled, LocalSpeechModelCatalog.EmbeddingFileName), 28_281_164);
        var manager = new LocalSpeechModelManager(LocalSpeechModelCatalog.Speaker, _directory, bundled);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.Ready, status.State);
        Assert.True(status.IsBundled);
        Assert.Equal(LocalSpeechModelCatalog.SpeakerModelId, status.ModelId);
        Assert.Equal(1_540_506 + 28_281_164, status.InstalledBytes);
    }

    [Fact]
    public void Speaker_payload_with_one_missing_model_is_not_ready()
    {
        var bundled = Path.Combine(_bundledDirectory, "speaker");
        Directory.CreateDirectory(bundled);
        WriteSizedFile(Path.Combine(bundled, LocalSpeechModelCatalog.EmbeddingFileName), 28_281_164);
        var manager = new LocalSpeechModelManager(LocalSpeechModelCatalog.Speaker, _directory, bundled);

        Assert.Equal(LocalSpeechModelState.NotInstalled, manager.GetStatus().State);
    }

    [Fact]
    public void Payloads_carry_their_own_files_and_sources()
    {
        Assert.Equal(
            [LocalSpeechModelCatalog.ModelFileName, LocalSpeechModelCatalog.TokensFileName],
            LocalSpeechModelCatalog.Recognition.Files.Select(file => file.Name));

        // The speaker files come from two different repositories and are renamed
        // on the way in, so each one carries an explicit source and the payload
        // has no shared repository to fall back on.
        var segmentation = LocalSpeechModelCatalog.Speaker.SourcesFor(LocalSpeechModelCatalog.SegmentationFileName);
        var embedding = LocalSpeechModelCatalog.Speaker.SourcesFor(LocalSpeechModelCatalog.EmbeddingFileName);
        Assert.Single(segmentation);
        Assert.Single(embedding);
        Assert.EndsWith("/model.int8.onnx", segmentation[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("3dspeaker_speech_campplus", embedding[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Empty(LocalSpeechModelCatalog.Speaker.Repositories);
    }

    [Fact]
    public void Recognition_payload_keeps_its_mirror_fallback()
    {
        var sources = LocalSpeechModelCatalog.Recognition.SourcesFor(LocalSpeechModelCatalog.ModelFileName);

        Assert.Equal(2, sources.Count);
        Assert.Contains("hf-mirror.com", sources[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("huggingface.co", sources[1].AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_model_is_reported_without_loading_native_runtime()
    {
        var manager = new LocalSpeechModelManager(_directory);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.NotInstalled, status.State);
        Assert.Equal(LocalSpeechModelCatalog.ModelId, status.ModelId);
        // The SenseVoice payload is a folder, not a single file.
        Assert.Equal(_directory, status.FilePath);
        Assert.False(Directory.Exists(status.FilePath));
    }

    [Fact]
    public void Partial_model_is_not_reported_as_ready()
    {
        WritePayload(_directory, includeTokens: false);
        var manager = new LocalSpeechModelManager(_directory);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.Invalid, status.State);
        Assert.Contains("不完整", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undersized_model_file_is_not_reported_as_ready()
    {
        WritePayload(_directory, modelBytes: 128);
        var manager = new LocalSpeechModelManager(_directory);

        Assert.Equal(LocalSpeechModelState.Invalid, manager.GetStatus().State);
    }

    [Fact]
    public async Task Remove_is_idempotent_and_honors_cancellation()
    {
        WritePayload(_directory);
        var manager = new LocalSpeechModelManager(_directory);

        await manager.RemoveAsync();
        await manager.RemoveAsync();

        Assert.False(Directory.Exists(_directory));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.RemoveAsync(cancelled.Token));
    }

    [Fact]
    public async Task Recognizer_reports_missing_model_without_falling_back_to_system_api()
    {
        var manager = new LocalSpeechModelManager(_directory);
        await using var recognizer = new SenseVoiceSpeechRecognizer(manager);
        var request = new SpeechRecognitionRequest(new float[16_000], 16_000, "en");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recognizer.RecognizeAsync(request));

        Assert.Contains("未安装", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_model_is_ready_without_user_copy()
    {
        var bundledDirectory = _bundledDirectory;
        WritePayload(bundledDirectory);
        var manager = new LocalSpeechModelManager(_directory, bundledDirectory);

        var status = manager.GetStatus();

        Assert.Equal(LocalSpeechModelState.Ready, status.State);
        Assert.True(status.IsBundled);
        Assert.Equal(bundledDirectory, status.FilePath);
        Assert.Equal(bundledDirectory, manager.ModelDirectory);
        Assert.Equal(ExpectedPayloadBytes, status.InstalledBytes);
    }

    [Fact]
    public async Task Remove_never_deletes_the_bundled_model()
    {
        var bundledDirectory = _bundledDirectory;
        WritePayload(bundledDirectory);
        WritePayload(_directory, modelBytes: 128);
        var manager = new LocalSpeechModelManager(_directory, bundledDirectory);

        await manager.RemoveAsync();

        Assert.False(Directory.Exists(_directory));
        Assert.True(File.Exists(Path.Combine(bundledDirectory, LocalSpeechModelCatalog.ModelFileName)));
        Assert.Equal(LocalSpeechModelState.Ready, manager.GetStatus().State);
    }

    [Fact]
    public async Task Install_is_a_no_op_when_the_bundled_model_is_present()
    {
        var bundledDirectory = _bundledDirectory;
        WritePayload(bundledDirectory);
        var manager = new LocalSpeechModelManager(_directory, bundledDirectory);

        var status = await manager.InstallAsync();

        Assert.Equal(LocalSpeechModelState.Ready, status.State);
        Assert.True(status.IsBundled);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Invalid_bundled_payload_without_user_copy_reports_not_installed()
    {
        var bundledDirectory = _bundledDirectory;
        WritePayload(bundledDirectory, modelBytes: 128);
        var manager = new LocalSpeechModelManager(_directory, bundledDirectory);

        var status = manager.GetStatus();

        // A placeholder bundled payload must fall back to the on-demand
        // download flow instead of being reported as unusable state.
        Assert.Equal(LocalSpeechModelState.NotInstalled, status.State);
        Assert.False(status.IsBundled);
    }

    [Fact]
    public async Task Real_bundled_asset_loads_through_sensevoice_when_present()
    {
        var assetDirectory = FindRepositoryAsset();
        if (assetDirectory is null)
        {
            // The asset is optional in source control; this test only guards
            // clones that imported it via Import-BundledSpeechModel.ps1.
            return;
        }

        var manager = new LocalSpeechModelManager(_directory, assetDirectory);
        await using var recognizer = new SenseVoiceSpeechRecognizer(manager);
        var request = new SpeechRecognitionRequest(new float[8_000], 16_000, "en");

        var result = await recognizer.RecognizeAsync(request);

        Assert.Equal(request.RequestId, result.RequestId);
    }

    private const long ModelBytes = 100_000_000;
    private const int TokensBytes = 12_000;
    private const long ExpectedPayloadBytes = ModelBytes + TokensBytes;

    /// <summary>Creates a payload whose sizes satisfy the manager's floors.</summary>
    private static void WritePayload(string directory, bool includeTokens = true, long modelBytes = ModelBytes)
    {
        Directory.CreateDirectory(directory);
        WriteSizedFile(Path.Combine(directory, LocalSpeechModelCatalog.ModelFileName), modelBytes);
        if (includeTokens)
        {
            WriteSizedFile(Path.Combine(directory, LocalSpeechModelCatalog.TokensFileName), TokensBytes);
        }
    }

    private static void WriteSizedFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static string? FindRepositoryAsset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "assets", "models", "speech", LocalSpeechModelCatalog.ModelDirectory);
            if (File.Exists(Path.Combine(candidate, LocalSpeechModelCatalog.ModelFileName))) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        if (Directory.Exists(_bundledDirectory)) Directory.Delete(_bundledDirectory, recursive: true);
    }
}
