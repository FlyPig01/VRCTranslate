namespace VrcTranslate.Core.Settings;

/// <summary>
/// Last known screen-space rectangle for a game overlay.
/// Coordinates and dimensions are physical pixels, matching WinAppSDK's
/// <c>AppWindow.Position</c> and <c>AppWindow.Size</c> APIs.
/// </summary>
public readonly record struct OverlayWindowLayout(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Reject values that could produce an invisible or unreasonably large
    /// native window after a damaged settings file is edited.
    /// </summary>
    public bool IsUsable =>
        Width is >= 240 and <= 10000 &&
        Height is >= 56 and <= 10000;
}
