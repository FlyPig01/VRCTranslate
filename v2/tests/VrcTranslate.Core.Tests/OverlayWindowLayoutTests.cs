using VrcTranslate.Core.Settings;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class OverlayWindowLayoutTests
{
    [Fact]
    public void Accepts_normal_physical_pixel_rectangles()
    {
        Assert.True(new OverlayWindowLayout(100, 200, 1240, 150).IsUsable);
    }

    [Theory]
    [InlineData(239, 56)]
    [InlineData(240, 55)]
    [InlineData(10001, 100)]
    [InlineData(100, 10001)]
    public void Rejects_dimensions_outside_native_window_limits(int width, int height)
    {
        Assert.False(new OverlayWindowLayout(0, 0, width, height).IsUsable);
    }
}
