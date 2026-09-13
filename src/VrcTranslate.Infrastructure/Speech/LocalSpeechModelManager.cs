using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Resolves one model payload (SenseVoice recognition, or the speaker
/// separation models) from the copy bundled inside the publish output first and
/// then from the portable data folder. The recovery download pulls every file
/// into a staging folder and promotes it only after the whole payload is
/// complete, so an interrupted download can never look usable.
/// </summary>
public sealed class LocalSpeechModelManager : ILocalSpeechModelManager
{
    private readonly LocalSpeechModelPayload _payload;
    private readonly string _modelDirectory;
    private readonly string _bundledDirectory;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private volatile bool _installing;

    /// <summary>Manages the SenseVoice recognition payload.</summary>
    public LocalSpeechModelManager(string? modelDirectory = null, string? bundledModelDirectory = null)
        : this(LocalSpeechModelCatalog.Recognition, modelDirectory, bundledModelDirectory)
    {
    }

    public LocalSpeechModelManager(
        LocalSpeechModelPayload payload,
        string? modelDirectory = null,
        string? bundledModelDirectory = null)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _modelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
            ? payload.GetDefaultDirectory()
            : Path.GetFullPath(modelDirectory);
        _bundledDirectory = string.IsNullOrWhiteSpace(bundledModelDirectory)
            ? payload.GetBundledDirectory()
            : Path.GetFullPath(bundledModelDirectory);
    }

    public LocalSpeechModelPayload Payload => _payload;

    /// <summary>Directory the recognizer should load from; falls back to the downloaded copy when neither is usable.</summary>
    public string ModelDirectory => ResolveUsableDirectory() ?? _modelDirectory;

    public LocalSpeechModelStatus GetStatus()
    {
        if (_installing)
        {
            return new(LocalSpeechModelState.Installing, _payload.Id,
                _payload.DisplayName, _modelDirectory, TotalSize(_bundledDirectory), "正在安装");
        }

        if (MeasurePayload(_bundledDirectory) is { } bundledSize)
        {
            return new(LocalSpeechModelState.Ready, _payload.Id,
                _payload.DisplayName, _bundledDirectory, bundledSize, "内置模型已就绪", IsBundled: true);
        }

        if (MeasurePayload(_modelDirectory) is { } installedSize)
        {
            return new(LocalSpeechModelState.Ready, _payload.Id,
                _payload.DisplayName, _modelDirectory, installedSize, "本地模型已就绪");
        }

        // Only the payload files decide the state: a staging folder or any other
        // stray file left in the directory must not make an absent model look
        // like a broken one.
        if (PayloadFileNames.Any(file => File.Exists(Path.Combine(_modelDirectory, file))))
        {
            return new(LocalSpeechModelState.Invalid, _payload.Id,
                _payload.DisplayName, _modelDirectory, TotalSize(_modelDirectory), "模型文件不完整，请重新安装");
        }

        return new(LocalSpeechModelState.NotInstalled, _payload.Id,
            _payload.DisplayName, _modelDirectory, 0, "本地模型未安装");
    }

    public async Task<LocalSpeechModelStatus> InstallAsync(
        IProgress<LocalSpeechModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var current = GetStatus();
        if (current.State == LocalSpeechModelState.Ready)
        {
            // The bundled or already-downloaded payload removes the need for network access.
            return current;
        }

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have finished the install while this one waited.
            var latest = GetStatus();
            if (latest.State == LocalSpeechModelState.Ready)
            {
                return latest;
            }

            _installing = true;
            Directory.CreateDirectory(_modelDirectory);
            var stagingDirectory = _modelDirectory + ".download-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                long received = 0;
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(30);
                foreach (var file in PayloadFileNames)
                {
                    await DownloadFileAsync(
                        httpClient,
                        stagingDirectory,
                        file,
                        bytes =>
                        {
                            received += bytes;
                            progress?.Report(new LocalSpeechModelProgress(received, null, _payload.Id));
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (var file in PayloadFileNames)
                {
                    var source = Path.Combine(stagingDirectory, file);
                    var destination = Path.Combine(_modelDirectory, file);
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(source, destination);
                }

                var status = GetStatus();
                if (status.State != LocalSpeechModelState.Ready)
                {
                    throw new InvalidDataException("本地语音模型下载内容不完整。");
                }

                return status;
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
        finally
        {
            _installing = false;
            _installLock.Release();
        }
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The bundled copy is part of the application package and must survive;
        // only the downloaded copy in the portable data folder is removable.
        TryDeleteDirectory(_modelDirectory);
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> PayloadFileNames =>
        _payload.Files.Select(file => file.Name).ToArray();

    private async Task DownloadFileAsync(
        HttpClient httpClient,
        string targetDirectory,
        string fileName,
        Action<long> onBytes,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(targetDirectory, fileName + ".part");
        var finalPath = Path.Combine(targetDirectory, fileName);
        Exception? lastError = null;
        var downloaded = false;
        foreach (var uri in _payload.SourcesFor(fileName))
        {
            try
            {
                await using (var source = await httpClient.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 1024 * 128, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 128];
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        onBytes(read);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                TryDeleteFile(temporaryPath);
                continue;
            }

            // A mirror can answer with a truncated or substituted body, so the
            // payload is checked before it is promoted and the next mirror gets
            // a turn when it does not hold up.
            if (!IsPayloadFileValid(temporaryPath, fileName))
            {
                lastError = new InvalidDataException($"下载的模型文件 {fileName} 不完整。");
                TryDeleteFile(temporaryPath);
                continue;
            }

            downloaded = true;
            break;
        }

        if (!downloaded)
        {
            throw lastError ?? new HttpRequestException("无法下载本地语音模型。");
        }

        File.Move(temporaryPath, finalPath, overwrite: true);
    }

    private string? ResolveUsableDirectory() =>
        MeasurePayload(_bundledDirectory) is not null ? _bundledDirectory
        : MeasurePayload(_modelDirectory) is not null ? _modelDirectory
        : null;

    /// <summary>Total size when every payload file is present and large enough; otherwise null.</summary>
    private long? MeasurePayload(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        long total = 0;
        foreach (var file in PayloadFileNames)
        {
            var path = Path.Combine(directory, file);
            try
            {
                if (!File.Exists(path)) return null;
                var length = new FileInfo(path).Length;
                if (length < _payload.MinimumBytesFor(file)) return null;
                total += length;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        return total;
    }

    private static long TotalSize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return Directory.EnumerateFiles(directory).Sum(file =>
            {
                try { return new FileInfo(file).Length; }
                catch { return 0; }
            });
        }
        catch
        {
            return 0;
        }
    }

    private bool IsPayloadFileValid(string path, string fileName)
    {
        try
        {
            return new FileInfo(path).Length >= _payload.MinimumBytesFor(fileName);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A failed cleanup never masks the original install error.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}