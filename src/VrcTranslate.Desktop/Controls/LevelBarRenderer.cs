using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace VrcTranslate.Desktop.Controls;

/// <summary>
/// Draws the capture level as a rolling bar meter.
/// 
/// Every bar is a full-height rectangle carrying a green-to-red gradient that is
/// anchored to the whole track, and the clip reveals only its lower part. Scaling
/// a gradient with the bar instead would make a quiet bar show the red end, which
/// is exactly backwards: the colour has to mean "how loud", not "how tall".
/// </summary>
public sealed class LevelBarRenderer
{
    private readonly Rectangle[] _bars;
    private readonly RectangleGeometry[] _clips;
    private readonly double _height;
    private readonly double _barWidth;

    public LevelBarRenderer(
        StackPanel host,
        int barCount = 16,
        double height = 22,
        double barWidth = 6,
        double gap = 2)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(barCount, 1);
        _height = height;
        _barWidth = barWidth;

        host.Children.Clear();
        host.Orientation = Orientation.Horizontal;
        host.Spacing = gap;

        var fill = CreateMeterBrush();
        _bars = new Rectangle[barCount];
        _clips = new RectangleGeometry[barCount];
        for (var index = 0; index < barCount; index++)
        {
            var clip = new RectangleGeometry { Rect = new Rect(0, height, barWidth, 0) };
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = height,
                RadiusX = barWidth / 2,
                RadiusY = barWidth / 2,
                Fill = fill,
                Clip = clip,
            };
            _clips[index] = clip;
            _bars[index] = bar;
            host.Children.Add(bar);
        }
    }

    /// <summary>Draws one history frame; missing entries read as silence.</summary>
    public void Render(IReadOnlyList<double> levels)
    {
        for (var index = 0; index < _bars.Length; index++)
        {
            var level = index < levels.Count ? Math.Clamp(levels[index], 0d, 1d) : 0d;
            var visible = level * _height;
            _clips[index].Rect = new Rect(0, _height - visible, _barWidth, visible);
        }
    }

    public void Reset() => Render([]);

    private static LinearGradientBrush CreateMeterBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0),
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0.00, Color = ColorHelper.FromArgb(255, 42, 168, 118) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.55, Color = ColorHelper.FromArgb(255, 124, 194, 106) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.78, Color = ColorHelper.FromArgb(255, 224, 160, 48) });
        brush.GradientStops.Add(new GradientStop { Offset = 1.00, Color = ColorHelper.FromArgb(255, 214, 74, 70) });
        return brush;
    }
}