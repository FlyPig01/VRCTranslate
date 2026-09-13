using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Application.Settings;

/// <summary>Maps global hotkeys to user actions without exposing UI or Win32 types.</summary>
public sealed class HotkeyActionRouter
{
    private readonly IReadOnlyDictionary<string, HotkeyAction> _actions;
    private readonly IHotkeyActionHandler _handler;

    public HotkeyActionRouter(SystemSettings settings, IHotkeyActionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        var bindings = new[]
        {
            new HotkeyBinding(HotkeyAction.OpenQuickInput, settings.QuickInputHotkey),
            new HotkeyBinding(HotkeyAction.ToggleSpeechTranslation, settings.VoiceHotkey),
            new HotkeyBinding(HotkeyAction.ToggleSelfVoice, settings.SelfVoiceHotkey)
        };

        var actions = new Dictionary<string, HotkeyAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            if (!actions.TryAdd(binding.Gesture, binding.Action))
            {
                throw new ArgumentException($"Hotkey '{binding.Gesture}' is assigned more than once.", nameof(settings));
            }
        }

        _actions = actions;
    }

    public bool TryResolve(string gesture, out HotkeyAction action)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            action = default;
            return false;
        }

        string normalized;
        try
        {
            normalized = HotkeyBinding.Normalize(gesture);
        }
        catch (ArgumentException)
        {
            // A global-hotkey source can deliver malformed text while a user is
            // editing a binding. TryResolve must keep its Try contract and let
            // the caller report an unbound gesture instead of crashing.
            action = default;
            return false;
        }

        return _actions.TryGetValue(normalized, out action);
    }

    public Task DispatchAsync(string gesture, CancellationToken cancellationToken = default)
    {
        if (!TryResolve(gesture, out var action))
        {
            throw new KeyNotFoundException($"Hotkey '{gesture}' is not configured.");
        }

        return _handler.HandleAsync(action, cancellationToken);
    }
}
