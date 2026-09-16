using System.Threading.Channels;
using VrcTranslate.Application.Translation;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Application.Captions;

/// <summary>Chatbox port: the pipeline must not learn the concrete OSC client.</summary>
public interface IChatboxOutput
{
    Task SendChatboxAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>The caption surface other-player sentences appear on.</summary>
public interface IOtherPlayerSubtitleSurface
{
    /// <summary>Shows one recognized sentence before its translation exists.</summary>
    void PublishPending(long captionId, string text, string? speakerLabel);

    /// <summary>
    /// Completes one message with its translation, or with null when that
    /// translation will never arrive - both end the surface's waiting state.
    /// </summary>
    void FillTranslation(long captionId, string? translatedText);
}

/// <summary>Tuning of <see cref="OtherPlayerCaptionPipeline"/>; defaults match production.</summary>
public sealed record OtherPlayerCaptionPipelineOptions
{
    /// <summary>Translations in flight at once; commits stay ordered regardless of this.</summary>
    public int MaxConcurrentTranslations { get; init; } = 2;

    /// <summary>
    /// Accepted-but-unstarted sentences. A full queue gives that caption an explicit
    /// terminal state instead of dropping it silently.
    /// </summary>
    public int QueueCapacity { get; init; } = 64;

    public static OtherPlayerCaptionPipelineOptions Default { get; } = new();
}

/// <summary>One recognized other-player sentence plus the route snapshot taken for it.</summary>
public sealed record OtherPlayerCaptionInput(
    string Text,
    string? SpeakerLabel,
    string? SourceLanguageHint,
    TranslationRoute Route);

/// <summary>Why a caption finished the way it did; only Succeeded may reach the chatbox.</summary>
public enum OtherPlayerTranslationStatus
{
    Succeeded,
    TestEcho,
    Failed,
    Cancelled
}

public sealed record OtherPlayerCaptionDiagnostics(long CaptionId, string Message);

/// <summary>
/// Other-player caption pipeline (D28/D29). Recognition results are published to
/// the subtitle surface the moment they exist, then translated on an ordered
/// session queue: a slow or failed sentence never drops or delays the next one,
/// and results are applied strictly in caption order. The chatbox only ever sees
/// a real service translation that differs from the recognized text - echo
/// profiles, failures, cancellations and same-text replies stay local.
/// </summary>
public sealed class OtherPlayerCaptionPipeline : IAsyncDisposable
{
    private readonly TranslationService _translator;
    private readonly IOtherPlayerSubtitleSurface _subtitles;
    private readonly IChatboxOutput _chatbox;
    private readonly OtherPlayerCaptionPipelineOptions _options;
    private readonly object _lifecycle = new();
    private SessionState? _current;
    private long _captionSequence;
    private long _sessionGeneration;
    private bool _disposed;

    public OtherPlayerCaptionPipeline(
        TranslationService translator,
        IOtherPlayerSubtitleSurface subtitles,
        IChatboxOutput chatbox,
        OtherPlayerCaptionPipelineOptions? options = null)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _subtitles = subtitles ?? throw new ArgumentNullException(nameof(subtitles));
        _chatbox = chatbox ?? throw new ArgumentNullException(nameof(chatbox));
        _options = options ?? OtherPlayerCaptionPipelineOptions.Default;
        if (_options.MaxConcurrentTranslations < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.QueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <summary>Raised for queue overflows and apply-time faults; never per-sentence noise.</summary>
    public event EventHandler<OtherPlayerCaptionDiagnostics>? Diagnostics;

    /// <summary>
    /// Starts a fresh run: new generation, queue and workers. An unended previous
    /// run is drained first, so only one run is ever active.
    /// </summary>
    public async Task BeginSessionAsync()
    {
        SessionState? previous;
        var state = new SessionState(
            (int)Interlocked.Increment(ref _sessionGeneration),
            _options.QueueCapacity);
        lock (_lifecycle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OtherPlayerCaptionPipeline));
            previous = _current;
            _current = state;
        }

        if (previous is not null) await previous.EndAsync().ConfigureAwait(false);
        state.Start(RunWorkerAsync, RunCommitterAsync);
    }

