using VrcTranslate.Core.Translation;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class TranslationOutputFormatterTests
{
    [Fact]
    public void Formats_primary_translation_and_original_when_second_translation_is_missing()
    {
        var output = TranslationOutputFormatter.Format("你好", "Hello");

        Assert.Equal("Hello / 你好", output);
    }

    [Fact]
    public void Formats_primary_second_translation_and_original_in_order()
    {
        var output = TranslationOutputFormatter.Format("你好", "Hello", "こんにちは");

        Assert.Equal("Hello / こんにちは / 你好", output);
    }

    [Theory]
    [InlineData(" 你好 ", " Hello ", " ", "Hello / 你好")]
    [InlineData("", "Hello", "こんにちは", "Hello / こんにちは")]
    [InlineData(null, "Hello", null, "Hello")]
    public void Trims_values_and_omits_empty_parts(
        string? original,
        string? primary,
        string? secondary,
        string expected)
    {
        Assert.Equal(expected, TranslationOutputFormatter.Format(original, primary, secondary));
    }

    [Fact]
    public void Formats_a_batch_using_the_same_order()
    {
        var route = CreateRoute();
        var request = new TextTranslationRequest("你好", route, TextTranslationSource.ManualText);
        var primary = new TextTranslationResult(
            request,
            "Hello",
            "zh-CN",
            TimeSpan.Zero,
            InvariantValidationResult.Valid);
        var secondary = new TextTranslationResult(
            request,
            "こんにちは",
            "zh-CN",
            TimeSpan.Zero,
            InvariantValidationResult.Valid);
        var batch = new TextTranslationBatchResult(
            request,
            new TranslationTargetSet("en-US", "ja-JP"),
            [primary, secondary]);

        Assert.Equal("Hello / こんにちは / 你好", batch.FormattedText);
        Assert.Equal(batch.FormattedText, TranslationOutputFormatter.Format(batch));
        Assert.Equal(batch.FormattedText, TranslationOutputFormatter.FormatForOsc(batch));
    }

    [Fact]
    public void Chatbox_payload_keeps_the_original_when_it_is_enabled()
    {
        var output = TranslationOutputFormatter.FormatForChatbox("你好", "Hello", "こんにちは", includeOriginal: true);

        Assert.Equal("Hello / こんにちは / 你好", output);
    }

    [Fact]
    public void Chatbox_payload_drops_the_original_when_it_is_disabled()
    {
        var output = TranslationOutputFormatter.FormatForChatbox("你好", "Hello", "こんにちは", includeOriginal: false);

        // Only the recognized line is optional; both translations stay, so a
        // second language never disappears with the original.
        Assert.Equal("Hello / こんにちは", output);
    }

    [Fact]
    public void Chatbox_payload_without_the_original_still_sends_a_translation()
    {
        Assert.Equal("Hello", TranslationOutputFormatter.FormatForChatbox("你好", "Hello", null, includeOriginal: false));
    }

    [Fact]
    public void Chatbox_payload_falls_back_to_the_original_when_nothing_was_translated()
    {
        // Turning the original off must never turn a failed translation into an
        // empty chatbox line: with no translation the recognized text is all
        // there is, so it is sent instead of clearing the box.
        Assert.Equal(
            "你好",
            TranslationOutputFormatter.FormatForChatbox("你好", primaryTranslation: null, secondaryTranslation: null, includeOriginal: false));
        Assert.Equal(
            "你好",
            TranslationOutputFormatter.FormatForChatbox("你好", primaryTranslation: "  ", secondaryTranslation: "", includeOriginal: false));
    }

    [Fact]
    public void Chatbox_payload_applies_the_chatbox_limit_after_dropping_the_original()
    {
        var original = new string('原', 200);

        // The trimmed payload never carries the original once it is off, even
        // though the untrimmed text would have been longer than the limit.
        var output = TranslationOutputFormatter.FormatForChatbox(original, "Hello", null, includeOriginal: false);

        Assert.Equal("Hello", output);
        Assert.True(
            TranslationOutputFormatter.FormatForChatbox(original, "Hello", null, includeOriginal: true).Length
            <= TranslationOutputFormatter.OscChatboxMaxUtf16Length);
    }

    [Fact]
    public void Batch_chatbox_payload_follows_the_same_switch()
    {
        var route = CreateRoute();
        var request = new TextTranslationRequest("你好", route, TextTranslationSource.ManualText);
        var primary = new TextTranslationResult(
            request,
            "Hello",
            "zh-CN",
            TimeSpan.Zero,
            InvariantValidationResult.Valid);
        var batch = new TextTranslationBatchResult(
            request,
            new TranslationTargetSet("en-US"),
            [primary]);

        Assert.Equal("Hello / 你好", TranslationOutputFormatter.FormatForChatbox(batch, includeOriginal: true));
        Assert.Equal("Hello", TranslationOutputFormatter.FormatForChatbox(batch, includeOriginal: false));
    }

    [Fact]
    public void Trims_for_chatbox_without_splitting_a_surrogate_pair()
    {
        var text = new string('a', 143) + "😀" + "tail";

        var trimmed = TranslationOutputFormatter.TrimForOsc(text, 144);

        Assert.Equal(143, trimmed.Length);
        Assert.DoesNotContain('\ud83d', trimmed);
        Assert.DoesNotContain('\ude00', trimmed);
    }

    [Fact]
    public void Rejects_a_non_positive_chatbox_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslationOutputFormatter.TrimForOsc("text", 0));
    }

    private static TranslationRoute CreateRoute() => new(
        "route-1",
        "Default",
        new TranslationProfile(
            "profile-1",
            "Local",
            "provider",
            "model",
            new Uri("https://example.test"),
            "credential"),
        new LanguagePolicy(SourceLanguageMode.AutoDetect, "zh-CN"));
}
