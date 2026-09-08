using System.Text.Json;
using VrcTranslate.Core.Settings;

namespace VrcTranslate.Infrastructure.Configuration;

/// <summary>
/// Persists the last position and size of each game overlay.
///
/// Layout changes arrive from the native window callback while a user is
/// dragging or resizing. Writes are therefore coalesced on a short timer and
/// replaced atomically so a process exit or a partial write cannot corrupt the
/// rest of the settings.
/// </summary>
public sealed class OverlayWindowLayoutStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, OverlayWindowLayout> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public OverlayWindowLayoutStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Load();
    }

    /// <summary>Returns the saved rectangle, or <c>null</c> if none is usable.</summary>
    public OverlayWindowLayout? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            return _layouts.TryGetValue(key.Trim(), out var layout) && layout.IsUsable
                ? layout
                : null;
        }
    }

    /// <summary>
    /// Updates a rectangle and schedules a coalesced atomic write.
    /// </summary>
    public void Set(string key, OverlayWindowLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!layout.IsUsable) return;

        lock (_sync)
        {
            if (_disposed) return;
            var normalizedKey = key.Trim();
            if (_layouts.TryGetValue(normalizedKey, out var previous) && previous == layout)
            {
                return;
            }

            _layouts[normalizedKey] = layout;
            _dirty = true;
            ScheduleSaveLocked();
        }
    }

    /// <summary>Flushes a pending write synchronously during application exit.</summary>
    public void Flush()
    {
        Dictionary<string, OverlayWindowLayout> snapshot;
        lock (_sync)
        {
            if (!_dirty) return;
            snapshot = new Dictionary<string, OverlayWindowLayout>(_layouts, StringComparer.OrdinalIgnoreCase);
            _dirty = false;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        try
        {
            Write(snapshot);
        }
        catch
        {
            // Keep the in-memory layout usable and retry on the next update.
            lock (_sync) _dirty = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        Flush();
        GC.SuppressFinalize(this);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var document = JsonSerializer.Deserialize<LayoutDocument>(File.ReadAllText(_path), SerializerOptions);
            if (document?.Windows is null) return;
            _layouts = document.Windows
                .Where(item => !string.IsNullOrWhiteSpace(item.Key) && item.Value.IsUsable)
                .ToDictionary(item => item.Key.Trim(), item => item.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A malformed optional layout file must never prevent the shell
            // from opening; the next successful save replaces it.
            _layouts = new Dictionary<string, OverlayWindowLayout>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ScheduleSaveLocked()
    {
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(
            static state => ((OverlayWindowLayoutStore)state!).Flush(),
            this,
            dueTime: TimeSpan.FromMilliseconds(250),
            period: Timeout.InfiniteTimeSpan);
    }

    private void Write(IReadOnlyDictionary<string, OverlayWindowLayout> layouts)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new LayoutDocument
            {
                Windows = new Dictionary<string, OverlayWindowLayout>(layouts, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private sealed class LayoutDocument
    {
        public Dictionary<string, OverlayWindowLayout> Windows { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
