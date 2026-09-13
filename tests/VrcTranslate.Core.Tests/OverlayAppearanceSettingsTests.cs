using VrcTranslate.Core.Settings;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class OverlayAppearanceSettingsTests
{
    [Fact]
    public void Defaults_both_overlays_to_ninety_percent()
    {
        var settings = new OverlayAppearanceSettings();

        Assert.Equal(0.90d, settings.InputOverlayOpacity);
        Assert.Equal(0.90d, settings.SubtitleOverlayOpacity);
    }

    [Theory]
    [InlineData(0.25d, 0.60d)]
    [InlineData(0.60d, 0.60d)]
    [InlineData(0.83d, 0.83d)]
    [InlineData(1.00d, 1.00d)]
    [InlineData(2.00d, 1.00d)]
    public void Normalizes_finite_values_into_the_supported_range(double value, double expected)
    {
        Assert.Equal(expected, OverlayAppearanceSettings.NormalizeOpacity(value));
    }

    [Fact]
    public void Non_finite_values_fall_back_to_ninety_percent()
    {
        Assert.Equal(0.90d, OverlayAppearanceSettings.NormalizeOpacity(double.NaN));
        Assert.Equal(0.90d, OverlayAppearanceSettings.NormalizeOpacity(double.PositiveInfinity));
        Assert.Equal(0.90d, OverlayAppearanceSettings.NormalizeOpacity(double.NegativeInfinity));
    }

    [Fact]
    public void Normalizes_each_overlay_independently()
    {
        var normalized = new OverlayAppearanceSettings
        {
            InputOverlayOpacity = 0.4d,
            SubtitleOverlayOpacity = 1.4d
        }.Normalize();

        Assert.Equal(0.60d, normalized.InputOverlayOpacity);
        Assert.Equal(1.00d, normalized.SubtitleOverlayOpacity);
    }
}
