using VrcTranslate.Application.Settings;
using VrcTranslate.Core.Settings;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class OverlayAppearanceServiceTests
{
    [Fact]
    public void Loads_a_normalized_snapshot()
    {
        using var service = new OverlayAppearanceService(new RecordingStore(new OverlayAppearanceSettings
        {
            InputOverlayOpacity = 0.2d,
            SubtitleOverlayOpacity = double.NaN
        }));

        Assert.Equal(0.60d, service.Current.InputOverlayOpacity);
        Assert.Equal(0.90d, service.Current.SubtitleOverlayOpacity);
    }

    [Fact]
    public void Setters_update_and_save_each_overlay_independently()
    {
        var store = new RecordingStore(new OverlayAppearanceSettings
        {
            InputOverlayOpacity = 0.70d,
            SubtitleOverlayOpacity = 0.80d
        });
        using var service = new OverlayAppearanceService(store);

        service.SetInputOpacity(0.75d);
        Assert.Equal(0.75d, service.Current.InputOverlayOpacity);
        Assert.Equal(0.80d, service.Current.SubtitleOverlayOpacity);

        service.SetSubtitleOpacity(0.85d);
        Assert.Equal(0.75d, service.Current.InputOverlayOpacity);
        Assert.Equal(0.85d, service.Current.SubtitleOverlayOpacity);
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public void Raises_changed_only_when_the_effective_value_changes()
    {
        var store = new RecordingStore(new OverlayAppearanceSettings());
        using var service = new OverlayAppearanceService(store);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.SetInputOpacity(0.90d);
        service.SetInputOpacity(double.NaN);
        service.SetSubtitleOpacity(2.0d);
        service.SetSubtitleOpacity(1.5d);

        Assert.Equal(1, changes);
        Assert.Single(store.Saved);
        Assert.Equal(1.00d, service.Current.SubtitleOverlayOpacity);
    }

    [Fact]
    public void Flush_and_dispose_are_forwarded_to_the_store()
    {
        var store = new RecordingStore(new OverlayAppearanceSettings());
        var service = new OverlayAppearanceService(store);

        service.Flush();
        service.Dispose();
        service.Dispose();

        Assert.Equal(1, store.FlushCount);
        Assert.Equal(1, store.DisposeCount);
    }

    private sealed class RecordingStore(OverlayAppearanceSettings initial) : IOverlayAppearanceStore
    {
        public List<OverlayAppearanceSettings> Saved { get; } = [];

        public int FlushCount { get; private set; }

        public int DisposeCount { get; private set; }

        public OverlayAppearanceSettings Load() => initial;

        public void Save(OverlayAppearanceSettings settings) => Saved.Add(settings);

        public void Flush() => FlushCount++;

        public void Dispose() => DisposeCount++;
    }
}