    /// <summary>
    /// Publishes the sentence to the subtitle surface immediately and hands it to
    /// the session queue. Returns false only when no session is running; a full
    /// queue still publishes and then ends that caption locally.
    /// </summary>
    public bool TrySubmit(OtherPlayerCaptionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var state = Volatile.Read(ref _current);
        if (state is null || !state.IsActive) return false;

        var captionId = Interlocked.Increment(ref _captionSequence);
        _subtitles.PublishPending(captionId, input.Text, input.SpeakerLabel);
        state.MarkFirstCaption(captionId);
        if (!state.Work.Writer.TryWrite(new WorkItem(state.Generation, captionId, input)))
        {
            state.Record(new Commit(state.Generation, captionId, OtherPlayerTranslationStatus.Failed, null, "翻译队列已满。"));
            Diagnostics?.Invoke(this, new OtherPlayerCaptionDiagnostics(captionId, "他人语音翻译队列已满，本句只显示原文。"));
        }

        return true;
    }

    /// <summary>
    /// Ends the run: nothing new is accepted, in-flight translations are
    /// cancelled, and no caption of this run is filled or sent afterwards.
    /// </summary>
    public async Task EndSessionAsync()
    {
        SessionState? state;
        lock (_lifecycle)
        {
            state = _current;
            _current = null;
        }

        if (state is not null) await state.EndAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle) _disposed = true;
        await EndSessionAsync().ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(SessionState state)
    {
        var slots = new SemaphoreSlim(_options.MaxConcurrentTranslations);
        var inFlight = new List<Task>();
        try
        {
            while (true)
            {
                try
                {
                    if (!await state.Work.Reader.WaitToReadAsync(state.Lifetime.Token).ConfigureAwait(false)) break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await slots.WaitAsync(state.Lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!state.Work.Reader.TryRead(out var item))
                {
                    slots.Release();
                    continue;
                }

                inFlight.Add(TranslateAndRecordAsync(state, item, slots));
                inFlight.RemoveAll(done => done.IsCompleted);
            }
        }
        finally
        {
            // Items that were accepted but never started still owe their caption
            // a terminal state; cancelled in-flight work records itself.
            while (state.Work.Reader.TryRead(out var leftover))
            {
                state.Record(new Commit(state.Generation, leftover.CaptionId, OtherPlayerTranslationStatus.Cancelled, null, null));
            }

            try { await Task.WhenAll(inFlight.ToArray()).ConfigureAwait(false); }
            catch { /* every task records its own outcome */ }
            state.Commits.Writer.TryComplete();
            slots.Dispose();
        }
    }

    private async Task TranslateAndRecordAsync(SessionState state, WorkItem item, SemaphoreSlim slots)
    {
        Commit commit;
        try
        {
            if (string.Equals(item.Input.Route.Profile.Provider, "echo", StringComparison.OrdinalIgnoreCase))
            {
                // The echo profile returns its input by design; treating that as a
                // translation is how recognized text leaked into the chatbox (D28).
                commit = new Commit(state.Generation, item.CaptionId, OtherPlayerTranslationStatus.TestEcho, null, null);
            }
            else
            {
                var result = await _translator.TranslateAsync(
                    new TextTranslationRequest(
                        item.Input.Text,
                        item.Input.Route,
                        TextTranslationSource.SpeechRecognition,
                        sourceLanguageHint: item.Input.SourceLanguageHint),
                    state.Lifetime.Token).ConfigureAwait(false);
                commit = new Commit(state.Generation, item.CaptionId, OtherPlayerTranslationStatus.Succeeded, result, null);
            }
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested)
        {
            commit = new Commit(state.Generation, item.CaptionId, OtherPlayerTranslationStatus.Cancelled, null, null);
        }
        catch (Exception exception)
        {
            commit = new Commit(state.Generation, item.CaptionId, OtherPlayerTranslationStatus.Failed, null, exception.Message);
        }
        finally
        {
            slots.Release();
        }

        state.Record(commit);
    }

    private async Task RunCommitterAsync(SessionState state)
    {
        await foreach (var commit in state.Commits.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!state.IsActive) return;
            try
            {
                await ApplyAsync(commit, state.Lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Diagnostics?.Invoke(this, new OtherPlayerCaptionDiagnostics(commit.CaptionId, exception.Message));
            }
        }
    }

    private async Task ApplyAsync(Commit commit, CancellationToken cancellationToken)
    {
        if (commit.Status == OtherPlayerTranslationStatus.Succeeded && commit.Result is { } result)
        {
            var translated = result.TranslatedText;
            if (string.IsNullOrWhiteSpace(translated) || SameAfterTrim(result.OriginalText, translated))
            {
                // A provider that hands back the recognized line produced one text,
                // not a translation: the caption keeps its original, the chatbox stays quiet.
                _subtitles.FillTranslation(commit.CaptionId, null);
                return;
            }

            _subtitles.FillTranslation(commit.CaptionId, translated);
            await _chatbox.SendChatboxAsync(
                TranslationOutputFormatter.TrimForOsc(translated), cancellationToken).ConfigureAwait(false);
            return;
        }

        _subtitles.FillTranslation(commit.CaptionId, null);
    }

    /// <summary>Equality ignoring surrounding whitespace only; same text is not a translation.</summary>
    private static bool SameAfterTrim(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private readonly record struct WorkItem(int Generation, long CaptionId, OtherPlayerCaptionInput Input);

    private sealed record Commit(
        int Generation,
        long CaptionId,
        OtherPlayerTranslationStatus Status,
        TextTranslationResult? Result,
        string? FailureMessage);

    /// <summary>
    /// One run's queue, workers and commit ledger. Committing reads ids out of the
    /// ledger strictly in caption order, so finishing out of order cannot reorder
    /// the surface or the chatbox.
    /// </summary>
    private sealed class SessionState
    {
        private readonly object _gate = new();

        public SessionState(int generation, int queueCapacity)
        {
            Generation = generation;
            Lifetime = new CancellationTokenSource();
            Work = Channel.CreateBounded<WorkItem>(
                new BoundedChannelOptions(queueCapacity) { FullMode = BoundedChannelFullMode.Wait });
            Commits = Channel.CreateUnbounded<Commit>();
            Ledger = [];
        }

        public int Generation { get; }

        public CancellationTokenSource Lifetime { get; }

        public Channel<WorkItem> Work { get; }

        public Channel<Commit> Commits { get; }

        public Dictionary<long, Commit> Ledger { get; }

        public Task Worker { get; private set; } = Task.CompletedTask;

        public Task Committer { get; private set; } = Task.CompletedTask;

        public bool IsActive { get; private set; } = true;

        private long? NextCommitId { get; set; }

        public void Start(Func<SessionState, Task> worker, Func<SessionState, Task> committer)
        {
            Worker = worker(this);
            Committer = committer(this);
        }

        /// <summary>
        /// The commit ledger's base: the first caption this run accepted. Outcomes
        /// can arrive in any order, but only this id - and what follows it - commits.
        /// </summary>
        public void MarkFirstCaption(long captionId)
        {
            lock (_gate)
            {
                if (IsActive) NextCommitId ??= captionId;
            }
        }

        /// <summary>Files one outcome; ids contiguous from this run's first id continue to the committer.</summary>
        public void Record(Commit commit)
        {
            List<Commit>? ready = null;
            lock (_gate)
            {
                if (!IsActive) return;
                Ledger[commit.CaptionId] = commit;
                while (NextCommitId is { } next && Ledger.Remove(next, out var item))
                {
                    (ready ??= []).Add(item);
                    NextCommitId = next + 1;
                }
            }

            if (ready is null) return;
            foreach (var item in ready) _ = Commits.Writer.TryWrite(item);
        }

        public async Task EndAsync()
        {
            lock (_gate) IsActive = false;
            Lifetime.Cancel();
            Work.Writer.TryComplete();
            try { await Task.WhenAll(Worker, Committer).ConfigureAwait(false); }
            catch { /* workers never throw out */ }
            Lifetime.Dispose();
        }
    }
}
