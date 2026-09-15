using VrcTranslate.Application.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class SpeechSegmenterTests
{
    [Fact]
    public void Emits_one_sentence_after_speech_and_trailing_silence()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.01f, silenceMilliseconds: 100, maximumMilliseconds: 4_000);
        var speech = Enumerable.Repeat(0.2f, 4_800).ToArray();
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
        Assert.Empty(segmenter.Flush());
    }

    [Fact]
    public void Quiet_speech_below_the_old_fixed_threshold_is_captured()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.012f, silenceMilliseconds: 100, maximumMilliseconds: 4_000);
        // A quiet microphone: ambient around 0.002 pulls the adapted gate down
        // to its floor, so voice at 0.01 — under the old fixed 0.012 — passes.
        Assert.Empty(segmenter.Append(Enumerable.Repeat(0.002f, 40_000).ToArray()));

        var speech = Enumerable.Repeat(0.01f, 4_800).ToArray();
        Assert.Empty(segmenter.Append(speech));

        var sentence = Assert.Single(segmenter.Append(new float[2_000]));
        Assert.True(sentence.Length >= speech.Length);
    }

    [Fact]
    public void Loud_background_stays_closed_until_voice_exceeds_the_adapted_threshold()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.012f, silenceMilliseconds: 100, maximumMilliseconds: 4_000);
        // A game bed at 0.02 lifts the gate to the 0.05 cap, so a 0.03 murmur
        // (above the old fixed 0.012) no longer floods the recognizer.
        Assert.Empty(segmenter.Append(Enumerable.Repeat(0.02f, 40_000).ToArray()));
        Assert.Empty(segmenter.Append(Enumerable.Repeat(0.03f, 4_800).ToArray()));
        Assert.Empty(segmenter.Append(new float[2_000]));

        Assert.Empty(segmenter.Append(Enumerable.Repeat(0.1f, 4_800).ToArray()));
        var sentence = Assert.Single(segmenter.Append(new float[2_000]));
        Assert.True(sentence.Length >= 4_800);
    }

    [Fact]
    public void Transient_bursts_shorter_than_the_minimum_speech_window_are_dropped()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.012f, silenceMilliseconds: 100, maximumMilliseconds: 4_000);

        Assert.Empty(segmenter.Append(Enumerable.Repeat(0.5f, 1_600).ToArray()));
        Assert.Empty(segmenter.Append(new float[2_000]));
        Assert.Empty(segmenter.Flush());
        Assert.Equal(1, segmenter.Diagnostics.DroppedShortSegments);
        Assert.Equal(0, segmenter.Diagnostics.CompletedSegments);
    }

    [Fact]
    public void Speech_after_silence_keeps_the_pre_roll()
    {
        var segmenter = new SpeechSegmenter(16_000, energyThreshold: 0.01f, silenceMilliseconds: 100, maximumMilliseconds: 4_000);
        Assert.Empty(segmenter.Append(new float[16_000]));

        var speech = Enumerable.Repeat(0.2f, 4_800).ToArray();
        Assert.Empty(segmenter.Append(speech));

        var sentence = Assert.Single(segmenter.Append(new float[2_000]));
        // The leading 320 ms of context rides along so onsets survive.
        Assert.True(sentence.Length > speech.Length);
    }
}
