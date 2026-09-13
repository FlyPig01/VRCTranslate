using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>One file of a model payload.</summary>
/// <param name="Name">File name inside the payload folder.</param>
/// <param name="MinimumBytes">
/// Floor used to tell a complete file from an interrupted download or a
/// placeholder. Deliberately below the upstream size so a re-exported model is
/// still accepted.
/// </param>
/// <param name="Source">
/// Full upstream URL when the payload does not live in a single repository, or
/// when the local file name differs from the upstream one.
/// </param>
public sealed record LocalSpeechModelFile(string Name, long MinimumBytes, Uri? Source = null);

/// <summary>
/// A self-contained set of model files the local speech component can load:
/// which folder it lives in, which files it must contain, how large they have
/// to be, and where a recovery download would come from.
/// </summary>
public sealed record LocalSpeechModelPayload(
    string Id,
    string DisplayName,
    string DirectoryName,
    IReadOnlyList<LocalSpeechModelFile> Files,
    IReadOnlyList<Uri> Repositories,
    long ExpectedBytes)
{
    public bool Contains(string fileName) => Files.Any(file =>
        string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase));

    public long MinimumBytesFor(string fileName) => Files
        .FirstOrDefault(file => string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase))
        ?.MinimumBytes ?? 0;

    /// <summary>Every upstream URL to try for one file, in order.</summary>
    public IReadOnlyList<Uri> SourcesFor(string fileName)
    {
        var file = Files.FirstOrDefault(item =>
            string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (file?.Source is not null) return [file.Source];
        return Repositories.Select(repository => new Uri(repository, fileName)).ToArray();
    }

    /// <summary>Folder shipped inside the publish output, relative to the executable.</summary>
    public string GetBundledDirectory() => Path.Combine(
        AppContext.BaseDirectory, LocalSpeechModelCatalog.BundledRelativeFolder, DirectoryName);

    /// <summary>Where an on-demand download goes: the portable data folder.</summary>
    public string GetDefaultDirectory() => PortableStorage.Combine("models", DirectoryName);
}