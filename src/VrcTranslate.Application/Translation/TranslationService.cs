using System.Diagnostics;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Application.Translation;

public sealed class TranslationInvariantException : InvalidOperationException
{
    public TranslationInvariantException(IReadOnlyList<InvariantViolation> violations)
        : base("The translation changed protected values.")
    {
        Violations = violations ?? throw new ArgumentNullException(nameof(violations));
    }

    public IReadOnlyList<InvariantViolation> Violations { get; }
}

/// <summary>Translates manual text and final speech text through the same route.</summary>
public sealed class TranslationService
{
    private readonly ITranslationProvider _provider;
    private readonly IInvariantProtectionGuard _invariantGuard;

    public TranslationService(ITranslationProvider provider, IInvariantProtectionGuard invariantGuard)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _invariantGuard = invariantGuard ?? throw new ArgumentNullException(nameof(invariantGuard));
    }

    public async Task<TextTranslationResult> TranslateAsync(
        TextTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var protectedText = _invariantGuard.Protect(request.Text, request.Route.InvariantPolicy);
        var resolvedSourceLanguage = request.Route.LanguagePolicy.ResolveSourceLanguage(request.SourceLanguageHint);
        var providerRequest = new TranslationProviderRequest(
            protectedText.Text,
            request.Route.LanguagePolicy.TargetLanguage,
            resolvedSourceLanguage,
            request.Route.Profile.Model,
            request.Route.Profile.Endpoint,
            request.Route.Profile.CredentialReference,
            request.CorrelationId,
            request.Route.Profile.Provider,
            request.Route.Profile.Region,
            request.Route.Profile.Options);

        TranslationProviderResponse? response = null;
        Exception? lastError = null;
        for (var attempt = 0; attempt <= request.Route.RetryPolicy.MaxRetries; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Route.RetryPolicy.RequestTimeout);
            try
            {
                response = await _provider.TranslateAsync(providerRequest, timeout.Token).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("The translation provider exceeded the configured timeout.");
            }
            catch (Exception exception) when (attempt < request.Route.RetryPolicy.MaxRetries)
            {
                lastError = exception;
            }
        }

        if (response is null)
        {
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            throw lastError ?? new TimeoutException("The translation provider did not return a response.");
        }

        stopwatch.Stop();
        var validation = _invariantGuard.Validate(protectedText, response.TranslatedText);
        if (!validation.IsValid)
        {
            throw new TranslationInvariantException(validation.Violations);
        }

        return new TextTranslationResult(
            request,
            response.TranslatedText,
            resolvedSourceLanguage ?? response.DetectedSourceLanguage,
            stopwatch.Elapsed,
            validation);
    }

    /// <summary>
    /// Translates one source sentence to each configured output language. Each
    /// request keeps the same provider and source language while changing only
    /// the target language, so a second output never changes the first result.
    /// </summary>
    public async Task<TextTranslationBatchResult> TranslateManyAsync(
        TextTranslationRequest request,
        TranslationTargetSet targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targets);

        // 一次识别/输入里配置了几个目标语言，就同时向提供商发几个请求。串行时用户要等
        // "第一段译文 + 第二段译文"两段时间，并行只等最慢的那一段；两段请求彼此独立，
        // 各自仍走本目标的重试与超时策略。
        var pending = new Task<TextTranslationResult>[targets.Languages.Count];
        for (var index = 0; index < targets.Languages.Count; index++)
        {
            pending[index] = TranslateAsync(
                WithTargetLanguage(request, targets.Languages[index]),
                cancellationToken);
        }

        TextTranslationResult[] results;
        try
        {
            results = await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && pending.Length > 1)
        {
            // 主目标失败：整条消息就没有可用的译文，跟串行时的行为一致。
            if (!pending[0].IsCompletedSuccessfully)
            {
                throw;
            }

            // 只有副目标失败：保留已经拿到的主目标译文，把这一条按"只配了一个目标"处理，
            // 用户仍然看到并发出译文（"译文 / 原文"），而不是整句白等。
            var degradedTargets = new TranslationTargetSet(targets.PrimaryLanguage);
            return new TextTranslationBatchResult(request, degradedTargets, [pending[0].Result]);
        }

        return new TextTranslationBatchResult(request, targets, results);
    }

    /// <summary>
    /// Rebinds one batch request to a single output language. Only the route's
    /// language policy changes, so every other part of the request - text,
    /// correlation id, source hint - stays identical for all targets.
    /// </summary>
    private static TextTranslationRequest WithTargetLanguage(TextTranslationRequest request, string targetLanguage) =>
        new(
            request.Text,
            request.Route.ForTargetLanguage(targetLanguage),
            request.Source,
            request.RequestedAt,
            request.SourceLanguageHint,
            request.CorrelationId);
}
