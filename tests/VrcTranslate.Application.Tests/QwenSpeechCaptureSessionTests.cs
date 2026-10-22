using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class QwenSpeechCaptureSessionTests
{
    [Fact]
    public async Task Maps_cloud_speaker_ids_to_stable_letters_and_forwards_audio()
    {
        var capture = new FakeAudioCapture(new AudioCaptureRequest(AudioCaptureSourceKind.SystemLoopback));
        var client = new FakeQwenClient();
        await using var session = new QwenSpeechCaptureSession(capture, client);
        var labels = new List<string?>();
        session.CaptionUpdated += (_, update) => labels.Add(update.SpeakerLabel);

        await session.StartAsync();
        capture.Emit(new float[320]);
        client.Emit(new QwenCaptionUpdate("item-1", "こんにちは", "你好", 7, null, false));
        client.Emit(new QwenCaptionUpdate("item-2", "そうですね", "是啊", 9, null, false));
        client.Emit(new QwenCaptionUpdate("item-3", "またね", "再见", 7, null, true));

        Assert.Equal(1, client.StartCount);
        Assert.Equal(1, client.AppendCount);
        Assert.Equal(["A", "B", "A"], labels);
    }
}

internal sealed class FakeQwenClient : IQwenLiveTranslateClient
{
    public event EventHandler<QwenCaptionUpdate>? CaptionUpdated;

    public event EventHandler<Exception>? Faulted
    {
        add { }
        remove { }
    }

    public int StartCount { get; private set; }

    public int AppendCount { get; private set; }

    public int CompleteCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public void Append(ReadOnlyMemory<float> samples, int sampleRate) => AppendCount++;

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        CompleteCount++;
        return Task.CompletedTask;
    }

    public Task TestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Emit(QwenCaptionUpdate update) => CaptionUpdated?.Invoke(this, update);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
