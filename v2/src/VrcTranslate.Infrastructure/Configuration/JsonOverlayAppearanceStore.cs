using System.Text.Json;
using System.Text.Json.Serialization;
using VrcTranslate.Application.Settings;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Infrastructure.Configuration;

/// <summary>
/// Stores overlay appearance in one small JSON file. Rapid slider changes are
/// collapsed into one atomic write after a short quiet period.
/// </summary>
public sealed class JsonOverlayAppearanceStore : IOverlayAppearanceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _path;
    private readonly object _sync = new();
    private readonly object _writeSync = new();
    private readonly Action<OverlayAppearanceSettings> _write;
    private OverlayAppearanceSettings? _pending;
    private Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public JsonOverlayAppearanceStore(string path)
        : this(path, writer: null)
    {
    }

    internal JsonOverlayAppearanceStore(
        string path,
        Action<OverlayAppearanceSettings>? writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _write = writer ?? Write;
    }

    public OverlayAppearanceSettings Load()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        try
        {
            if (!File.Exists(_path))
            {
                return OverlayAppearanceSettings.Default;
            }

            return (JsonSerializer.Deserialize<OverlayAppearanceSettings>(
                File.ReadAllText(_path),
                SerializerOptions) ?? OverlayAppearanceSettings.Default).Normalize();
        }
        catch (JsonException)
        {
            return OverlayAppearanceSettings.Default;
        }
        catch (IOException)
        {
            return OverlayAppearanceSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return OverlayAppearanceSettings.Default;
        }
    }

    public void Save(OverlayAppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = settings.Normalize();
            _dirty = true;
            _saveTimer?.Dispose();
            _saveTimer = new Timer(
                static state => ((JsonOverlayAppearanceStore)state!).Flush(),
                this,
                TimeSpan.FromMilliseconds(250),
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        // Only one writer may drain the queue. Without this gate, a delayed
        // timer flush could write an older snapshot after a newer explicit
        // flush and silently roll the user's last slider value back.
        lock (_writeSync)
        {
            while (true)
            {
                OverlayAppearanceSettings? snapshot;
                lock (_sync)
                {
                    if (!_dirty)
                    {
                        return;
                    }

                    snapshot = _pending;
                    _dirty = false;
                    _saveTimer?.Dispose();
                    _saveTimer = null;
                }

                if (snapshot is null)
                {
                    continue;
                }

                try
                {
                    _write(snapshot);
                }
                catch (IOException)
                {
                    RestorePendingAfterFailedWrite(snapshot);
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    RestorePendingAfterFailedWrite(snapshot);
                    return;
                }

                // A Save may have arrived while this snapshot was being
                // written. Loop under the same writer gate so that the newer
                // value is always the final value on disk.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        // Mark the store disposed before draining. This prevents a Save from
        // racing into the small gap between the final flush and disposal.
        Flush();

        GC.SuppressFinalize(this);
    }

    private void RestorePendingAfterFailedWrite(OverlayAppearanceSettings snapshot)
    {
        lock (_sync)
        {
            if (!_disposed && !_dirty)
            {
                _pending = snapshot;
                _dirty = true;
            }
        }
    }

    private void Write(OverlayAppearanceSettings settings)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings.Normalize(), SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The next successful save uses a unique temporary name.
                }
                catch (UnauthorizedAccessException)
                {
                    // A cleanup failure must not crash the running overlay.
                }
            }
        }
    }
}
