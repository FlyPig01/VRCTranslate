using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Storage;

/// <summary>Persists Qwen LiveTranslate settings; the API key is DPAPI-protected.</summary>
[SupportedOSPlatform("windows")]
public sealed class QwenAsrSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _sync = new();

    public QwenAsrSettingsStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public QwenAsrSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return new QwenAsrSettings();
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions)
                    ?? new Document();
                return new QwenAsrSettings
                {
                    Enabled = document.Enabled,
                    WorkspaceId = document.WorkspaceId ?? string.Empty,
                    Region = string.IsNullOrWhiteSpace(document.Region) ? "cn-beijing" : document.Region,
                    Model = string.IsNullOrWhiteSpace(document.Model)
                        ? "qwen3.8-livetranslate-flash-realtime"
                        : document.Model,
                    TargetLanguage = string.IsNullOrWhiteSpace(document.TargetLanguage) ? "zh" : document.TargetLanguage,
                    ApiKey = Unprotect(document.ApiKeyProtected),
                };
            }
            catch
            {
                return new QwenAsrSettings();
            }
        }
    }

    public void Save(QwenAsrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var document = new Document
            {
                Enabled = settings.Enabled,
                WorkspaceId = settings.WorkspaceId?.Trim() ?? string.Empty,
                Region = settings.Region,
                Model = settings.Model,
                TargetLanguage = settings.TargetLanguage,
                ApiKeyProtected = Protect(settings.ApiKey),
            };

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
        }
    }

    private static string Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret)) return string.Empty;
        var encrypted = Convert.FromBase64String(protectedSecret);
        var clear = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clear);
    }

    private sealed class Document
    {
        public bool Enabled { get; set; }
        public string? WorkspaceId { get; set; }
        public string? Region { get; set; }
        public string? Model { get; set; }
        public string? TargetLanguage { get; set; }
        public string? ApiKeyProtected { get; set; }
    }
}
