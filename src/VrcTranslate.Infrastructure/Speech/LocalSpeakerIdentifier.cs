using VrcTranslate.Core.Speech;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Speaker separation for captions: the embedding model, the segmentation model,
/// the in-session registry and the voiceprint library behind one contract.
/// Every member degrades to "no speaker information" when the models are missing
/// or fail to load, so the plain caption path keeps working.
/// </summary>
public sealed class LocalSpeakerIdentifier : ISpeakerIdentifier, IDisposable
{
    private readonly LocalSpeechModelManager _models;
    private readonly SpeakerVoiceprintStore _store;
    private readonly SpeakerMatchOptions _matchOptions;
    private readonly SpeakerIdentifierOptions _options;
    private readonly object _sync = new();
    private SpeakerRegistry? _registry;
    private SpeakerEmbedder? _embedder;
    private SpeakerDiarizationSplitter? _splitter;
    private bool _embedderFailed;
    private bool _splitterFailed;
    private bool _disposed;

    public LocalSpeakerIdentifier(
        LocalSpeechModelManager speakerModels,
        SpeakerVoiceprintStore? store = null,
        SpeakerMatchOptions? matchOptions = null,
        SpeakerIdentifierOptions? options = null)
    {
        _models = speakerModels ?? throw new ArgumentNullException(nameof(speakerModels));
        _store = store ?? new SpeakerVoiceprintStore(LocalSpeechModelCatalog.EmbeddingFileName);
        _matchOptions = matchOptions ?? new SpeakerMatchOptions();
        _options = options ?? new SpeakerIdentifierOptions();
    }

    public bool IsAvailable => !_disposed && _models.GetStatus().State == LocalSpeechModelState.Ready;

    public event EventHandler? SpeakersChanged;

    /// <summary>Model that wrote a voiceprint library this build cannot use, otherwise null.</summary>
    public string? ReplacedLibraryModel => _store.IncompatibleModel;

    public IReadOnlyList<SpeakerIdentity> Speakers
    {
        get
        {
            lock (_sync)
            {
                return _disposed ? [] : EnsureRegistry().Speakers;
            }
        }
    }

    public SpeakerMatch? Identify(
        ReadOnlyMemory<float> samples,
        int sampleRate,
        string? previousSpeakerId = null,
        bool allowCreate = true)
    {
        _ = previousSpeakerId;

        SpeakerMatch? match;
        var changed = false;
        lock (_sync)
        {
            if (_disposed || EnsureEmbedder() is not { } embedder) return null;
            float[] embedding;
            try
            {
                embedding = embedder.Embed(samples, sampleRate);
            }
            catch (Exception)
            {
                return null;
            }

            if (embedding.Length == 0) return null;
            var registry = EnsureRegistry();
            var before = registry.Version;
            match = registry.Match(embedding, allowCreate: allowCreate);
            changed = registry.Version != before;
        }

        if (changed) RaiseSpeakersChanged();
        return match;
    }

