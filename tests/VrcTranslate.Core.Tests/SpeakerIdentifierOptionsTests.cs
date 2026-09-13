using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class SpeakerIdentifierOptionsTests
{
    [Fact]
    public void The_change_window_stays_at_the_measured_floor()
    {
        // Measured on the bundled CAM++ model: a 1 s window scores 0.45 for the
        // same speaker (indistinguishable from another speaker) while 2 s scores
        // 0.69 against 0.09. Shrinking this back makes every sentence look like it
        // holds two speakers, so the floor is a correctness constraint, not a
        // performance knob.
        var options = new SpeakerIdentifierOptions();

        Assert.True(
            options.ChangeWindowSeconds >= 2.0f,
            "说话人比较窗口不得小于 2 秒：1 秒窗口的同人相似度与跨人相似度重叠。");
    }

    [Fact]
    public void Two_comparison_windows_have_to_fit_in_a_splittable_segment()
    {
        var options = new SpeakerIdentifierOptions();

        Assert.True(options.MinimumSplitSeconds >= options.ChangeWindowSeconds * 2);
        Assert.InRange(options.ChangeSimilarityThreshold, 0.2f, 0.6f);
    }
}