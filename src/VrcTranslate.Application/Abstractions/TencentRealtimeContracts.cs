using VrcTranslate.Core.Speech;

namespace VrcTranslate.Application.Abstractions;

/// <summary>
/// One stable sentence returned by the real-time recognizer. Times are relative
/// to the stream start; <see cref="Audio"/> is a copy of the same interval so
/// the caller can attach a local speaker label without keeping the whole stream.
/// </summary>
public sealed record TencentRealtimeResult(
    string Text,
    string? MessageId,
    TimeSpan Start,
    TimeSpan End,
    ReadOnlyMemory<float> Audio);

/// <summary>
/// A single real-time ASR connection. One instance owns one voice_id and one
/// WebSocket; a reconnect must create a new instance.
/// </summary>
public interface ITencentRealtimeRecognizer : IAsyncDisposable
{
    event EventHandler<TencentRealtimeResult>? ResultReady;

    event EventHandler<Exception>? Faulted;

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues captured 16 kHz mono audio; never blocks the capture callback.</summary>
    void Append(ReadOnlyMemory<float> samples, int sampleRate);

    /// <summary>Sends the protocol end frame and waits for the final result.</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a connection, sends the end frame, and verifies authentication.</summary>
    Task TestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates one real-time connection per capture session.</summary>
public interface ITencentRealtimeRecognizerFactory
{
    ITencentRealtimeRecognizer Create(TencentAsrSettings settings);
}
