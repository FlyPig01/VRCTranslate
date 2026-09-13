using Whisper.net.Ggml;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>Models shipped through the optional local speech component.</summary>
public static class LocalSpeechModelCatalog
{
    // The multilingual base model recognizes Chinese, English, Japanese, and
    // Korean for this product while staying small enough for a desktop bundle.
    public const string ModelId = "whisper-base-q5_1";
    public const string FileName = "ggml-base-q5_1.bin";
    public const string DisplayName = "Whisper Base · 本地识别";

    public static readonly IReadOnlyList<SpeechLanguageOption> Languages =
        LocalSpeechLanguages.Supported;

    public static GgmlType GgmlType => GgmlType.Base;

    public static QuantizationType Quantization => QuantizationType.Q5_1;

    /// <summary>Model folder shipped inside the publish output, relative to the executable.</summary>
    public const string BundledRelativeFolder = "Models";

    /// <summary>Full path of the model bundled with the application, whether or not it exists.</summary>
    public static string GetBundledModelPath() => Path.Combine(
        AppContext.BaseDirectory, BundledRelativeFolder, FileName);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCTranslate",
        "v2",
        "models",
        "speech");
}
