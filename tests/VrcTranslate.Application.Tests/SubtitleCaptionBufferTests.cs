using VrcTranslate.Application.Subtitles;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class SubtitleCaptionBufferTests
{
    [Fact]
    public void The_default_log_keeps_the_most_recent_two_hundred_messages()
    {
        var buffer = new SubtitleCaptionBuffer();

        Assert.Equal(200, SubtitleCaptionBuffer.DefaultCapacity);
        Assert.Equal(200, buffer.Capacity);
        Assert.Equal(SubtitleContentMode.TranslatedWithOriginal, buffer.ContentMode);
    }

    [Fact]
    public void Reaching_the_cap_drops_the_oldest_messages_instead_of_growing()
    {
        var buffer = new SubtitleCaptionBuffer();

        for (var index = 1; index <= 250; index++)
        {
            Assert.True(buffer.Append($"line {index}", $"译文 {index}"));
        }

        Assert.Equal(200, buffer.Count);
        Assert.Equal(50, buffer.DroppedCount);
        Assert.Equal("line 51", buffer.Captions[0].Original);
        Assert.Equal("line 250", buffer.Captions[^1].Original);
    }

    [Fact]
    public void A_small_cap_still_keeps_the_newest_messages_in_arrival_order()
    {
        var buffer = new SubtitleCaptionBuffer(capacity: 3);

        foreach (var value in new[] { "a", "b", "c", "d", "e" })
        {
            buffer.Append(value, value.ToUpperInvariant());
        }

        Assert.Equal(["c", "d", "e"], buffer.Captions.Select(caption => caption.Original));
        Assert.Equal(2, buffer.DroppedCount);
    }

    [Fact]
    public void A_paused_log_keeps_every_message_and_ignores_new_ones()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append("first", "第一句");
        buffer.Append("second", "第二句");
        var before = buffer.Captions;

        buffer.Pause();
        var accepted = buffer.Append("third", "第三句");

        Assert.True(buffer.IsPaused);
        Assert.False(accepted);
        Assert.Equal(2, buffer.Count);
        // Pausing stops appending; it must not clear or rewrite what is already shown.
        Assert.Equal(before, buffer.Captions);
    }

    [Fact]
    public void Resuming_continues_after_the_existing_messages()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append("first", "第一句");
        buffer.Pause();
        buffer.Append("dropped", "被丢弃");
        buffer.Resume();
        Assert.True(buffer.Append("second", "第二句"));

        Assert.False(buffer.IsPaused);
        Assert.Equal(["first", "second"], buffer.Captions.Select(caption => caption.Original));
    }

    [Fact]
    public void Translated_only_presentation_hides_the_recognized_line()
    {
        var caption = new SubtitleCaption("hello world", "你好，世界");

        var lines = SubtitleCaptionBuffer.Present(caption, SubtitleContentMode.TranslatedOnly);

        var line = Assert.Single(lines);
        Assert.Equal("你好，世界", line.Text);
        Assert.False(line.IsOriginal);
    }

    [Fact]
    public void Translated_with_original_presentation_keeps_both_lines_in_one_message()
    {
        var buffer = new SubtitleCaptionBuffer(contentMode: SubtitleContentMode.TranslatedWithOriginal);
        Assert.True(buffer.Append("hello world", "你好，世界"));

        // One message carries both lines; the original is not a second message.
        var caption = Assert.Single(buffer.Captions);
        var lines = buffer.Present(caption);

        Assert.Equal(2, lines.Count);
        Assert.Equal("你好，世界", lines[0].Text);
        Assert.False(lines[0].IsOriginal);
        Assert.Equal("hello world", lines[1].Text);
        Assert.True(lines[1].IsOriginal);
    }

    [Fact]
    public void Switching_the_presentation_mode_changes_the_projection_not_the_messages()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append("hello world", "你好，世界");
        var caption = Assert.Single(buffer.Captions);

        buffer.SetContentMode(SubtitleContentMode.TranslatedOnly);
        Assert.Single(buffer.Present(caption));

        buffer.SetContentMode(SubtitleContentMode.TranslatedWithOriginal);
        Assert.Equal(2, buffer.Present(caption).Count);
        Assert.Single(buffer.Captions);
    }

    [Fact]
    public void A_missing_translation_still_shows_the_recognized_line()
    {
        var caption = new SubtitleCaption("hello world", "   ");

        var lines = SubtitleCaptionBuffer.Present(caption, SubtitleContentMode.TranslatedOnly);

        var line = Assert.Single(lines);
        Assert.Equal("hello world", line.Text);
        Assert.True(line.IsOriginal);
    }

    [Fact]
    public void A_speaker_label_stays_on_the_message()
    {
        var buffer = new SubtitleCaptionBuffer();
        Assert.True(buffer.Append("hello world", "你好，世界", "  小明  "));

        var caption = Assert.Single(buffer.Captions);
        Assert.True(caption.HasSpeaker);
        Assert.Equal("小明", caption.SpeakerLabel);
        // The label is its own part of the message; it never leaks into the text lines.
        Assert.DoesNotContain(buffer.Present(caption), line => line.Text.Contains("小明", StringComparison.Ordinal));
    }

    [Fact]
    public void A_message_without_a_speaker_carries_no_label()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append("hello world", "你好，世界");
        buffer.Append("hello again", "又见面了", "   ");

        Assert.All(buffer.Captions, caption => Assert.False(caption.HasSpeaker));
    }

    [Fact]
    public void A_caption_without_any_text_is_not_a_message()
    {
        var buffer = new SubtitleCaptionBuffer();

        Assert.False(buffer.Append("   ", null));
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Captions);
    }

    [Fact]
    public void The_log_must_retain_at_least_one_message()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubtitleCaptionBuffer(capacity: 0));
    }

    [Fact]
    public void Recognition_publishes_the_message_before_its_translation_arrives()
    {
        var buffer = new SubtitleCaptionBuffer();
        Assert.True(buffer.Append(SubtitleCaption.Pending("hello world", "小明", id: 7)));

        var caption = Assert.Single(buffer.Captions);
        Assert.Equal(7, caption.Id);
        Assert.Equal(SubtitleTranslationState.Pending, caption.TranslationState);
        Assert.True(caption.HasSpeaker);

        // The recognized line is readable on its own; nothing waits for the translation.
        var line = Assert.Single(buffer.Present(caption));
        Assert.Equal("hello world", line.Text);
        Assert.True(line.IsOriginal);
        Assert.Equal(SubtitleTranslationState.Pending, line.TranslationState);
    }

    [Fact]
    public void The_translation_fills_the_same_message_instead_of_adding_a_second_one()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", "小明", id: 7));

        Assert.True(buffer.TrySetTranslation(7, "你好，世界"));

        var caption = Assert.Single(buffer.Captions);
        Assert.Equal(7, caption.Id);
        Assert.Equal("你好，世界", caption.Translated);
        Assert.Equal("hello world", caption.Original);
        Assert.Equal("小明", caption.SpeakerLabel);
        Assert.Equal(SubtitleTranslationState.Ready, caption.TranslationState);

        // 译文在上、原文在下：one message, two lines, fixed order.
        var lines = buffer.Present(caption);
        Assert.Equal(2, lines.Count);
        Assert.Equal("你好，世界", lines[0].Text);
        Assert.False(lines[0].IsOriginal);
        Assert.Equal("hello world", lines[1].Text);
        Assert.True(lines[1].IsOriginal);
    }

    [Fact]
    public void An_empty_translation_leaves_the_recognized_line_instead_of_a_placeholder()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", id: 3));

        Assert.True(buffer.TrySetTranslation(3, "   "));

        var caption = Assert.Single(buffer.Captions);
        Assert.Equal(SubtitleTranslationState.Unavailable, caption.TranslationState);
        Assert.Equal(string.Empty, caption.Translated);
        var line = Assert.Single(buffer.Present(caption));
        Assert.Equal("hello world", line.Text);
        Assert.True(line.IsOriginal);
        Assert.Equal(SubtitleTranslationState.Unavailable, line.TranslationState);
    }

    [Fact]
    public void A_failed_translation_resolves_the_message_instead_of_leaving_it_pending()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", id: 5));

        // The failed path reports "no translation at all", not an empty attempt.
        Assert.True(buffer.TrySetTranslation(5, null));

        var caption = Assert.Single(buffer.Captions);
        Assert.Equal(SubtitleTranslationState.Unavailable, caption.TranslationState);
        Assert.Single(buffer.Present(caption));
    }

    [Fact]
    public void A_resolved_translation_is_never_overwritten_or_duplicated()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", id: 9));
        Assert.True(buffer.TrySetTranslation(9, "你好，世界"));

        Assert.False(buffer.TrySetTranslation(9, "迟到的译文"));

        var caption = Assert.Single(buffer.Captions);
        Assert.Equal("你好，世界", caption.Translated);
        Assert.Equal(SubtitleTranslationState.Ready, caption.TranslationState);
    }

    [Fact]
    public void Translated_only_shows_the_recognized_line_until_the_translation_arrives()
    {
        var buffer = new SubtitleCaptionBuffer(contentMode: SubtitleContentMode.TranslatedOnly);
        buffer.Append(SubtitleCaption.Pending("hello world", id: 11));

        // 仅译文 must not open an empty message while the translation is on its way.
        var pending = Assert.Single(buffer.Present(Assert.Single(buffer.Captions)));
        Assert.Equal("hello world", pending.Text);
        Assert.True(pending.IsOriginal);
        Assert.Equal(SubtitleTranslationState.Pending, pending.TranslationState);

        Assert.True(buffer.TrySetTranslation(11, "你好，世界"));

        var ready = Assert.Single(buffer.Present(Assert.Single(buffer.Captions)));
        Assert.Equal("你好，世界", ready.Text);
        Assert.False(ready.IsOriginal);
        Assert.Equal(SubtitleTranslationState.Ready, ready.TranslationState);
    }

    [Fact]
    public void An_unknown_id_never_touches_the_messages_already_shown()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", id: 4));
        var before = buffer.Captions;

        Assert.False(buffer.TrySetTranslation(99, "你好，世界"));
        // A caption without a correlation id can never be filled by accident.
        Assert.False(buffer.TrySetTranslation(0, "你好，世界"));

        Assert.Equal(before, buffer.Captions);
        Assert.Equal(SubtitleTranslationState.Pending, Assert.Single(buffer.Captions).TranslationState);
    }

    [Fact]
    public void A_message_is_readable_by_its_correlation_id()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", "小明", id: 12));

        Assert.True(buffer.TryGetCaption(12, out var caption));
        Assert.Equal("hello world", caption.Original);
        Assert.False(buffer.TryGetCaption(13, out _));
    }

    [Fact]
    public void A_message_already_on_screen_is_still_completed_while_paused()
    {
        var buffer = new SubtitleCaptionBuffer();
        buffer.Append(SubtitleCaption.Pending("hello world", id: 6));
        buffer.Pause();

        // Pausing stops new messages; the message the reader can already see still
        // receives the translation that was requested before the pause.
        Assert.True(buffer.TrySetTranslation(6, "你好，世界"));
        Assert.False(buffer.Append("late sentence", "迟到的句子"));

        var caption = Assert.Single(buffer.Captions);
        Assert.Equal("你好，世界", caption.Translated);
        Assert.True(buffer.IsPaused);
    }

    [Fact]
    public void The_bounded_log_drops_pending_messages_like_any_other()
    {
        var buffer = new SubtitleCaptionBuffer(capacity: 2);
        buffer.Append(SubtitleCaption.Pending("first", id: 21));
        buffer.Append(SubtitleCaption.Pending("second", id: 22));
        buffer.Append(SubtitleCaption.Pending("third", id: 23));

        // The evicted message cannot be filled any more; the retained ones can.
        Assert.False(buffer.TrySetTranslation(21, "第一句"));
        Assert.True(buffer.TrySetTranslation(22, "第二句"));
        Assert.Equal(["second", "third"], buffer.Captions.Select(caption => caption.Original));
    }
}
