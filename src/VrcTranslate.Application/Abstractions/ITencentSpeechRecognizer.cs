using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Abstractions;

/// <summary>
/// Optional cloud speech recognizer. Kept in the application layer so the
/// capture session never references an HTTP implementation directly.
/// </summary>
public interface ITencentSpeechRecognizer : IDisposable
{
    Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken = default);

    Task TestAsync(CancellationToken cancellationToken = default);
}
