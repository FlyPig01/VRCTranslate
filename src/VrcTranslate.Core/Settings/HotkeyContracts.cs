namespace VrcTranslate.Core.Settings;

public enum HotkeyAction
{
    OpenQuickInput,
    ToggleSpeechTranslation,
    ToggleSelfVoice
}

public sealed record HotkeyBinding
{
    public HotkeyBinding(HotkeyAction action, string gesture)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        Action = action;
        Gesture = Normalize(gesture);
    }

    public HotkeyAction Action { get; }
    public string Gesture { get; }

    public static string Normalize(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            throw new ArgumentException("Hotkey gesture is required.", nameof(gesture));
        }

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Hotkey gesture is required.", nameof(gesture));
        }

        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = NormalizeModifier(parts[index]);
            if (!modifiers.Add(modifier))
            {
                throw new ArgumentException("A hotkey cannot repeat a modifier.", nameof(gesture));
            }
        }

        var primary = parts[^1].ToUpperInvariant();
        if (IsModifier(primary) || !IsSupportedPrimary(primary))
        {
            throw new ArgumentException("The hotkey key is not supported.", nameof(gesture));
        }

        // Keep modifier order stable so aliases and reordered chords compare
        // as the same binding everywhere in the application.
        var normalized = new List<string>(4);
        foreach (var modifier in new[] { "CTRL", "ALT", "SHIFT", "WIN" })
        {
            if (modifiers.Contains(modifier)) normalized.Add(modifier);
        }
        normalized.Add(primary);
        return string.Join('+', normalized);
    }

    private static string NormalizeModifier(string value) => value.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => "CTRL",
        "ALT" or "MENU" => "ALT",
        "SHIFT" => "SHIFT",
        "WIN" or "WINDOWS" => "WIN",
        _ => throw new ArgumentException("The hotkey modifier is not supported.", nameof(value))
    };

    private static bool IsModifier(string value) => value is "CTRL" or "CONTROL" or "ALT" or "MENU" or "SHIFT" or "WIN" or "WINDOWS";

    private static bool IsSupportedPrimary(string value)
    {
        if (value.Length == 1 && ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= '0' && value[0] <= '9')))
        {
            return true;
        }

        return value.Length is >= 2 and <= 3 &&
            value[0] == 'F' &&
            int.TryParse(value[1..], out var functionKey) &&
            functionKey is >= 1 and <= 12;
    }
}
