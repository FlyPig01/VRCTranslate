using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class SpeechContractsTests
{
    [Fact]
    public void Local_model_exposes_only_requested_languages()
    {
        Assert.Equal(["en", "ja", "ko"], LocalSpeechLanguages.Supported.Select(item => item.Code));
        Assert.DoesNotContain(LocalSpeechLanguages.Supported, item => item.Code == "zh-CN");
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("en-US", "en")]
    [InlineData("en", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko_KR", "ko")]
    public void Language_tags_are_normalized(string input, string expected)
    {
        Assert.True(LocalSpeechLanguages.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("")]
    public void Unsupported_language_tags_are_rejected(string input)
    {
        if (input == "zh-CN")
        {
            Assert.True(LocalSpeechLanguages.TryNormalize(input, out var chinese));
            Assert.Equal(LocalSpeechLanguages.SelfChinese, chinese);
            return;
        }

        Assert.False(LocalSpeechLanguages.TryNormalize(input, out _));
        Assert.Throws<ArgumentException>(() => new SpeechRecognitionRequest(
            new float[1600], 16_000, input));
    }

    [Fact]
    public void Recognition_request_validates_audio_and_keeps_request_id()
    {
        var request = new SpeechRecognitionRequest(new float[1600], 16_000, "en-US", "sample-1");

        Assert.Equal("sample-1", request.RequestId);
        Assert.Equal("en", request.SourceLanguage);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeechRecognitionRequest(
            new float[1600], 4_000, "en"));
    }

    [Fact]
    public void Progress_fraction_is_bounded_and_unknown_when_size_is_unknown()
    {
        Assert.Equal(0.5, new LocalSpeechModelProgress(50, 100, "model").Fraction);
        Assert.Null(new LocalSpeechModelProgress(50, null, "model").Fraction);
    }
}
