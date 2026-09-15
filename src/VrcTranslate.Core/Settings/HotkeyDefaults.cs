namespace VrcTranslate.Core.Settings;

/// <summary>
/// The shipped shortcuts and the single rule every reader uses to turn what is
/// on disk into the gesture the application actually answers to.
/// </summary>
public static class HotkeyDefaults
{
    /// <summary>Opens or closes the quick-input overlay.</summary>
    public const string QuickInput = "Ctrl+Alt+I";

    /// <summary>Starts or stops other-player recognition together with its caption surface.</summary>
    public const string OtherPlayerVoice = "F7";

    /// <summary>Starts or stops the user's own voice recognition.</summary>
    public const string SelfVoice = "Ctrl+F8";

    /// <summary>
    /// Resolves one persisted shortcut exactly like the global poller reads it:
    /// an empty value is a deliberate "未设置", so that action keeps no key at
    /// all, while a value the poller cannot read keeps the shipped default. A
    /// usable value is canonicalized so every reader compares one spelling.
    /// </summary>
    /// <param name="stored">The raw value from the settings file; null or blank means the user cleared it.</param>
    /// <param name="fallback">The shipped default for that action.</param>
    public static string ResolvePersisted(string? stored, string fallback)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        try
        {
            return HotkeyBinding.Normalize(stored);
        }
        catch (ArgumentException)
        {
            return HotkeyBinding.Normalize(fallback);
        }
    }
}
