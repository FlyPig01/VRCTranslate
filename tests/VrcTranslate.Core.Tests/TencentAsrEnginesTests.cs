using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class TencentAsrEnginesTests
{
    [Theory]
    [InlineData("16k_ja", TencentAsrMode.Realtime, "16k_ja")]
    [InlineData("16k_zh-PY", TencentAsrMode.Sentence, "16k_zh-PY")]
    [InlineData("16k_zh-PY", TencentAsrMode.Realtime, "16k_zh")]
    [InlineData("16k_multi_lang", TencentAsrMode.Sentence, "16k_zh-PY")]
    [InlineData("16k_multi_lang", TencentAsrMode.Realtime, "16k_multi_lang")]
    [InlineData("16k_en_game", TencentAsrMode.Sentence, "16k_en")]
    public void Saved_engines_are_normalized_for_the_selected_api(
        string saved,
        TencentAsrMode mode,
        string expected)
    {
        Assert.Equal(expected, TencentAsrEngines.Normalize(saved, mode));
    }

    [Fact]
    public void Only_standard_realtime_engines_are_free()
    {
        Assert.False(TencentAsrEngines.IsPaidRealtimeEngine(TencentAsrEngines.Japanese));
        Assert.True(TencentAsrEngines.IsPaidRealtimeEngine(TencentAsrEngines.MultiLanguage));
        Assert.True(TencentAsrEngines.IsPaidRealtimeEngine(TencentAsrEngines.RealtimeChineseEnglish));
    }

    [Fact]
    public void Source_language_is_fixed_only_for_single_language_engines()
    {
        Assert.Equal("ja", TencentAsrEngines.SourceLanguage(TencentAsrEngines.Japanese));
        Assert.Equal("ko", TencentAsrEngines.SourceLanguage(TencentAsrEngines.Korean));
        Assert.Equal("en", TencentAsrEngines.SourceLanguage(TencentAsrEngines.English));
        Assert.Equal("zh", TencentAsrEngines.SourceLanguage(TencentAsrEngines.Chinese));
        Assert.Equal("auto", TencentAsrEngines.SourceLanguage(TencentAsrEngines.ChineseEnglish));
        Assert.Equal("auto", TencentAsrEngines.SourceLanguage(TencentAsrEngines.MultiLanguage));
    }
}
