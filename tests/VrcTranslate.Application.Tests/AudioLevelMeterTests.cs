using VrcTranslate.Application.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class AudioLevelMeterTests
{
    [Fact]
    public void A_silent_room_keeps_the_meter_at_zero()
    {
        var meter = new AudioLevelMeter();

        for (var i = 0; i < 20; i++)
        {
            meter.Observe(0.0005f);
            meter.Observe(0f);
            meter.Advance();
        }

        Assert.Equal(0d, meter.Level);
        Assert.All(meter.History, value => Assert.Equal(0d, value));
    }

    [Fact]
    public void Speech_fills_the_meter_instead_of_creeping_along_the_bottom()
    {
        var meter = new AudioLevelMeter();

        // -26 dBFS is a comfortable speaking voice and used to map to 20%.
        meter.Observe(0.05f);
        meter.Advance();

        Assert.True(
            meter.Level >= 0.70d,
            $"正常说话应点亮大部分电平条，实际 {meter.Level:0.00}。");
        Assert.True(
            meter.Level < 0.88d,
            $"正常说话不应直接冲进红区，实际 {meter.Level:0.00}。");
    }

    [Fact]
    public void A_shout_reaches_the_red_zone_above_normal_speech()
    {
        var meter = new AudioLevelMeter();
        meter.Observe(0.05f);
        meter.Advance();
        var speaking = meter.Level;

        meter.Observe(0.5f);
        meter.Advance();

        Assert.True(meter.Level >= 0.88d, $"大声应进入红区，实际 {meter.Level:0.00}。");
        Assert.True(meter.Level > speaking, "大声必须比正常说话更高，否则电平条失去意义。");
    }

    [Fact]
    public void A_quiet_passage_after_a_loud_one_reads_lower()
    {
        var meter = new AudioLevelMeter();
        meter.Observe(0.3f);
        meter.Advance();
        var loud = meter.Level;

        meter.Observe(0.02f);
        meter.Advance();

        Assert.True(meter.Level < loud, "小声应当显示得比大声低。");
    }

    [Fact]
    public void Level_falls_back_gradually_and_then_stops()
    {
        var meter = new AudioLevelMeter();
        meter.Observe(0.2f);
        meter.Advance();
        var loud = meter.Level;

        // The first ticks hold across the capture gap, then the meter releases.
        meter.Advance();
        meter.Advance();
        meter.Advance();
        var decayed = meter.Level;

        Assert.True(decayed < loud, "声音停下来后电平必须回落。");
        Assert.True(decayed > 0, "回落应当是渐进的，而不是瞬间归零。");

        for (var i = 0; i < 120; i++) meter.Advance();
        Assert.Equal(0d, meter.Level);
    }

    [Fact]
    public void The_level_holds_between_capture_callbacks()
    {
        // Capture delivers a buffer every ~100 ms while the meter draws every 60 ms,
        // so a tick with no new audio must not make the bars sag.
        var meter = new AudioLevelMeter();
        meter.Observe(0.2f);
        meter.Advance();
        var afterObservation = meter.Level;

        meter.Advance();
        meter.Advance();

        Assert.Equal(afterObservation, meter.Level);
    }

    [Fact]
    public void The_loudest_reading_between_ticks_wins()
    {
        var meter = new AudioLevelMeter();

        meter.Observe(0.03f);
        meter.Observe(0.2f);
        meter.Observe(0.01f);
        meter.Advance();

        Assert.True(meter.Level > 0.5d, "两个显示帧之间出现的大声不能被丢掉。");
    }

    [Fact]
    public void History_scrolls_oldest_first()
    {
        var meter = new AudioLevelMeter(new AudioLevelMeterOptions { BarCount = 3 });
        meter.Observe(0.01f);
        meter.Advance();
        meter.Observe(0.05f);
        meter.Advance();

        var history = meter.History.ToArray();

        Assert.Equal(3, history.Length);
        Assert.Equal(0d, history[0]);
        Assert.True(history[2] > history[1]);
    }

    [Fact]
    public void Reset_clears_the_meter_and_the_history()
    {
        var meter = new AudioLevelMeter();
        meter.Observe(0.2f);
        meter.Advance();

        meter.Reset();

        Assert.Equal(0d, meter.Level);
        Assert.All(meter.History, value => Assert.Equal(0d, value));
    }
}