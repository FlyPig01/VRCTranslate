using System.Text.Json;
using VrcTranslate.Infrastructure.Storage;

namespace VrcTranslate.Desktop;

/// <summary>
/// 作者小店（闲鱼等）的入口。它是**用户自己的**配置项，不是软件内置的推广：
/// 值写在 <c>v2-user-settings.json</c> 的 <c>ShopUrl</c>，空着时引导页整张卡片折叠。
/// 这样作者换链接、临时下架都不用重新编译或发版，用户想关掉也只需清掉这个字段。
/// </summary>
internal static class AuthorShop
{
    /// <summary>属性名，位于 <c>v2-user-settings.json</c>。</summary>
    public const string UrlPropertyName = "ShopUrl";

    /// <summary>链接下面那句说明，写死在这里以免界面文案散落。</summary>
    public const string Description =
        "软件本身免费开源、功能不收费。作者在闲鱼出售 VRChat 模型、工程文件与素材，" +
        "感兴趣可以看看；不看也不影响使用。";

    /// <summary>
    /// 读到的链接；没有配置、字段为空、文件损坏或不是 http(s) 一律返回空字符串，
    /// 让卡片保持折叠，而不是把一个坏链接摆到界面上。
    /// </summary>
    public static string ReadUrl()
    {
        try
        {
            var path = PortableStorage.GetPath(AppDataFiles.UserSettings);
            if (!File.Exists(path)) return string.Empty;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(UrlPropertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            var url = value.GetString()?.Trim() ?? string.Empty;
            return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                   (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? url
                : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}
