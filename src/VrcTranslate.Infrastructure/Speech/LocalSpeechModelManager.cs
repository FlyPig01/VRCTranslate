using Whisper.net.Ggml;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Resolves the Whisper model from the copy bundled inside the publish output
/// first and falls back to the optional copy under the user profile that older
/// installs downloaded on demand. Downloads still exist as a recovery path for
/// packages produced without the bundled asset and are written to a temporary
/// file that is atomically promoted only after the stream completes, so an
/// interrupted download cannot look usable.
/// </summary>
public sealed class LocalSpeechModelManager : ILocalSpeechModelManager
{
    // A real Whisper base model is tens of MiB. A one MiB floor catches
    // interrupted/placeholder files without hard-coding a remote size.
    private const long MinimumModelBytes = 1_048_576;

    private readonly string _modelDirectory;
    private readonly string _modelPath;
    private readonly string _bundledPath;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private volatile bool _installing;

    public LocalSpeechModelManager(string? modelDirectory = null, string? bundledModelPath = null)
    {
        _modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
            ? LocalSpeechModelCatalog.DefaultDirectory
            : Path.GetFullPath(modelDirectory);
        _modelPath = Path.Combine(_modelDirectory, LocalSpeechModelCatalog.FileName);
        _bundledPath = string.IsNullOrWhiteSpace(bundledModelPath)
            ? LocalSpeechModelCatalog.GetBundledModelPath()
            : Path.GetFullPath(bundledModelPath);
    }

    /// <summary>Path of the model the recognizer should load; falls back to the user copy when neither file exists.</summary>
    public string ModelPath => ResolveUsablePath() ?? _modelPath;

    public LocalSpeechModelStatus GetStatus()
    {
        if (_installing)
        {
            return new(LocalSpeechModelState.Installing, LocalSpeechModelCatalog.ModelId,
                LocalSpeechModelCatalog.DisplayName, _modelPath, ExistingLength(_modelPath), "正在安装");
        }

        if (TryMeasure(_bundledPath, out var bundledBytes) && bundledBytes >= MinimumModelBytes)
        {
            return new(LocalSpeechModelState.Ready, LocalSpeechModelCatalog.ModelId,
                LocalSpeechModelCatalog.DisplayName, _bundledPath, bundledBytes, "内置模型已就绪", IsBundled: true);
        }

        if (TryMeasure(_modelPath, out var installedBytes))
        {
            return installedBytes >= MinimumModelBytes
                ? new(LocalSpeechModelState.Ready, LocalSpeechModelCatalog.ModelId,
                    LocalSpeechModelCatalog.DisplayName, _modelPath, installedBytes, "本地模型已就绪")
                : new(LocalSpeechModelState.Invalid, LocalSpeechModelCatalog.ModelId,
                    LocalSpeechModelCatalog.DisplayName, _modelPath, installedBytes, "模型文件不完整，请重新安装");
        }

        return new(LocalSpeechModelState.NotInstalled, LocalSpeechModelCatalog.ModelId,
            LocalSpeechModelCatalog.DisplayName, _modelPath, 0, "本地模型未安装");
    }

    public async Task<LocalSpeechModelStatus> InstallAsync(
        IProgress<LocalSpeechModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var current = GetStatus();
        if (current.State == LocalSpeechModelState.Ready)
        {
            // The bundled or already-downloaded copy removes the need for network access.
            return current;
        }

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _installing = true;
        var temporaryPath = _modelPath + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_modelDirectory);
            await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                LocalSpeechModelCatalog.GgmlType,
                LocalSpeechModelCatalog.Quantization,
                cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[1024 * 128];
            long received = 0;
            var total = source.CanSeek ? source.Length : (long?)null;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new LocalSpeechModelProgress(received, total, LocalSpeechModelCatalog.ModelId));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (received < MinimumModelBytes)
            {
                throw new InvalidDataException("本地语音模型下载内容不完整。");
            }

            File.Move(temporaryPath, _modelPath, overwrite: true);
            return GetStatus();
        }
        finally
        {
            _installing = false;
            _installLock.Release();
            TryDelete(temporaryPath);
        }
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The bundled copy is part of the application package and must survive;
        // only the on-demand copy under the user profile is removable.
        TryDelete(_modelPath);
        return Task.CompletedTask;
    }

    private string? ResolveUsablePath()
    {
        if (TryMeasure(_bundledPath, out var bundledBytes) && bundledBytes >= MinimumModelBytes) return _bundledPath;
        if (TryMeasure(_modelPath, out var installedBytes) && installedBytes >= MinimumModelBytes) return _modelPath;
        return null;
    }

    private bool TryMeasure(string path, out long length)
    {
        length = ExistingLength(path);
        return File.Exists(path);
    }

    private long ExistingLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A failed cleanup never masks the original install error.
        }
    }
}
