using System.Text.Json;
using VrcTranslate.Infrastructure.Configuration;
using VrcTranslate.Infrastructure.Translation;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Osc;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class JsonConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vrctranslate-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveThenLoadRoundTripsAndLeavesNoTemporaryFiles()
    {
        var path = Path.Combine(_directory, "settings", "settings.json");
        var store = new JsonConfigurationStore<TestSettings>(path);

        await store.SaveAsync(new TestSettings("zh-CN", true));
        await store.SaveAsync(new TestSettings("ja-JP", false));
        var loaded = await store.LoadAsync();

        Assert.Equal("ja-JP", loaded.TargetLanguage);
        Assert.False(loaded.SpeechEnabled);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task MissingFileReturnsDefaultValue()
    {
        var store = new JsonConfigurationStore<TestSettings>(Path.Combine(_directory, "missing.json"));

        var loaded = await store.LoadAsync();

        Assert.Equal(string.Empty, loaded.TargetLanguage);
        Assert.False(loaded.SpeechEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public sealed class TestSettings
    {
        public TestSettings()
        {
        }

        public TestSettings(string targetLanguage, bool speechEnabled)
        {
            TargetLanguage = targetLanguage;
            SpeechEnabled = speechEnabled;
        }

        public string TargetLanguage { get; set; } = string.Empty;

        public bool SpeechEnabled { get; set; }
    }
}

public sealed class EchoTranslationProviderTests
{
    [Fact]
    public async Task ReturnsInputWithoutNetworkAccess()
    {
        var provider = new EchoTranslationProvider();
        var request = new TranslationProviderRequest(
            "Hello",
            "zh-CN",
            "en-US",
            "echo",
            new Uri("https://example.test"),
            "test-credential",
            "test-correlation");

        var result = await provider.TranslateAsync(request);

        Assert.Equal("echo", provider.Id);
        Assert.Equal("Hello", result.TranslatedText);
        Assert.Equal("en-US", result.DetectedSourceLanguage);
    }
}

public sealed class TranslationProviderCatalogTests
{
    [Fact]
    public void ResolvesProvidersByCaseInsensitiveId()
    {
        var provider = new EchoTranslationProvider();
        var catalog = new TranslationProviderCatalog([provider]);

        Assert.Same(provider, catalog.Get("ECHO"));
        Assert.True(catalog.TryGet("echo", out var resolved));
        Assert.Same(provider, resolved);
        Assert.Contains("echo", catalog.Ids);
    }

    [Fact]
    public void Reports_only_registered_provider_adapters()
    {
        var catalog = new TranslationProviderCatalog(TranslationProviderRegistry.CreateShippedProviders());

        // The shipped set is the whole catalog: the removal of DeepL and Google
        // is asserted here, from the same factory the desktop shell composes.
        Assert.Equal(
            TranslationProviderRegistry.ShippedProviderIds.OrderBy(id => id, StringComparer.Ordinal),
            catalog.Ids.OrderBy(id => id, StringComparer.Ordinal));
        Assert.True(catalog.IsRegistered("echo"));
        Assert.True(catalog.IsRegistered("deepseek"));
        Assert.True(catalog.IsRegistered("tencent"));
        Assert.True(catalog.IsRegistered("aliyun"));
        Assert.False(catalog.IsRegistered("openai-compatible"));
        Assert.False(catalog.IsRegistered("deepl"));
        Assert.False(catalog.IsRegistered("google-free"));
        Assert.False(catalog.IsRegistered("google-cloud"));
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        var providers = new VrcTranslate.Infrastructure.Translation.ITranslationProvider[]
        {
            new EchoTranslationProvider(),
            new EchoTranslationProvider()
        };

        Assert.Throws<ArgumentException>(() => new TranslationProviderCatalog(providers));
    }

    [Fact]
    public void ReportsUnknownProvider()
    {
        var catalog = new TranslationProviderCatalog([new EchoTranslationProvider()]);

        Assert.Throws<KeyNotFoundException>(() => catalog.Get("missing"));
        Assert.False(catalog.TryGet("missing", out _));
    }
}

/// <summary>
/// D8: DeepL, the free Google endpoint and Google Cloud were removed. A profile
/// or route saved while they were still shipped has to degrade to a provider this
/// build can serve - it must never throw and never leave the translation page
/// without a usable service.
/// </summary>
public sealed class RemovedTranslationProviderTests
{
    private static IReadOnlyCollection<string> ShippedIds =>
        new TranslationProviderCatalog(TranslationProviderRegistry.CreateShippedProviders()).Ids;

    private static TranslationProfileRecord SavedProfile(
        string id,
        string provider,
        string endpoint = "https://saved.example.test/translate") =>
        new(id, id, provider, "saved-model", endpoint, "本地配置", "auto", "zh-CN", "", new Dictionary<string, string>());

    private static TranslationProfileRecord LocalTestProfile() =>
        new("local-test", "本地测试", "echo", "echo", "https://localhost/echo", "本地配置", "auto", "zh-CN", "", new Dictionary<string, string>());

    [Fact]
    public void Removed_providers_are_not_registered_and_every_spelling_is_retired()
    {
        var catalog = new TranslationProviderCatalog(TranslationProviderRegistry.CreateShippedProviders());

        foreach (var retired in TranslationProviderRegistry.RetiredProviderIds)
        {
            Assert.False(catalog.IsRegistered(retired), $"'{retired}' must not be registered any more.");
        }

        Assert.Equal(["deepl", "google-free", "google-cloud"], TranslationProviderRegistry.RetiredProviderIds);
        Assert.True(TranslationProviderRegistry.IsRetired("DeepL"));
        Assert.True(TranslationProviderRegistry.IsRetired("deep-l"));
        Assert.True(TranslationProviderRegistry.IsRetired("google_free"));
        Assert.True(TranslationProviderRegistry.IsRetired("google_cloud"));
        Assert.Null(TranslationProviderRegistry.TryResolveId("deepl"));
        Assert.Null(TranslationProviderRegistry.TryResolveId("google-free"));
        Assert.Null(TranslationProviderRegistry.TryResolveId("google-cloud"));
        Assert.Equal(TranslationProviderRegistry.DefaultProviderId, TranslationProviderRegistry.ResolveForRouting("deepl"));
    }

    [Fact]
    public void Profiles_of_removed_providers_are_dropped_and_the_rest_stay_intact()
    {
        var saved = new[]
        {
            SavedProfile("deepseek-1", "deepseek"),
            SavedProfile("deepl-1", "deepl"),
            SavedProfile("deepl-legacy-1", "deep-l"),
            SavedProfile("google-free-1", "google-free"),
            SavedProfile("google-cloud-1", "google-cloud"),
            SavedProfile("broken-1", "deepseek", endpoint: "not-a-url")
        };

        // Loading must not throw, and a profile of a removed provider must not be
        // offered as a service that cannot translate anything.
        var retained = TranslationProfileResolver.RetainServable(saved, ShippedIds);

        Assert.Equal(new[] { "deepseek-1" }, retained.Select(profile => profile.Id));
        Assert.Equal("https://saved.example.test/translate", retained[0].Endpoint);
    }

    [Fact]
    public void Route_falls_back_to_a_servable_profile_when_the_saved_one_was_removed()
    {
        var saved = new[]
        {
            LocalTestProfile(),
            SavedProfile("deepl-1", "deepl"),
            SavedProfile("deepseek-1", "deepseek")
        };

        var fallback = TranslationProfileResolver.SelectFallback(saved, ShippedIds);

        // The offline echo profile is a diagnostic; a configured service wins.
        Assert.NotNull(fallback);
        Assert.Equal("deepseek-1", fallback!.Id);
        Assert.Equal(TranslationProviderRegistry.DefaultProviderId, fallback.Provider);
    }

    [Fact]
    public void Route_falls_back_to_the_local_test_profile_when_nothing_else_is_left()
    {
        var fallback = TranslationProfileResolver.SelectFallback(
            [LocalTestProfile(), SavedProfile("deepl-1", "deepl")], ShippedIds);

        Assert.NotNull(fallback);
        Assert.Equal("local-test", fallback!.Id);

        // Nothing servable at all: the shell uses its built-in default profile.
        Assert.Null(TranslationProfileResolver.SelectFallback([SavedProfile("deepl-1", "deepl")], ShippedIds));
        Assert.Empty(TranslationProfileResolver.RetainServable(null, ShippedIds));
    }
}

public sealed class RoutedProviderTests
{
    [Fact]
    public async Task Tencent_provider_signs_request_and_reads_translation()
    {
        var handler = new CapturingResponseHandler("{\"Response\":{\"TargetText\":\"你好\"}}");
        using var client = new HttpClient(handler);
        var provider = new TencentTranslationProvider(client);

        var result = await provider.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en-US", "TextTranslate", new Uri("https://example.test"), "secret-id", "test", "tencent",
            "ap-guangzhou", new Dictionary<string, string> { ["secret"] = "secret-key" }));

        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("ap-guangzhou", handler.Request.Headers.GetValues("X-TC-Region").Single());
        Assert.Contains("Credential=secret-id/", handler.Request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("SourceText", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"Target\":\"zh\"", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aliyun_provider_signs_form_request_and_maps_professional_scene()
    {
        var handler = new CapturingResponseHandler("{\"Code\":\"200\",\"Data\":{\"Translated\":\"你好\"}}");
        using var client = new HttpClient(handler);
        var provider = new AliyunTranslationProvider(client);

        var result = await provider.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", "professional", new Uri("https://example.test"), "access-id", "test", "aliyun",
            "cn-shanghai", new Dictionary<string, string> { ["secret"] = "access-secret" }));

        Assert.Equal("你好", result.TranslatedText);
        Assert.Contains("AccessKeyId=access-id", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("RegionId=cn-shanghai", handler.RequestBody, StringComparison.Ordinal);
        // 专业版 = Action:Translate + 领域 Scene。曾经这里断言 Scene=professional，
        // 那是错的：实测该值会被服务端以 10004「参数出错」拒绝（见 TranslationConnectionTests）。
        Assert.Contains("Action=Translate&", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("Scene=social", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Scene=professional", handler.RequestBody, StringComparison.Ordinal);
        // TranslateGeneral 把 FormatType 列为必填，缺了就是 400 MissingFormatType。
        Assert.Contains("FormatType=text", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("Signature=", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatches_using_request_provider_id()
    {
        var echo = new EchoTranslationProvider();
        var catalog = new TranslationProviderCatalog([echo]);
        var routed = new RoutedTranslationProvider(catalog);
        var result = await routed.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", "echo", new Uri("https://localhost"), "local", "test", "echo"));

        Assert.Equal("hello", result.TranslatedText);
    }

    [Fact]
    public async Task Dispatches_legacy_openai_provider_alias_without_failing_route_lookup()
    {
        // Unknown aliases still fail, but the legacy OpenAI-compatible
        // spellings are migrated to the dedicated DeepSeek provider.
        var deepSeek = new DeepSeekTranslationProvider(
            new HttpClient(new StubHandler("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}")));
        var aliasCatalog = new TranslationProviderCatalog([deepSeek]);
        var aliasRouted = new RoutedTranslationProvider(aliasCatalog);

        var result = await aliasRouted.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", "demo", new Uri("https://example.test/v1"), "secret", "test", "openai_compatible"));

        Assert.Equal("你好", result.TranslatedText);
    }

    [Fact]
    public async Task Routed_provider_rejects_null_request_with_argument_exception()
    {
        var routed = new RoutedTranslationProvider(new TranslationProviderCatalog([new EchoTranslationProvider()]));

        await Assert.ThrowsAsync<ArgumentNullException>(() => routed.TranslateAsync(null!));
    }

    [Fact]
    public void Xiaomi_request_body_uses_the_mimo_model_and_disables_thinking_by_default()
    {
        Assert.Equal("mimo-v2.5", XiaomiTranslationProvider.DefaultModel);
        Assert.Equal("https://api.xiaomimimo.com/v1", XiaomiTranslationProvider.DefaultEndpoint);
        var request = new TranslationProviderRequest(
            "hello", "zh-CN", "en", XiaomiTranslationProvider.DefaultModel, new Uri("https://api.xiaomimimo.com/v1"), "secret", "test", "xiaomi");

        var serialized = JsonSerializer.Serialize(XiaomiTranslationProvider.BuildRequestBody(request));

        // MiMo accepts the same thinking switch as DeepSeek, so a one-sentence
        // translation must not pay for reasoning it does not use.
        Assert.Contains("\"model\":\"mimo-v2.5\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"disabled\"", serialized, StringComparison.Ordinal);
        Assert.Contains("temperature", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parses_xiaomi_response_without_network()
    {
        var provider = new XiaomiTranslationProvider(
            new HttpClient(new StubHandler("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}")));

        var result = await provider.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", XiaomiTranslationProvider.DefaultModel, new Uri("https://api.xiaomimimo.com/v1"), "secret", "test", "xiaomi"));

        Assert.Equal("你好", result.TranslatedText);
    }

    [Fact]
    public async Task Xiaomi_requires_a_key_before_calling_the_service()
    {
        var provider = new XiaomiTranslationProvider(
            new HttpClient(new StubHandler("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(
            new TranslationProviderRequest(
                "hello", "zh-CN", "en", XiaomiTranslationProvider.DefaultModel, new Uri("https://api.xiaomimimo.com/v1"), "本地配置", "test", "xiaomi")));

        Assert.Contains("小米", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xiaomi_chat_endpoint_is_built_from_the_platform_base_url()
    {
        // The saved preset is the platform base URL, so the adapter has to append
        // the chat path itself; /v1 must stay part of the address.
        var endpoint = ChatCompletionsTranslation.BuildChatEndpoint(new Uri("https://api.xiaomimimo.com/v1"));

        Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", endpoint.ToString());
    }

    [Fact]
    public void DeepSeek_request_body_disables_thinking_by_default()
    {
        Assert.Equal("deepseek-flash", DeepSeekTranslationProvider.DefaultModel);
        var request = new TranslationProviderRequest(
            "hello", "zh-CN", "en", DeepSeekTranslationProvider.DefaultModel, new Uri("https://api.deepseek.com"), "secret", "test", "deepseek");

        var serialized = JsonSerializer.Serialize(DeepSeekTranslationProvider.BuildRequestBody(request));

        Assert.Contains("\"type\":\"disabled\"", serialized, StringComparison.Ordinal);
        Assert.Contains("temperature", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeek_request_body_enables_thinking_only_for_reasoner_models()
    {
        var request = new TranslationProviderRequest(
            "hello", "zh-CN", "en", "deepseek-reasoner", new Uri("https://api.deepseek.com"), "secret", "test", "deepseek");

        var serialized = JsonSerializer.Serialize(DeepSeekTranslationProvider.BuildRequestBody(request));

        Assert.Contains("\"type\":\"enabled\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parses_deepseek_response_without_network()
    {
        var handler = new StubHandler("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");
        using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(client);
        var result = await provider.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", "deepseek-chat", new Uri("https://api.deepseek.com"), "secret", "test", "deepseek"));

        Assert.Equal("你好", result.TranslatedText);
        Assert.Contains("zh-CN", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("Translate", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("/chat/completions", handler.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatches_a_retired_provider_id_to_the_default_provider()
    {
        // A route saved while DeepL was still shipped must not fail the catalog
        // lookup after the removal: it degrades to the default provider.
        var handler = new StubHandler("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");
        using var client = new HttpClient(handler);
        var routed = new RoutedTranslationProvider(new TranslationProviderCatalog([
            new DeepSeekTranslationProvider(client)
        ]));

        var result = await routed.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", "deepseek-flash", new Uri("https://api.deepseek.com"), "secret", "test", "deepl"));

        Assert.Equal("你好", result.TranslatedText);
        Assert.Contains("/chat/completions", handler.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retired_spellings_of_removed_providers_resolve_to_the_default_provider()
    {
        var handler = new StubHandler("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}");
        using var client = new HttpClient(handler);
        var routed = new RoutedTranslationProvider(new TranslationProviderCatalog([
            new DeepSeekTranslationProvider(client)
        ]));

        foreach (var retired in new[] { "deep-l", "google_free", "google_cloud" })
        {
            var result = await routed.TranslateAsync(new TranslationProviderRequest(
                "hello", "zh-CN", "en", "deepseek-flash", new Uri("https://api.deepseek.com"), "secret", "test", retired));
            Assert.Equal("你好", result.TranslatedText);
        }
    }

    [Fact]
    public async Task An_unknown_provider_id_still_fails_the_lookup()
    {
        // Degrading must not swallow a typo: only the retired ids fall back.
        var routed = new RoutedTranslationProvider(new TranslationProviderCatalog([
            new EchoTranslationProvider()
        ]));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => routed.TranslateAsync(new TranslationProviderRequest(
            "hello", "zh-CN", "en", "demo", new Uri("https://localhost"), "local", "test", "not-a-provider")));
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturingResponseHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage Request { get; private set; } = null!;
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed class OscChatboxClientTests
{
    [Fact]
    public async Task Sends_chatbox_packet_to_configured_endpoint()
    {
        using var receiver = new System.Net.Sockets.UdpClient(0);
        var port = ((System.Net.IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var client = new OscChatboxClient("127.0.0.1", port);

        await client.SendChatboxAsync("你好");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await receiver.ReceiveAsync(timeout.Token);
        var packet = System.Text.Encoding.UTF8.GetString(result.Buffer);

        Assert.Contains("/chatbox/input", packet, StringComparison.Ordinal);
        Assert.Contains(",sTF", packet, StringComparison.Ordinal);
        Assert.Contains("你好", packet, StringComparison.Ordinal);
    }
}

public sealed class LegacyConfigImporterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vrctranslate-legacy-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Imports_non_translation_features_and_route_fields()
    {
        await File.WriteAllTextAsync(_path, """
        {
          "translation": {"provider": "openai_compatible", "base_url": "https://example.test/v1", "model": "demo", "api_key": "secret", "source_language": "ja", "target_language": "zh-CN", "message_format": "bilingual"},
          "osc": {"host": "192.168.1.2", "port": 9010, "play_sound": false},
          "voice": {"target_process_name": "VRChat.exe", "overlay_opacity": 0.75, "font_size": 20},
          "self_voice": {"enabled": true, "toggle_hotkey": "Ctrl+F8"},
          "glossary": {"enabled": true, "builtin_enabled": false},
          "ui": {"language": "zh-CN", "quick_input_hotkey": "Ctrl+Alt+I"}
        }
        """);

        var result = await new LegacyConfigImporter().ImportAsync(_path);

        Assert.Equal(["translation", "osc", "voice", "self_voice", "glossary", "ui"], result.ImportedSections);
        Assert.Equal("openai_compatible", result.Settings.Translation.Profiles[0].Provider);
        Assert.Equal("bilingual", result.Settings.Translation.SelfRoute.MessageFormat);
        Assert.Equal(9010, result.Settings.Osc.Port);
        Assert.False(result.Settings.Osc.PlaySound);
        Assert.Equal(0.75, result.Settings.Voice.OverlayOpacity);
        Assert.True(result.Settings.SelfVoice.Enabled);
        Assert.False(result.Settings.Glossary.BuiltInEnabled);
    }

    [Fact]
    public async Task Missing_file_returns_explicit_status_and_defaults()
    {
        var missing = Path.Combine(Path.GetDirectoryName(_path)!, $"missing-{Guid.NewGuid():N}.json");

        var result = await new LegacyConfigImporter().ImportAsync(missing);

        Assert.Equal(LegacyConfigImportStatus.Missing, result.Status);
        Assert.Null(result.LegacyVersion);
        Assert.Empty(result.ImportedSections);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Corrupt_json_raises_a_diagnostic_import_exception()
    {
        await File.WriteAllTextAsync(_path, "{ \"version\": 13, ");

        var exception = await Assert.ThrowsAsync<LegacyConfigImportException>(
            () => new LegacyConfigImporter().ImportAsync(_path));

        Assert.Equal(Path.GetFullPath(_path), exception.SourcePath);
        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("14")]
    [InlineData("\"13\"")]
    public async Task Unsupported_version_is_rejected(string version)
    {
        await File.WriteAllTextAsync(_path, $"{{\"version\":{version}}}");

        await Assert.ThrowsAsync<LegacyConfigImportException>(
            () => new LegacyConfigImporter().ImportAsync(_path));
    }

    [Fact]
    public async Task Missing_version_is_imported_with_warning()
    {
        await File.WriteAllTextAsync(_path, "{\"translation\":{\"provider\":\"test\"}}");

        var result = await new LegacyConfigImporter().ImportAsync(_path);

        Assert.Null(result.LegacyVersion);
        Assert.Contains(result.Warnings, warning => warning.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Provider_specific_credentials_are_sent_to_store_and_only_reference_is_mapped()
    {
        var store = new CapturingCredentialStore();
        await File.WriteAllTextAsync(_path, """
        {
          "version": 13,
          "translation": {
            "profiles": [
              {
                "id": "tencent-main",
                "provider": "tencent",
                "model": "general",
                "base_url": "https://example.test",
                "secret_id": "secret-id",
                "secret_key": "secret-key"
              }
            ]
          }
        }
        """);

        var result = await new LegacyConfigImporter(store).ImportAsync(_path);
        var profile = Assert.Single(result.Settings.Translation.Profiles);
        var credential = Assert.Single(store.Credentials);

        Assert.Equal("vault:tencent-main", profile.CredentialReference);
        Assert.Equal("vault:tencent-main", Assert.Single(result.CredentialReferences));
        Assert.Equal("tencent", credential.Provider);
        Assert.Equal("secret-id", credential.Values["secret_id"]);
        Assert.Equal("secret-key", credential.Values["secret_key"]);
    }

    [Fact]
    public async Task Voice_profiles_keep_selected_model_and_provider_credentials()
    {
        var store = new CapturingCredentialStore();
        await File.WriteAllTextAsync(_path, """
        {
          "version": 13,
          "voice": {
            "target_process_name": "VRChat.exe",
            "asr_profile_id": "aliyun-asr",
            "asr_profiles": [
              {
                "id": "tencent-asr",
                "name": "腾讯实时",
                "provider": "tencent_realtime",
                "model": "16k_zh_en",
                "options": {"app_id": "app-1", "secret_id": "sid-1"}
              },
              {
                "id": "aliyun-asr",
                "name": "阿里实时",
                "provider": "aliyun_nls_realtime",
                "model": "nls-realtime",
                "options": {"app_key": "ak-1", "access_key_id": "id-1", "access_key_secret": "secret-1"}
              }
            ],
            "segment": {"energy_threshold": 220, "silence_ms": 500, "minimum_speech_ms": 180},
            "overlay": {"show_original": false, "display_mode": "translation", "opacity": 0.7}
          },
          "ui": {"voice_hotkey": "F7"}
        }
        """);

        var result = await new LegacyConfigImporter(store).ImportAsync(_path);

        Assert.Equal("aliyun-asr", result.Settings.Voice.AsrProfileId);
        Assert.Equal(2, result.Settings.Voice.AsrProfiles.Count);
        var tencent = result.Settings.Voice.AsrProfiles[0];
        Assert.Equal("16k_zh_en", tencent.Model);
        Assert.Equal("vault:tencent-asr", tencent.CredentialReference);
        Assert.DoesNotContain("secret_id", tencent.Options.Keys);
        Assert.Equal("app-1", store.Credentials.Single(c => c.ProfileId == "tencent-asr").Values["app_id"]);
        var aliyun = result.Settings.Voice.AsrProfiles[1];
        Assert.Equal("vault:aliyun-asr", aliyun.CredentialReference);
        Assert.Equal("secret-1", store.Credentials.Single(c => c.ProfileId == "aliyun-asr").Values["access_key_secret"]);
        Assert.Equal(220, result.Settings.Voice.EnergyThreshold);
        Assert.Equal(500, result.Settings.Voice.SilenceMilliseconds);
        Assert.False(result.Settings.Voice.ShowOriginal);
        Assert.Equal("translation", result.Settings.Voice.OverlayDisplayMode);
        Assert.Equal("F7", result.Settings.System.VoiceHotkey);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class CapturingCredentialStore : ILegacyCredentialStore
    {
        public List<LegacyCredential> Credentials { get; } = [];

        public string Store(LegacyCredential credential)
        {
            Credentials.Add(credential);
            return $"vault:{credential.ProfileId}";
        }
    }
}
