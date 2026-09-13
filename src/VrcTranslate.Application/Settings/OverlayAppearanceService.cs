using VrcTranslate.Core.Settings;

namespace VrcTranslate.Application.Settings;

/// <summary>
/// Synchronous application service shared by settings pages and overlay windows.
/// File formats and write scheduling remain behind <see cref="IOverlayAppearanceStore"/>.
/// </summary>
public sealed class OverlayAppearanceService : IDisposable
{
    private readonly IOverlayAppearanceStore _store;
    private readonly object _sync = new();
    private OverlayAppearanceSettings _current;
    private bool _disposed;

    public OverlayAppearanceService(IOverlayAppearanceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _current = (_store.Load() ?? OverlayAppearanceSettings.Default).Normalize();
    }

    public OverlayAppearanceSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public void SetInputOpacity(double opacity) => Update(
        current => current with
        {
            InputOverlayOpacity = OverlayAppearanceSettings.NormalizeOpacity(opacity)
        });

    public void SetSubtitleOpacity(double opacity) => Update(
        current => current with
        {
            SubtitleOverlayOpacity = OverlayAppearanceSettings.NormalizeOpacity(opacity)
        });

    public void Flush()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        _store.Flush();
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
        }

        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Update(Func<OverlayAppearanceSettings, OverlayAppearanceSettings> change)
    {
        OverlayAppearanceSettings next;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            next = change(_current).Normalize();
            if (next == _current)
            {
                return;
            }

            _current = next;
            _store.Save(next);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
