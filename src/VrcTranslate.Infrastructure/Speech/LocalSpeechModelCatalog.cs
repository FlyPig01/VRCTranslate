using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Models shipped through the local speech component. SenseVoiceSmall INT8
/// replaces the earlier Whisper Base bundle: the V1 benchmark measured
/// 174~207 ms/sentence on CPU with 4.9%~10% error rates across Chinese,
/// English, Japanese, and Korean, versus 31.6% CER on Chinese with Whisper.
/// The speaker payload adds the two models the caption speaker labels need.
/// </summary>
public static class LocalSpeechModelCatalog
{
    public const string ModelId = "sensevoice-small-int8";
    public const string ModelDirectory = "sensevoice";
    public const string ModelFileName = "model.int8.onnx";
    public const string TokensFileName = "tokens.txt";
    public const string DisplayName = "SenseVoice Small · 本地识别";

    public const string SpeakerModelId = "speaker-separation-int8";
    public const string SpeakerDirectory = "speaker";
    public const string SegmentationFileName = "segmentation.int8.onnx";
    public const string EmbeddingFileName = "embedding.onnx";
    public const string SpeakerDisplayName = "说话人区分 · 本地模型";

    /// <summary>Silero VAD gate that keeps game sound out of the recognizer.</summary>
    public const string VadDirectory = "vad";
    public const string VadFileName = "silero_vad.onnx";

    /// <summary>Model folder shipped inside the publish output, relative to the executable.</summary>
    public const string BundledRelativeFolder = "Models";

    private const string SenseVoiceRepository =
        "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/";
    private const string SenseVoiceFallbackRepository =
        "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/";

    public static readonly IReadOnlyList<SpeechLanguageOption> Languages =
        LocalSpeechLanguages.Supported;

    /// <summary>SenseVoiceSmall INT8 recognition payload.</summary>
    public static LocalSpeechModelPayload Recognition { get; } = new(
        ModelId,
        DisplayName,
        ModelDirectory,
        [
            new LocalSpeechModelFile(ModelFileName, 100_000_000),
            new LocalSpeechModelFile(TokensFileName, 10_000),
        ],
        [new Uri(SenseVoiceRepository), new Uri(SenseVoiceFallbackRepository)],
        ExpectedBytes: 239_549_735);

    /// <summary>
    /// Speaker separation payload: pyannote segmentation locates speaker changes
    /// and the 3D-Speaker CAM++ embedding produces the voiceprint a caption label
    /// is attached to. Both files ship inside the package, so the explicit
    /// sources only matter for a package built without the bundled asset.
    /// </summary>
    public static LocalSpeechModelPayload Speaker { get; } = new(
        SpeakerModelId,
        SpeakerDisplayName,
        SpeakerDirectory,
        [
            new LocalSpeechModelFile(
                SegmentationFileName,
                1_000_000,
                new Uri("https://hf-mirror.com/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.int8.onnx")),
            new LocalSpeechModelFile(
                EmbeddingFileName,
                20_000_000,
                new Uri("https://hf-mirror.com/csukuangfj/speaker-embedding-models/resolve/main/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx")),
        ],
        [],
        ExpectedBytes: 29_821_670);

    public static IReadOnlyList<LocalSpeechModelPayload> All { get; } = [Recognition, Speaker];

    /// <summary>Full path of the bundled recognition folder, whether or not it exists.</summary>
    public static string GetBundledModelDirectory() => Recognition.GetBundledDirectory();

    /// <summary>Where an on-demand recognition download is written.</summary>
    public static string DefaultDirectory => Recognition.GetDefaultDirectory();

    /// <summary>Recognition payload files, in load order.</summary>
    public static IReadOnlyList<string> PayloadFiles { get; } =
        Recognition.Files.Select(file => file.Name).ToArray();
}