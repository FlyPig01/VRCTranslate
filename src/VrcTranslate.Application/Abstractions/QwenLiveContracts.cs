namespace VrcTranslate.Application.Abstractions;

/// <summary>One live Qwen caption, updated in place as source text and translation arrive.</summary>
public sealed record QwenCaptionUpdate(
    string Id,
    string Original,
    string Translation,
    int? SpeakerId,
    string? SpeakerLabel,
    bool IsFinal);

/// <summary>One Qwen LiveTranslate WebSocket connection.</summary>
public interface IQwenLiveTranslateClient : IAsyncDisposable
{
    event EventHandler<QwenCaptionUpdate>? CaptionUpdated;

    event EventHandler<Exception>? Faulted;

    Task StartAsync(CancellationToken cancellationToken = default);

    void Append(ReadOnlyMemory<float> samples, int sampleRate);

    Task CompleteAsync(CancellationToken cancellationToken = default);

    Task TestAsync(CancellationToken cancellationToken = default);
}