    public bool Reject(string speakerId)
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed) return false;
            if (!EnsureRegistry().RejectLastObservation(speakerId)) return false;
            Persist();
            changed = true;
        }

        if (changed) RaiseSpeakersChanged();
        return true;
    }

    /// <summary>
    /// Splits a segment at the speaker changes the segmentation model reports.
    /// <para>
    /// No cheap pre-check runs in front of this on purpose: the head/tail
    /// voiceprint comparison that used to gate it disagreed with the model on 11 of
    /// 29 real dialogue segments (38% of real speaker changes were never split) and
    /// saved nothing - the segments it rejected finished segmentation in 33~233 ms.
    /// Measured 2026-09-16, see docs/能力边界与决策记录.md.
    /// </para>
    /// </summary>
    public IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate)
    {
        lock (_sync)
        {
            if (_disposed) return [];
            if (samples.Length < (int)(sampleRate * _options.MinimumSplitSeconds)) return [];
            if (EnsureSplitter() is not { } splitter) return [];

            try
            {
                var split = splitter.Find(samples);
                var spans = SpeakerSegmentSplitter.Split(
                    samples.Length,
                    split.ChangePoints,
                    (int)(sampleRate * _options.MinimumPartSeconds),
                    _options.MaxParts);
                return spans
                    .Select(span => span with
                    {
                        LocalSpeakerId = LocalSpeakerFor(split.Segments, span),
                    })
                    .ToArray();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    public bool Rename(string speakerId, string? name)
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed) return false;
            if (EnsureRegistry().Rename(speakerId, name))
            {
                Persist();
                changed = true;
            }
        }

        if (changed) RaiseSpeakersChanged();
        return changed;
    }

    public bool Merge(string sourceId, string targetId)
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed) return false;
            if (!EnsureRegistry().Merge(sourceId, targetId)) return false;
            Persist();
            changed = true;
        }

        if (changed) RaiseSpeakersChanged();
        return true;
    }

    public bool Forget(string speakerId)
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed) return false;
            if (!EnsureRegistry().Forget(speakerId)) return false;
            Persist();
            changed = true;
        }

        if (changed) RaiseSpeakersChanged();
        return true;
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_disposed) return;
            EnsureRegistry().Clear();
            _store.Clear();
        }

        RaiseSpeakersChanged();
    }

    private void RaiseSpeakersChanged()
    {
        try { SpeakersChanged?.Invoke(this, EventArgs.Empty); }
        catch
        {
            // A settings-page listener must never break recognition.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _embedder?.Dispose();
            _embedder = null;
            _splitter?.Dispose();
            _splitter = null;
        }
    }


    private void Persist() => _store.Save(
        _registry!.Snapshot(LocalSpeechModelCatalog.EmbeddingFileName));

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private SpeakerRegistry EnsureRegistry()
    {
        if (_registry is not null) return _registry;
        var registry = new SpeakerRegistry(_matchOptions);
        try
        {
            registry.Load(_store.Load());
        }
        catch (Exception)
        {
            // An unreadable library must not stop the session from labelling speakers.
        }

        _registry = registry;
        return registry;
    }

    private static int LocalSpeakerFor(
        IReadOnlyList<DiarizationSegment> segments,
        SpeechSpan span)
    {
        var bestOverlap = 0;
        var localSpeaker = -1;
        foreach (var segment in segments)
        {
            var start = Math.Max(span.StartSample, segment.StartSample);
            var end = Math.Min(span.EndSample, segment.EndSample);
            var overlap = end - start;
            if (overlap <= bestOverlap) continue;
            bestOverlap = overlap;
            localSpeaker = segment.LocalSpeakerId;
        }

        return localSpeaker;
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private SpeakerEmbedder? EnsureEmbedder()
    {
        if (_embedder is not null) return _embedder;
        if (_embedderFailed) return null;
        var status = _models.GetStatus();
        if (status.State != LocalSpeechModelState.Ready) return null;

        try
        {
            _embedder = new SpeakerEmbedder(
                Path.Combine(status.FilePath, LocalSpeechModelCatalog.EmbeddingFileName));
            return _embedder;
        }
        catch (Exception)
        {
            // Loading failed: retrying for every sentence would only add latency.
            _embedderFailed = true;
            return null;
        }
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private SpeakerDiarizationSplitter? EnsureSplitter()
    {
        if (_splitter is not null) return _splitter;
        if (_splitterFailed) return null;
        var status = _models.GetStatus();
        if (status.State != LocalSpeechModelState.Ready) return null;

        try
        {
            _splitter = new SpeakerDiarizationSplitter(
                Path.Combine(status.FilePath, LocalSpeechModelCatalog.SegmentationFileName),
                Path.Combine(status.FilePath, LocalSpeechModelCatalog.EmbeddingFileName));
            return _splitter;
        }
        catch (Exception)
        {
            _splitterFailed = true;
            return null;
        }
    }
}