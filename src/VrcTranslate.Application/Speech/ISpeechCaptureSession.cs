using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Speech;

/// <summary>
/// The shell sees one capture session regardless of whether sentences come from
/// the local segmenter or a real-time cloud stream. Both implementations own the
/// same events and lifecycle so <c>VoiceSessionHost</c> never branches on them.
/// </summary>
public interface ISpeechCaptureSession : IAsyncDisposable
{
    event EventHandler<SpeechRecognitionResult>? ResultReady;

    /// <summary>Non-stable original text for the live line; empty text clears it.</summary>
    event EventHandler<TencentRealtimePartial>? PartialReady;

    event EventHandler<Exception>? Faulted;

    event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    event EventHandler<AudioLevelEventArgs>? LevelChanged;

    event EventHandler<string>? Diagnostic;

    event EventHandler<SpeechSegmenterFallbackEventArgs>? SegmenterFallback;

    bool IsStarted { get; }

    AudioSourceState SourceState { get; }

    bool ConsumeFallbackNotice();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
