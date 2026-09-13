using VrcTranslate.Core.Settings;

namespace VrcTranslate.Application.Settings;

/// <summary>Persistence boundary for the shared overlay appearance snapshot.</summary>
public interface IOverlayAppearanceStore : IDisposable
{
    OverlayAppearanceSettings Load();

    void Save(OverlayAppearanceSettings settings);

    void Flush();
}
