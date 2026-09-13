using System.Text.Json;
using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// The voiceprint library on disk (data/v2-speakers.json). It is small and
/// written rarely, so it stays synchronous: deciding what a caption says must
/// never wait on file IO.
/// </summary>
public sealed class SpeakerVoiceprintStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly string _embeddingModel;

    public SpeakerVoiceprintStore(string embeddingModel, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        _embeddingModel = embeddingModel;
        _path = Path.GetFullPath(path ?? PortableStorage.GetPath(AppDataFiles.Speakers));
    }

    /// <summary>Full path of the library file.</summary>
    public string LibraryPath => _path;

    /// <summary>Model that wrote the file when it differs from the one in use, otherwise null.</summary>
    public string? IncompatibleModel { get; private set; }

    public SpeakerVoiceprintDocument Load()
    {
        IncompatibleModel = null;
        try
        {
            if (!File.Exists(_path)) return SpeakerVoiceprintDocument.Empty;
            var document = JsonSerializer.Deserialize<SpeakerVoiceprintDocument>(
                File.ReadAllText(_path), SerializerOptions);
            if (document?.Speakers is null) return SpeakerVoiceprintDocument.Empty;

            if (!string.Equals(document.EmbeddingModel, _embeddingModel, StringComparison.Ordinal))
            {
                // Vectors from another model live in a different space, so matching
                // against them would produce confident nonsense. The file is set
                // aside once instead of being deleted.
                IncompatibleModel = document.EmbeddingModel ?? "unknown";
                SetAside();
                return SpeakerVoiceprintDocument.Empty;
            }

            return document;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            SetAside();
            return SpeakerVoiceprintDocument.Empty;
        }
    }

    public void Save(SpeakerVoiceprintDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A leftover temporary file is harmless.
            }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the file is not worth failing the user action over.
        }
    }

    private void SetAside()
    {
        try
        {
            if (File.Exists(_path)) File.Move(_path, _path + ".bak", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}