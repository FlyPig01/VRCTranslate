using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Models shipped through the local speech component. SenseVoiceSmall INT8
/// replaces the earlier Whisper Base bundle: the V1 benchmark measured
/// 174~207 ms/sentence on CPU with 4.9%~10% error rates across Chinese,
/// English, Japanese, and Korean, versus 31.6% CER on Chinese with Whisper.
/// </summary>
public static class LocalSpeechModelCatalog
{
    public const string ModelId = "sensevoice-small-int8";
    public const string ModelDirectory = "sensevoice";
    public const string ModelFileName = "model.int8.onnx";
    public const string TokensFileName = "tokens.txt";
    public const string DisplayName = "SenseVoice Small · 本地识别";

    public static readonly IReadOnlyList<SpeechLanguageOption> Languages =
        LocalSpeechLanguages.Supported;

    /// <summary>Model folder shipped inside the publish output, relative to the executable.</summary>
    public const string BundledRelativeFolder = "Models";

    /// <summary>Full path of the bundled model folder, whether or not it exists.</summary>
    public static string GetBundledModelDirectory() => Path.Combine(
        AppContext.BaseDirectory, BundledRelativeFolder, ModelDirectory);

    /// <summary>
    /// Where an on-demand download is written: inside the portable data folder,
    /// never in the user profile.
    /// </summary>
    public static string DefaultDirectory => PortableStorage.Combine("models", ModelDirectory);

    /// <summary>All model payload files that must exist for the recognizer to load.</summary>
    public static IReadOnlyList<string> PayloadFiles { get; } = [ModelFileName, TokensFileName];
}
