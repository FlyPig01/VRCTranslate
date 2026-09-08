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

        var results = new List<TextTranslationResult>(targets.Languages.Count);
        foreach (var target in targets.Languages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetRequest = new TextTranslationRequest(
                request.Text,
                request.Route.ForTargetLanguage(target),
                request.Source,
                request.RequestedAt,
                request.SourceLanguageHint,
                request.CorrelationId);
            results.Add(await TranslateAsync(targetRequest, cancellationToken).ConfigureAwait(false));
        }

        return new TextTranslationBatchResult(request, targets, results);
    }
}
