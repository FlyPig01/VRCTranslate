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
