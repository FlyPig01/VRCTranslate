using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class SpeakerIdentifierOptionsTests
{
    [Fact]
    public void The_split_floor_stays_where_the_segmentation_model_still_answers()
    {
        // Measured 2026-09-16 on a real two-speaker podcast: 2.5 s slices report a
        // change point in 6/24 cases and 3.0 s slices in 8/20, so 3 s is the shortest
        // slice that still carries usable evidence. Dropping below it only asks the
        // model questions it cannot answer.
        var options = new SpeakerIdentifierOptions();

        Assert.True(
            options.MinimumSplitSeconds >= 3.0f,
            "最小可切割时长不得低于 3 秒：更短的片段上分割模型几乎给不出换人点。");
    }

    [Fact]
    public void Every_piece_a_split_produces_stays_recognizable()
    {
        var options = new SpeakerIdentifierOptions();

        Assert.True(options.MinimumPartSeconds >= 0.5f);
        Assert.True(options.MinimumPartSeconds < options.MinimumSplitSeconds);
        Assert.InRange(options.MaxParts, 2, 4);
    }
}