using VrcTranslate.Application.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class SpeechSegmenterTests
{
    [Fact]
    public void Emits_one_sentence_after_speech_and_trailing_silence()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.01f, silenceMilliseconds: 100, maximumMilliseconds: 2_000);
        var speech = Enumerable.Repeat(0.2f, 1_600).ToArray();
        var silence = new float[2_000];

        Assert.Empty(segmenter.Append(speech));
        var completed = segmenter.Append(silence);

        var sentence = Assert.Single(completed);
        Assert.True(sentence.Length >= speech.Length);
    }

    [Fact]
    public void Does_not_emit_background_silence()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.01f, silenceMilliseconds: 100, maximumMilliseconds: 2_000);

        Assert.Empty(segmenter.Append(new float[8_000]));
        Assert.True(segmenter.Flush().IsEmpty);
    }
}
