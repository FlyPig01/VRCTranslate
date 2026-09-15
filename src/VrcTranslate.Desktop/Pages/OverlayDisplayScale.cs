using Microsoft.UI.Xaml;

namespace VrcTranslate.Desktop.Pages;

/// <summary>
/// Turns the design size of an overlay surface (device-independent pixels, the
/// unit every XAML length in this app is written in) into the physical pixels
/// the native window controller works with.
/// <para>
/// A window rectangle is always in physical pixels, so the same default numbers
/// used to describe the content measure differently on every scale: at 150% a
/// rectangle that was right for 150 DIP arrives as a window whose content is
/// only two thirds as wide. The two default surfaces therefore share this one
/// conversion instead of growing their own arithmetic.
/// </para>
/// </summary>
internal static class OverlayDisplayScale
{
    /// <summary>
    /// Display scale of the monitor the surface currently sits on, or zero when
    /// it is not known yet - before the first layout, or under a host without a
    /// XAML root (a design-time or UI test surface). Zero means "no conversion":
    /// the caller keeps the rectangle the native controller already applied.
    /// </summary>
    public static double Resolve(FrameworkElement? surface)
    {
        try
        {
            var scale = surface?.XamlRoot?.RasterizationScale ?? 0d;
            return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d ? 0d : scale;
        }
        catch
        {
            // Reading the scale is presentation polish; a host that cannot
            // answer keeps the unscaled default.
            return 0d;
        }
    }

    /// <summary>
    /// Converts one design size to physical pixels for the given scale. A scale
    /// the caller could not resolve returns zero, which every caller reads as
    /// "leave the current rectangle alone".
    /// </summary>
    public static int ToPhysicalPixels(double designPixels, double scale)
    {
        if (scale <= 0d || double.IsNaN(scale) || double.IsInfinity(scale)) return 0;
        var physical = designPixels * scale;
        return physical > int.MaxValue ? int.MaxValue : (int)Math.Ceiling(physical);
    }
}
