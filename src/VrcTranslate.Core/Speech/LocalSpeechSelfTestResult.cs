namespace VrcTranslate.Core.Speech;

/// <summary>
/// Outcome of the one startup check of the local speech stack. The check loads
/// the shipped recognition model and runs a known speech sample through the
/// segmenter and the recognizer, so a machine whose models cannot run stops
/// presenting an empty caption window without explanation.
/// </summary>
/// <param name="ModelReady">The model payload passed the size check.</param>
/// <param name="VadUsable">
/// The native Silero VAD loaded and produced at least one segment on the sample.
/// False also means the session must keep using the energy gate instead.
/// </param>
/// <param name="AsrUsable">The sample produced non-empty text.</param>
/// <param name="Message">Short user-facing reason or confirmation.</param>
/// <param name="Detail">Technical detail (exception text), never shown as the main reason.</param>
/// <param name="Duration">How long the check took.</param>
public sealed record LocalSpeechSelfTestResult(
    bool ModelReady,
    bool VadUsable,
    bool AsrUsable,
    string Message,
    string? Detail = null,
    TimeSpan Duration = default)
{
    /// <summary>True when captions can be produced; the VAD can fall back.</summary>
    public bool IsUsable => ModelReady && AsrUsable;

    /// <summary>One-line status for the voice page and diagnostics.</summary>
    public string Summary =>
        IsUsable
            ? (VadUsable ? "自检通过" : "自检通过（VAD 不可用，使用备用分段器）")
            : $"自检失败：{Message}";
}
