using VrcTranslate.Core.Settings;
using VrcTranslate.Infrastructure.Configuration;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class JsonOverlayAppearanceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"vrctranslate-overlay-appearance-{Guid.NewGuid():N}");

    [Fact]
    public void Missing_file_uses_defaults()
    {
        using var store = CreateStore();

        Assert.Equal(OverlayAppearanceSettings.Default, store.Load());
    }

    [Fact]
    public void Flush_round_trips_both_opacities_and_removes_temporary_file()
    {
        using (var store = CreateStore())
        {
            store.Save(new OverlayAppearanceSettings
            {
                InputOverlayOpacity = 0.72d,
                SubtitleOverlayOpacity = 0.84d
            });
            store.Flush();
        }

        using var restored = CreateStore();
        var settings = restored.Load();
        Assert.Equal(0.72d, settings.InputOverlayOpacity);
        Assert.Equal(0.84d, settings.SubtitleOverlayOpacity);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Corrupt_file_uses_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{not-json");

        using var store = CreateStore();

        Assert.Equal(OverlayAppearanceSettings.Default, store.Load());
    }

    [Fact]
    public void Invalid_saved_values_are_normalized_on_load()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "inputOverlayOpacity": 0.1,
              "subtitleOverlayOpacity": 4.0
            }
            """);

        using var store = CreateStore();
        var settings = store.Load();

        Assert.Equal(0.60d, settings.InputOverlayOpacity);
        Assert.Equal(1.00d, settings.SubtitleOverlayOpacity);
    }

    [Fact]
    public void Named_non_finite_values_use_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "inputOverlayOpacity": "NaN",
              "subtitleOverlayOpacity": "Infinity"
            }
            """);

        using var store = CreateStore();
        var settings = store.Load();

        Assert.Equal(0.90d, settings.InputOverlayOpacity);
        Assert.Equal(0.90d, settings.SubtitleOverlayOpacity);
    }

    [Fact]
    public async Task Rapid_saves_are_coalesced_to_the_latest_snapshot()
    {
        using var store = CreateStore();
        for (var index = 0; index < 20; index++)
        {
            store.Save(new OverlayAppearanceSettings
            {
                InputOverlayOpacity = 0.60d + (index * 0.01d),
                SubtitleOverlayOpacity = 0.70d
            });
        }

        await Task.Delay(100);
        Assert.False(File.Exists(SettingsPath));

        await Task.Delay(500);
        using var restored = CreateStore();
        Assert.Equal(0.79d, restored.Load().InputOverlayOpacity, precision: 10);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_flushes_cannot_overwrite_a_newer_snapshot_with_an_older_one()
    {
        using var firstWriteStarted = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        var written = new List<OverlayAppearanceSettings>();
        var writeCount = 0;
        using var store = new JsonOverlayAppearanceStore(
            SettingsPath,
            settings =>
            {
                if (Interlocked.Increment(ref writeCount) == 1)
                {
                    firstWriteStarted.Set();
                    Assert.True(releaseFirstWrite.Wait(TimeSpan.FromSeconds(5)));
                }

                lock (written)
                {
                    written.Add(settings);
                }
            });

        store.Save(new OverlayAppearanceSettings
        {
            InputOverlayOpacity = 0.65d,
            SubtitleOverlayOpacity = 0.75d
        });
        var olderFlush = Task.Run(store.Flush);
        Assert.True(firstWriteStarted.Wait(TimeSpan.FromSeconds(5)));

        store.Save(new OverlayAppearanceSettings
        {
            InputOverlayOpacity = 0.85d,
            SubtitleOverlayOpacity = 0.95d
        });
        var newerFlush = Task.Run(store.Flush);
        releaseFirstWrite.Set();

        await Task.WhenAll(olderFlush, newerFlush).WaitAsync(TimeSpan.FromSeconds(5));

        lock (written)
        {
            Assert.Equal(2, written.Count);
            Assert.Equal(0.65d, written[0].InputOverlayOpacity);
            Assert.Equal(0.85d, written[1].InputOverlayOpacity);
            Assert.Equal(0.95d, written[^1].SubtitleOverlayOpacity);
        }
    }

    private string SettingsPath => Path.Combine(_directory, "overlay-appearance.json");

    private JsonOverlayAppearanceStore CreateStore() => new(SettingsPath);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
