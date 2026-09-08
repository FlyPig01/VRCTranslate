using Whisper.net.Ggml;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>Models shipped through the optional local speech component.</summary>
public static class LocalSpeechModelCatalog
{
    // The multilingual base model is small enough for an optional desktop
    // component while still covering the three languages exposed by V2.
    public const string ModelId = "whisper-base-q5_1";
    public const string FileName = "ggml-base-q5_1.bin";
    public const string DisplayName = "Whisper Base · 本地识别";

    public static readonly IReadOnlyList<SpeechLanguageOption> Languages =
        LocalSpeechLanguages.Supported;

    public static GgmlType GgmlType => GgmlType.Base;

    public static QuantizationType Quantization => QuantizationType.Q5_1;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCTranslate",
        "v2",
        "models",
        "speech");
}
