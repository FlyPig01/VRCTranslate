namespace VrcTranslate.Core.Settings;

public sealed record SettingsIssue(string Path, string Message);

public sealed record SettingsValidationResult(IReadOnlyList<SettingsIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class WorkspaceSettingsValidator
{
    public static SettingsValidationResult Validate(WorkspaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<SettingsIssue>();

        if (settings.Version < 1)
        {
            issues.Add(new("Version", "Settings version must be at least 1."));
        }

        if (settings.Translation is null)
        {
            issues.Add(new("Translation", "Translation settings are required."));
            return new SettingsValidationResult(issues);
        }

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = settings.Translation.Profiles;
        if (profiles is null)
        {
            issues.Add(new("Translation.Profiles", "Profiles collection is required."));
        }

        if (profiles is not null)
        {
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                if (profile is null)
                {
                    issues.Add(new($"Translation.Profiles[{index}]", "Profile is required."));
                    continue;
                }

                if (!profileIds.Add(profile.ProfileId))
                {
                    issues.Add(new($"Translation.Profiles[{index}].ProfileId", "Profile ID must be unique."));
                }
            }
        }

        ValidateRoute(settings.Translation.DefaultRoute, "Translation.DefaultRoute", profileIds, issues);
        ValidateRoute(settings.Translation.SelfRoute, "Translation.SelfRoute", profileIds, issues);
        ValidateRoute(settings.Translation.VoiceRoute, "Translation.VoiceRoute", profileIds, issues);

        if (settings.Osc is null || settings.Osc.Port is < 1 or > 65535)
        {
            issues.Add(new("Osc.Port", "OSC port must be between 1 and 65535."));
        }

        if (settings.Voice is null)
        {
            issues.Add(new("Voice", "Voice settings are required."));
        }

        if (settings.SelfVoice is null)
        {
            issues.Add(new("SelfVoice", "Self voice settings are required."));
        }

        if (settings.Glossary is null)
        {
            issues.Add(new("Glossary", "Glossary settings are required."));
        }

        if (settings.System is null)
        {
            issues.Add(new("System", "System settings are required."));
        }
        else
        {
            var gestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddGesture("System.QuickInputHotkey", settings.System.QuickInputHotkey, gestures, issues);
            AddGesture("System.VoiceHotkey", settings.System.VoiceHotkey, gestures, issues);
            AddGesture("System.SelfVoiceHotkey", settings.System.SelfVoiceHotkey, gestures, issues);
        }

        return new SettingsValidationResult(issues);
    }

    private static void ValidateRoute(
        TranslationRouteSettings? route,
        string path,
        IReadOnlySet<string> profileIds,
        ICollection<SettingsIssue> issues)
    {
        if (route is null)
        {
            issues.Add(new(path, "Route settings are required."));
            return;
        }

        if (string.IsNullOrWhiteSpace(route.ProfileId))
        {
            issues.Add(new($"{path}.ProfileId", "Profile ID is required."));
        }
        else if (profileIds.Count > 0 && !profileIds.Contains(route.ProfileId))
        {
            issues.Add(new($"{path}.ProfileId", "Referenced profile does not exist."));
        }

        if (route.QueueLimit < 1)
        {
            issues.Add(new($"{path}.QueueLimit", "Queue limit must be positive."));
        }

        if (route.MaxRetries is < 0 or > 10)
        {
            issues.Add(new($"{path}.MaxRetries", "Max retries must be between 0 and 10."));
        }

        if (route.TimeoutSeconds <= 0 || route.TimeoutSeconds > 300 || route.TaskTtlSeconds <= 0)
        {
            issues.Add(new(path, "Timeout must be between 0 and 300 seconds, and task TTL must be positive."));
        }
    }

    private static void AddGesture(
        string path,
        string? gesture,
        IDictionary<string, string> gestures,
        ICollection<SettingsIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            issues.Add(new(path, "Hotkey is required."));
            return;
        }

        string normalized;
        try
        {
            normalized = HotkeyBinding.Normalize(gesture);
        }
        catch (ArgumentException)
        {
            issues.Add(new(path, "Hotkey key or modifier is not supported."));
            return;
        }

        if (!gestures.TryAdd(normalized, path))
        {
            issues.Add(new(path, $"Hotkey conflicts with {gestures[normalized]}"));
        }
    }
}
