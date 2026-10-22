using System.Reflection;
using System.Runtime.Versioning;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class QwenLiveTranslateClientTests
{
    [Fact]
    public void Source_delta_events_accumulate_instead_of_showing_only_the_last_chunk()
    {
        var client = new QwenLiveTranslateClient(new QwenAsrSettings
        {
            ApiKey = "test",
            WorkspaceId = "workspace",
        });
        var updates = new List<QwenCaptionUpdate>();
        client.CaptionUpdated += (_, update) => updates.Add(update);

        Invoke(client, """{"type":"conversation.item.created","item":{"id":"tr-1"},"previous_item_id":"src-1"}""");
        Invoke(client, """{"type":"conversation.item.input_audio_transcription.delta","item_id":"src-1","delta":"你"}""");
        Invoke(client, """{"type":"conversation.item.input_audio_transcription.delta","item_id":"src-1","delta":"好"}""");

        Assert.NotEmpty(updates);
        Assert.Equal("你好", updates[^1].Original);
    }

    private static void Invoke(QwenLiveTranslateClient client, string json)
    {
        var method = typeof(QwenLiveTranslateClient).GetMethod(
            "HandleEvent", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleEvent not found");
        method.Invoke(client, [json]);
    }
}
