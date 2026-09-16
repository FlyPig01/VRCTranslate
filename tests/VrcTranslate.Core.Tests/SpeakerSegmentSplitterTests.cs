using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Core.Tests;

/// <summary>
/// The splitter turns segmentation change points into ranges that are safe to
/// recognize separately. A point near an edge used to be dropped - which threw the
/// pulled in to the nearest position that keeps every piece recognizable.
/// </summary>
public sealed class SpeakerSegmentSplitterTests
{
    [Fact]
    public void No_change_points_means_no_split()
    {
        Assert.Empty(SpeakerSegmentSplitter.Split(10_000, [], 1_000, 3));
    }

    [Fact]
    public void A_point_near_the_start_is_pulled_instead_of_dropped()
    {
        // 200 samples in is closer than the 1_000-sample floor: clamping keeps the
        // split (one 1_000-sample piece + the rest) instead of returning nothing.
        var spans = SpeakerSegmentSplitter.Split(10_000, [200], 1_000, 3);

        Assert.Equal(2, spans.Count);
        Assert.Equal(new SpeechSpan(0, 1_000), spans[0]);
        Assert.Equal(new SpeechSpan(1_000, 10_000), spans[1]);
    }

    [Fact]
    public void A_point_near_the_end_is_pulled_instead_of_dropped()
    {
        var spans = SpeakerSegmentSplitter.Split(10_000, [9_800], 1_000, 3);

        Assert.Equal(2, spans.Count);
        Assert.Equal(new SpeechSpan(0, 9_000), spans[0]);
        Assert.Equal(new SpeechSpan(9_000, 10_000), spans[1]);
    }

    [Fact]
    public void Points_closer_than_the_floor_are_merged_into_one()
    {
        var spans = SpeakerSegmentSplitter.Split(10_000, [4_000, 4_200], 1_000, 3);

        Assert.Equal(2, spans.Count);
        Assert.Equal(new SpeechSpan(0, 4_000), spans[0]);
        Assert.Equal(new SpeechSpan(4_000, 10_000), spans[1]);
    }

    [Fact]
    public void A_segment_shorter_than_two_pieces_still_splits_at_the_middle()
    {
        // Two 1_000-sample pieces cannot fit in 1_500 samples; clamping to the
        // midpoint is the only boundary that leaves both pieces non-empty.
        var spans = SpeakerSegmentSplitter.Split(1_500, [1_400], 1_000, 3);

        Assert.Equal(2, spans.Count);
        Assert.Equal(new SpeechSpan(0, 750), spans[0]);
        Assert.Equal(new SpeechSpan(750, 1_500), spans[1]);
    }

    [Fact]
    public void More_points_than_the_cap_are_truncated()
    {
        var spans = SpeakerSegmentSplitter.Split(10_000, [2_000, 5_000, 8_000], 1_000, 3);

        Assert.Equal(3, spans.Count);
        Assert.Equal(new SpeechSpan(0, 2_000), spans[0]);
        Assert.Equal(new SpeechSpan(2_000, 5_000), spans[1]);
        Assert.Equal(new SpeechSpan(5_000, 10_000), spans[2]);
    }

    [Fact]
    public void Every_piece_is_non_empty_and_covers_the_whole_segment()
    {
        var spans = SpeakerSegmentSplitter.Split(9_000, [100, 4_500, 8_950], 800, 4);

        Assert.True(spans.Count >= 2);
        Assert.Equal(0, spans[0].StartSample);
        Assert.Equal(9_000, spans[^1].EndSample);
        for (var index = 0; index < spans.Count; index++)
        {
            Assert.True(spans[index].Length > 0);
            if (index > 0) Assert.Equal(spans[index - 1].EndSample, spans[index].StartSample);
        }
    }

    [Fact]
    public void A_cap_of_one_or_less_never_splits()
    {
        Assert.Empty(SpeakerSegmentSplitter.Split(10_000, [5_000], 1_000, 1));
    }
}
