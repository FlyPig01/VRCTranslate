using System.Runtime.Versioning;
using VrcTranslate.Core.Speech;
using VrcTranslate.Infrastructure.Speech;
using VrcTranslate.Infrastructure.Storage;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class QwenAsrSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"vrctranslate-qwen-asr-{Guid.NewGuid():N}");

    [Fact]
    public void Round_trips_workspace_region_and_protects_api_key()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "v2-qwen-asr.json");
        var store = new QwenAsrSettingsStore(path);
        store.Save(new QwenAsrSettings
        {
            Enabled = true,
            ApiKey = "sk-test-secret",
            WorkspaceId = "workspace-123",
            Region = "cn-beijing",
        });

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-test-secret", raw, StringComparison.Ordinal);

        var restored = new QwenAsrSettingsStore(path).Load();
        Assert.True(restored.Enabled);
        Assert.Equal("sk-test-secret", restored.ApiKey);
        Assert.Equal("workspace-123", restored.WorkspaceId);
        Assert.Equal("cn-beijing", restored.Region);
        Assert.True(restored.IsConfigured);
    }

    [Fact]
    public async Task Client_rejects_missing_configuration_without_opening_a_socket()
    {
        await using var client = new QwenLiveTranslateClient(new QwenAsrSettings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.TestAsync());
    }

    [Fact]
    public void Missing_file_is_unconfigured()
    {
        var settings = new QwenAsrSettingsStore(Path.Combine(_directory, "missing.json")).Load();
        Assert.False(settings.IsConfigured);
        Assert.StartsWith("qwen3.8", settings.Model, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
