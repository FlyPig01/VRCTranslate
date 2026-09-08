using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Configuration;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class OverlayWindowLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"vrctranslate-overlay-layout-{Guid.NewGuid():N}");

    [Fact]
    public void Set_flushes_a_layout_and_loads_it_on_the_next_instance()
    {
        var path = Path.Combine(_directory, "overlay-layout.json");
        var layout = new OverlayWindowLayout(128, 256, 1240, 150);

        using (var store = new OverlayWindowLayoutStore(path))
        {
            store.Set("quick-input", layout);
            store.Flush();
        }

        using var restored = new OverlayWindowLayoutStore(path);
        Assert.Equal(layout, restored.Get("quick-input"));
    }

    [Fact]
    public void Invalid_layouts_and_corrupt_files_are_ignored()
    {
        var path = Path.Combine(_directory, "overlay-layout.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"windows\":{\"quick-input\":{\"x\":0,\"y\":0,\"width\":\"bad\"}}}");

        using var store = new OverlayWindowLayoutStore(path);
        Assert.Null(store.Get("quick-input"));

        store.Set("quick-input", new OverlayWindowLayout(0, 0, 100, 40));
        store.Flush();
        Assert.Null(store.Get("quick-input"));
    }

    [Fact]
    public async Task Rapid_updates_are_coalesced_without_leaving_temporary_files()
    {
        var path = Path.Combine(_directory, "overlay-layout.json");
        using var store = new OverlayWindowLayoutStore(path);
        for (var index = 0; index < 20; index++)
        {
            store.Set("subtitle", new OverlayWindowLayout(index, index, 1400 + index, 150));
        }

        await Task.Delay(500);
        var loaded = new OverlayWindowLayoutStore(path);
        Assert.Equal(new OverlayWindowLayout(19, 19, 1419, 150), loaded.Get("subtitle"));
        loaded.Dispose();
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
