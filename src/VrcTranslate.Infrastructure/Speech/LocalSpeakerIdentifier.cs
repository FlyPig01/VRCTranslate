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

    public SpeakerMatch? Identify(ReadOnlyMemory<float> samples, int sampleRate)
    {
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

            return embedding.Length == 0 ? null : EnsureRegistry().Match(embedding);
        }
    }

    public bool IsSpeakerChangeSuspected(ReadOnlyMemory<float> samples, int sampleRate)
    {
        lock (_sync)
        {
            if (_disposed || EnsureEmbedder() is not { } embedder) return false;
            var window = (int)(sampleRate * _options.ChangeWindowSeconds);
            if (window <= 0 || samples.Length < window * 2) return false;

            var headOffset = FindVoicedWindow(samples, window, fromStart: true);
            if (headOffset < 0) return false;
            var tailOffset = FindVoicedWindow(samples, window, fromStart: false);
            if (tailOffset < 0) return false;

            // Overlapping windows share audio, which would inflate the similarity
            // and hide a real change; too short to tell apart means no suspicion.
            if (tailOffset - headOffset < window) return false;

            try
            {
                var head = embedder.Embed(samples.Slice(headOffset, window), sampleRate);
                if (head.Length == 0) return false;
                var tail = embedder.Embed(samples.Slice(tailOffset, window), sampleRate);
                if (tail.Length == 0) return false;
                return SpeakerMath.CosineSimilarity(head, tail) < _options.ChangeSimilarityThreshold;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate)
    {
        lock (_sync)
        {
            if (_disposed) return [];
            if (samples.Length < (int)(sampleRate * _options.MinimumSplitSeconds)) return [];
            if (EnsureSplitter() is not { } splitter) return [];

            try
            {
                var points = splitter.FindChangePoints(samples);
                return SpeakerSegmentSplitter.Split(
                    samples.Length,
                    points,
                    (int)(sampleRate * _options.MinimumPartSeconds),
                    _options.MaxParts);
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    public void Rename(string speakerId, string? name)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (EnsureRegistry().Rename(speakerId, name)) Persist();
        }
    }

    public bool Merge(string sourceId, string targetId)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            if (!EnsureRegistry().Merge(sourceId, targetId)) return false;
            Persist();
            return true;
        }
    }

    public bool Forget(string speakerId)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            if (!EnsureRegistry().Forget(speakerId)) return false;
            Persist();
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_disposed) return;
            EnsureRegistry().Clear();
            _store.Clear();
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

    /// <summary>
    /// Offset of the first comparison window that actually holds speech, scanning
    /// inward from one end. A segment keeps a silence tail because the segmenter
    /// waits out a pause before releasing a sentence, and the embedding of silence
    /// looks like a different person, so unvoiced windows must never be compared.
    /// </summary>
    private int FindVoicedWindow(ReadOnlyMemory<float> samples, int window, bool fromStart)
    {
        const int steps = 8;
        var step = Math.Max(1, window / 4);
        for (var index = 0; index < steps; index++)
        {
            var offset = index * step;
            if (offset + window > samples.Length) return -1;
            var start = fromStart ? offset : samples.Length - window - offset;
            if (start < 0) return -1;
            if (Rms(samples.Slice(start, window).Span) >= _options.SilenceRmsFloor) return start;
        }

        return -1;
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0f;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
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