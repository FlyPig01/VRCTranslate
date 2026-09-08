using Whisper.net.Ggml;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Installs the optional Whisper model outside the executable directory.
/// Downloads are written to a temporary file and atomically promoted only
/// after the stream completes, so an interrupted download cannot look usable.
/// </summary>
public sealed class LocalSpeechModelManager : ILocalSpeechModelManager
{
    private readonly string _modelDirectory;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private volatile bool _installing;

    public LocalSpeechModelManager(string? modelDirectory = null)
    {
        _modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
            ? LocalSpeechModelCatalog.DefaultDirectory
            : Path.GetFullPath(modelDirectory);
        _modelPath = Path.Combine(_modelDirectory, LocalSpeechModelCatalog.FileName);
    }

    public string ModelPath => _modelPath;

    public LocalSpeechModelStatus GetStatus()
    {
        if (_installing)
        {
            return new(LocalSpeechModelState.Installing, LocalSpeechModelCatalog.ModelId,
                LocalSpeechModelCatalog.DisplayName, _modelPath, ExistingLength(), "正在安装");
        }

        if (!File.Exists(_modelPath))
        {
            return new(LocalSpeechModelState.NotInstalled, LocalSpeechModelCatalog.ModelId,
                LocalSpeechModelCatalog.DisplayName, _modelPath, 0, "本地模型未安装");
        }

        var length = ExistingLength();
        // A real Whisper base model is tens of MiB. A one MiB floor catches
        // interrupted/placeholder files without hard-coding a remote size.
        return length >= 1_048_576
            ? new(LocalSpeechModelState.Ready, LocalSpeechModelCatalog.ModelId,
                LocalSpeechModelCatalog.DisplayName, _modelPath, length, "本地模型已就绪")
            : new(LocalSpeechModelState.Invalid, LocalSpeechModelCatalog.ModelId,
                LocalSpeechModelCatalog.DisplayName, _modelPath, length, "模型文件不完整，请重新安装");
    }

    public async Task<LocalSpeechModelStatus> InstallAsync(
        IProgress<LocalSpeechModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
            if (received < 1_048_576)
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
        TryDelete(_modelPath);
        return Task.CompletedTask;
    }

    private long ExistingLength()
    {
        try { return File.Exists(_modelPath) ? new FileInfo(_modelPath).Length : 0; }
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
