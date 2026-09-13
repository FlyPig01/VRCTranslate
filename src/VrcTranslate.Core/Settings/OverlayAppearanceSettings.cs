namespace VrcTranslate.Core.Settings;

/// <summary>Shared visual settings for the two game overlay windows.</summary>
public sealed record OverlayAppearanceSettings
{
    public const double DefaultOpacity = 0.90d;
    public const double MinimumOpacity = 0.60d;
    public const double MaximumOpacity = 1.00d;

    public double InputOverlayOpacity { get; init; } = DefaultOpacity;

    public double SubtitleOverlayOpacity { get; init; } = DefaultOpacity;

    public static OverlayAppearanceSettings Default { get; } = new();

    /// <summary>Returns a safe snapshot suitable for rendering and persistence.</summary>
    public OverlayAppearanceSettings Normalize() => new()
    {
        InputOverlayOpacity = NormalizeOpacity(InputOverlayOpacity),
        SubtitleOverlayOpacity = NormalizeOpacity(SubtitleOverlayOpacity)
    };

    public static double NormalizeOpacity(double value)
    {
        if (!double.IsFinite(value))
        {
            return DefaultOpacity;
        }

        return Math.Clamp(value, MinimumOpacity, MaximumOpacity);
    }
}
