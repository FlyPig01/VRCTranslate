using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Storage;

/// <summary>
/// Persists Tencent Cloud ASR preferences. The SecretKey is encrypted with
/// Windows DPAPI for the current user; the JSON document never contains it in
/// plaintext. Only the account owner on this Windows profile can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TencentAsrSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _sync = new();

    public TencentAsrSettingsStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public TencentAsrSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return new TencentAsrSettings();
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions)
                    ?? new Document();
                return new TencentAsrSettings
                {
                    Enabled = document.Enabled,
                    Mode = document.Mode,
                    Engine = string.IsNullOrWhiteSpace(document.Engine) ? "16k_ja" : document.Engine,
                    FallbackToLocal = document.FallbackToLocal,
                    AppId = document.AppId ?? string.Empty,
                    SecretId = document.SecretId ?? string.Empty,
                    SecretKey = Unprotect(document.SecretKeyProtected),
                };
            }
            catch
            {
                // A damaged or DPAPI-unreadable file must not stop the app. The
                // UI will show "not configured" and the user can re-enter it.
                return new TencentAsrSettings();
            }
        }
    }

    public void Save(TencentAsrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var document = new Document
            {
                Enabled = settings.Enabled,
                Mode = settings.Mode,
                Engine = settings.Engine,
                FallbackToLocal = settings.FallbackToLocal,
                AppId = settings.AppId?.Trim() ?? string.Empty,
                SecretId = settings.SecretId?.Trim() ?? string.Empty,
                SecretKeyProtected = Protect(settings.SecretKey),
            };

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
        }
    }

    private static string Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        var clear = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(clear, optionalEntropy: null, DataProtectionScope.CurrentUser);
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
        public TencentAsrMode Mode { get; set; } = TencentAsrMode.Sentence;
        public string? Engine { get; set; }
        public bool FallbackToLocal { get; set; } = true;
        public string? AppId { get; set; }
        public string? SecretId { get; set; }
        public string? SecretKeyProtected { get; set; }
    }
}
