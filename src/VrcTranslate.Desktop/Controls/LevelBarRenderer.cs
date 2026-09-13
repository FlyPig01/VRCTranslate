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
    private readonly StackPanel _host;
    private Rectangle[] _bars = [];
    private RectangleGeometry[] _clips = [];
    private double _height;
    private double _barWidth;
    private double _gap;

    /// <summary>Bars currently drawn; follows the meter width.</summary>
    public int BarCount => _bars.Length;

    public LevelBarRenderer(
        StackPanel host,
        int barCount = 16,
        double height = 22,
        double barWidth = 6,
        double gap = 2)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _height = height;
        Rebuild(barCount, barWidth, gap);
    }

    /// <summary>Rebuilds the bars for a new width; a no-op when nothing changed.</summary>
    public void Resize(int barCount, double barWidth, double gap)
    {
        if (barCount == _bars.Length && Math.Abs(barWidth - _barWidth) < 0.01 && Math.Abs(gap - _gap) < 0.01) return;
        Rebuild(barCount, barWidth, gap);
    }

    private void Rebuild(int barCount, double barWidth, double gap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(barCount, 1);
        _barWidth = barWidth;
        _gap = gap;

        _host.Children.Clear();
        _host.Orientation = Orientation.Horizontal;
        _host.Spacing = gap;

        var fill = CreateMeterBrush();
        _bars = new Rectangle[barCount];
        _clips = new RectangleGeometry[barCount];
        for (var index = 0; index < barCount; index++)
        {
            var clip = new RectangleGeometry { Rect = new Rect(0, _height, barWidth, 0) };
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = _height,
                RadiusX = barWidth / 2,
                RadiusY = barWidth / 2,
                Fill = fill,
                Clip = clip,
            };
            _clips[index] = clip;
            _bars[index] = bar;
            _host.Children.Add(bar);
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