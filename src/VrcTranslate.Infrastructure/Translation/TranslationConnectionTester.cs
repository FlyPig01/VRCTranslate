using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>One connection-test outcome for the profile dialog.</summary>
public sealed record TranslationConnectionTestResult(
    bool Succeeded,
    string? Translation,
    string? Hint,
    string Detail);

/// <summary>
/// Sends one short test sentence through a provider and turns failures into
/// actionable Chinese hints. The test is strictly opt-in: saving a profile
/// never depends on its outcome.
/// </summary>
public static class TranslationConnectionTester
{
    public static async Task<TranslationConnectionTestResult> TestAsync(
        ITranslationProvider provider,
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await provider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            return new TranslationConnectionTestResult(true, response.TranslatedText, null, "连接正常，翻译服务可用。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            TranslationErrorHints.TryDescribe(request.ProviderId, exception, out var hint);
            return new TranslationConnectionTestResult(false, null, hint, exception.Message);
        }
    }
}

/// <summary>
/// Maps provider error signatures to a hint the user can act on. Signatures
/// are matched loosely (substring, case-insensitive) because each provider
/// embeds them into its exception message differently.
/// </summary>
public static class TranslationErrorHints
{
    public static bool TryDescribe(string providerId, Exception exception, out string hint)
    {
        ArgumentNullException.ThrowIfNull(exception);
        hint = Describe(providerId, exception);
        return hint.Length > 0;
    }

    private static string Describe(string providerId, Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        switch (providerId)
        {
            case "tencent":
                if (Contains(message, "AuthFailure.SignatureFailure")) return "SecretId 与 SecretKey 不匹配，请重新复制（注意不要带空格）。";
                if (Contains(message, "AuthFailure.SecretIdNotFound")) return "SecretId 不存在或已删除，请到访问管理重新生成。";
                if (Contains(message, "FailedOperation.NoFreeAmount") || Contains(message, "LimitExceeded")) return "免费额度已用完或触发限流，请稍后重试或充值。";
                if (Contains(message, "FailedOperation.UserNotRegistered")) return "该账号还没有开通「机器翻译 TMT」服务。";
                break;
            case "aliyun":
                if (Contains(message, "InvalidAccessKeyId.NotFound")) return "AccessKey ID 不存在，请到 RAM 访问控制重新创建。";
                if (Contains(message, "SignatureDoesNotMatch")) return "AccessKey Secret 不匹配，请重新复制。";
                if (Contains(message, "Throttling")) return "触发限流，请稍后重试。";
                if (Contains(message, "Forbidden") || Contains(message, "InvalidAction")) return "子账号未授权「机器翻译」或该版本不支持。";
                break;
            case "deepseek":
            case "xiaomi":
                if (Contains(message, "返回 401")) return "API Key 无效或已被撤销。";
                if (Contains(message, "返回 402")) return "账户余额不足，请充值。";
                if (Contains(message, "返回 429")) return "触发限流，请稍后重试。";
                break;
        }

        if (exception is HttpRequestException or TaskCanceledException)
        {
            return "网络不通或服务不可达（不影响保存）。";
        }
        return string.Empty;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
