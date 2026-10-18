namespace VrcTranslate.Core.Speech;

/// <summary>A speaker heard in this session, or restored from the voiceprint library.</summary>
public sealed record SpeakerIdentity(string Id, string Label, string? Name, int ObservationCount = 0)
{
    /// <summary>What captions show: the user-chosen name when there is one, otherwise the session label.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Label : Name;

    /// <summary>What the settings list shows; never the record's generated ToString.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? $"{Label} · 未命名" : $"{Label} · {Name}";
}

/// <summary>How a sentence was matched to a speaker.</summary>
public enum SpeakerMatchKind
{
    /// <summary>Someone already heard earlier in this session.</summary>
    Session,

    /// <summary>Someone restored from the voiceprint library, so the caption can show a name.</summary>
    Remembered,

    /// <summary>Nobody was close enough; a new session speaker was registered.</summary>
    New,

    /// <summary>The match was ambiguous; the caption keeps the previous speaker.</summary>
    Held,

    /// <summary>The match was too close to call and no previous speaker exists.</summary>
    Uncertain,

    /// <summary>The session speaker limit is full; the caption uses "未知" until a slot frees.</summary>
    Unknown,
}

public sealed record SpeakerMatch(
    SpeakerIdentity Speaker,
    float Similarity,
    SpeakerMatchKind Kind,
    float SecondSimilarity = -1f,
    float Margin = 1f,
    string? Reason = null);

/// <summary>
/// Matching policy. Session matches are looser because the acoustic conditions
/// have not changed, while a voiceprint restored from disk has to clear a
/// stricter bar: mistaking one player for another is worse than showing a new
/// "speaker B" that the user can name.
/// </summary>
public sealed record SpeakerMatchOptions
{
    public float SessionThreshold { get; init; } = 0.50f;

    public float RememberedThreshold { get; init; } = 0.65f;

    /// <summary>Voice samples kept per speaker, oldest evicted first.</summary>
    public int MaxExemplars { get; init; } = 8;

    /// <summary>
    /// A new sample is only stored when it is less similar than this to what the
    /// speaker already covers. Storing every observation would fill the budget
    /// with near-duplicates and lose the range a speaker actually has.
    /// </summary>
    public float ExemplarNoveltyThreshold { get; init; } = 0.90f;

    public float MaxAdaptationRate { get; init; } = 0.20f;

    /// <summary>Unnamed speakers kept in one session; named speakers use the library instead.</summary>
    public int MaxSessionSpeakers { get; init; } = 16;

    /// <summary>Named voiceprints kept on disk; reaching it never evicts a named speaker.</summary>
    public int MaxRememberedSpeakers { get; init; } = 100;

    /// <summary>How long an unnamed session speaker may go unheard before it frees its slot.</summary>
    public TimeSpan SessionSpeakerTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Required gap to the second-best speaker before a match is accepted.</summary>
    public float MatchMargin { get; init; } = 0.06f;

    /// <summary>Scores this close to the threshold are treated as ambiguous, not as a new speaker.</summary>
    public float GrayZone { get; init; } = 0.05f;

    /// <summary>Shorter spans are too noisy for a trustworthy voiceprint.</summary>
    public float MinimumEmbeddingSeconds { get; init; } = 1.5f;

    /// <summary>Quieter spans are skipped instead of polluting the profile.</summary>
    public float MinimumEmbeddingRms { get; init; } = 0.004f;
}

/// <summary>One persisted voiceprint. Only speakers the user named are stored.</summary>
public sealed record SpeakerVoiceprintRecord(
    string Id,
    string Name,
    float[] Centroid,
    float[][] Exemplars,
    int ObservationCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// The voiceprint library on disk. The embedding model is recorded so a library
/// built by a different model is recognised as incomparable instead of silently
/// producing nonsense matches.
/// </summary>
public sealed record SpeakerVoiceprintDocument(
    int Version,
    string? EmbeddingModel,
    int Dimension,
    IReadOnlyList<SpeakerVoiceprintRecord> Speakers)
{
    public const int CurrentVersion = 1;

    public static SpeakerVoiceprintDocument Empty { get; } = new(CurrentVersion, null, 0, []);
}

/// <summary>
/// Cosine matching over the speakers heard in this session plus the voiceprints
/// restored from disk. Pure state and arithmetic: the embedding model and the
/// file both live outside so this can be tested without a native runtime.
/// </summary>
public sealed class SpeakerRegistry
{
    /// <summary>Caption identity used while the 16 unnamed session slots are full.</summary>
    public static SpeakerIdentity UnknownSpeaker { get; } = new("spk-unknown", "?", "未知");

    private readonly SpeakerMatchOptions _options;
    private readonly List<SpeakerState> _speakers = [];
    private int _labelCounter;
    private int _idCounter;

    public SpeakerRegistry(SpeakerMatchOptions? options = null) => _options = options ?? new();

    public SpeakerMatchOptions Options => _options;

    /// <summary>Monotonic change counter so the UI can tell when its list is stale.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<SpeakerIdentity> Speakers
    {
        get
        {
            SweepExpiredSessionSpeakers();
            return _speakers.Select(speaker => speaker.Identity).ToArray();
        }
    }

    /// <summary>Named speakers only: those are the ones the library keeps.</summary>
    public int RememberedCount => _speakers.Count(speaker => speaker.IsRemembered);

    /// <summary>Unnamed speakers currently occupying the session slots.</summary>
    public int SessionCount => _speakers.Count(speaker => !speaker.IsRemembered);

    public SpeakerMatch Match(ReadOnlySpan<float> embedding, string? previousSpeakerId = null)
    {
        // 1.1.5-compatible path: every span matches independently. The extra
        // argument remains for source compatibility but must not turn a real
        // speaker change into a held previous speaker.
        _ = previousSpeakerId;
        var normalized = SpeakerMath.Normalize(embedding);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("说话人嵌入不能为空。", nameof(embedding));
        }

        SweepExpiredSessionSpeakers();
        SpeakerState? best = null;
        SpeakerState? second = null;
        var bestSimilarity = float.NegativeInfinity;
        var secondSimilarity = float.NegativeInfinity;
        foreach (var speaker in _speakers)
        {
            // A library written by another embedding model has a different
            // dimension; those records simply cannot be compared.
            if (speaker.Dimension != normalized.Length) continue;
            var similarity = speaker.Similarity(normalized);
            if (similarity > bestSimilarity)
            {
                secondSimilarity = bestSimilarity;
                second = best;
                bestSimilarity = similarity;
                best = speaker;
            }
            else if (similarity > secondSimilarity)
            {
                secondSimilarity = similarity;
                second = speaker;
            }
        }

        var margin = second is null ? 1f : bestSimilarity - secondSimilarity;
        if (best is not null && bestSimilarity >= ThresholdFor(best))
        {
            best.Observe(normalized, _options);
            return new SpeakerMatch(
                best.Identity,
                bestSimilarity,
                best.IsRemembered ? SpeakerMatchKind.Remembered : SpeakerMatchKind.Session,
                second is null ? -1f : secondSimilarity,
                margin,
                "match");
        }

        if (SessionCount >= _options.MaxSessionSpeakers)
        {
            return new SpeakerMatch(
                UnknownSpeaker,
                best is null ? 0f : bestSimilarity,
                SpeakerMatchKind.Unknown,
                second is null ? -1f : secondSimilarity,
                margin,
                "session-full");
        }

        var created = new SpeakerState(
            "spk-" + (++_idCounter).ToString("D4"),
            NextLabel(),
            normalized,
            isRemembered: false,
            name: null);
        _speakers.Add(created);
        Version++;
        return new SpeakerMatch(
            created.Identity,
            best is null ? 0f : bestSimilarity,
            SpeakerMatchKind.New,
            second is null ? -1f : secondSimilarity,
            margin,
            "new");
    }

    /// <summary>Removes the most recent observation from a speaker after a user correction.</summary>
    public bool RejectLastObservation(string speakerId)
    {
        var speaker = Find(speakerId);
        if (speaker is null || !speaker.RejectLastObservation()) return false;
        Version++;
        return true;
    }

    public bool Rename(string speakerId, string? name)
    {
        var speaker = Find(speakerId);
        if (speaker is null) return false;
        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmed is not null && !speaker.IsRemembered && RememberedCount >= _options.MaxRememberedSpeakers)
        {
            return false;
        }

        speaker.Name = trimmed;
        speaker.IsRemembered = trimmed is not null;
        Version++;
        return true;
    }

    /// <summary>Folds <paramref name="sourceId"/> into <paramref name="targetId"/> and drops the source.</summary>
    public bool Merge(string sourceId, string targetId)
    {
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal)) return false;
        var source = Find(sourceId);
        var target = Find(targetId);
        if (source is null || target is null) return false;
        if (source.Dimension != target.Dimension) return false;

        target.Absorb(source, _options);
        target.IsRemembered |= source.IsRemembered;
        _speakers.Remove(source);
        Version++;
        return true;
    }

    public bool Forget(string speakerId)
    {
        var speaker = Find(speakerId);
        if (speaker is null) return false;
        _speakers.Remove(speaker);
        Version++;
        return true;
    }

    public void Clear()
    {
        _speakers.Clear();
        _labelCounter = 0;
        Version++;
    }

    /// <summary>Named speakers, ready to be written to the library.</summary>
    public SpeakerVoiceprintDocument Snapshot(string? embeddingModel)
    {
        var records = _speakers
            .Where(speaker => speaker.Name is not null)
            .Select(speaker => speaker.ToRecord())
            .ToArray();
        var dimension = records.Length > 0 ? records[0].Centroid.Length : 0;
        return new SpeakerVoiceprintDocument(
            SpeakerVoiceprintDocument.CurrentVersion, embeddingModel, dimension, records);
    }

    /// <summary>Restores named speakers as remembered, replacing anything loaded before.</summary>
    public void Load(SpeakerVoiceprintDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _speakers.RemoveAll(speaker => speaker.IsRemembered);
        foreach (var record in document.Speakers)
        {
            if (string.IsNullOrWhiteSpace(record.Name)) continue;
            if (record.Centroid is null || record.Centroid.Length == 0) continue;
            if (RememberedCount >= _options.MaxRememberedSpeakers) break;
            var normalized = SpeakerMath.Normalize(record.Centroid);
            if (normalized.Length == 0) continue;
            var speaker = new SpeakerState(
                record.Id,
                NextLabel(),
                normalized,
                isRemembered: true,
                record.Name);
            speaker.Restore(record, _options);
            _speakers.Add(speaker);
        }

        Version++;
    }

    private void SweepExpiredSessionSpeakers()
    {
        if (_speakers.Count == 0) return;
        var cutoff = DateTimeOffset.UtcNow - _options.SessionSpeakerTtl;
        var removed = _speakers.RemoveAll(speaker =>
            !speaker.IsRemembered && speaker.LastSeenAt < cutoff);
        if (removed > 0) Version++;
    }

    private SpeakerState? Find(string speakerId) => _speakers.FirstOrDefault(speaker =>
        string.Equals(speaker.Id, speakerId, StringComparison.Ordinal));

    private float ThresholdFor(SpeakerState speaker) =>
        speaker.IsRemembered ? _options.RememberedThreshold : _options.SessionThreshold;

    /// <summary>Excel-style labels: A..Z, then AA, AB, and so on.</summary>
    private string NextLabel()
    {
        var index = _labelCounter++;
        var label = string.Empty;
        do
        {
            label = (char)('A' + (index % 26)) + label;
            index = (index / 26) - 1;
        }
        while (index >= 0);
        return label;
    }


    private sealed class SpeakerState
    {
        private readonly List<float[]> _exemplars = [];
        private float[]? _previousCentroid;
        private int _previousObservationCount;
        private int _previousExemplarCount;
        private DateTimeOffset _previousLastSeenAt;
        private bool _hasObservationSnapshot;

        public SpeakerState(string id, string label, float[] centroid, bool isRemembered, string? name)
        {
            Id = id;
            Label = label;
            Centroid = centroid;
            IsRemembered = isRemembered;
            Name = name;
            // Creating the first centroid is itself one observation; otherwise
            // the UI would report a named speaker as having zero samples.
            ObservationCount = 1;
            CreatedAt = DateTimeOffset.UtcNow;
            LastSeenAt = CreatedAt;
        }

        public string Id { get; }

        public string Label { get; }

        public string? Name { get; set; }

        public float[] Centroid { get; private set; }

        public bool IsRemembered { get; set; }

        public int ObservationCount { get; private set; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset LastSeenAt { get; private set; }

        public int Dimension => Centroid.Length;

        public SpeakerIdentity Identity => new(Id, Label, Name, ObservationCount);

        /// <summary>Best cosine similarity against the centroid or any stored sample.</summary>
        public float Similarity(ReadOnlySpan<float> normalized)
        {
            var best = SpeakerMath.CosineSimilarity(Centroid, normalized);
            foreach (var exemplar in _exemplars)
            {
                var similarity = SpeakerMath.CosineSimilarity(exemplar, normalized);
                if (similarity > best) best = similarity;
            }

            return best;
        }

        public void Observe(float[] normalized, SpeakerMatchOptions options)
        {
            _previousCentroid = Centroid;
            _previousObservationCount = ObservationCount;
            _previousExemplarCount = _exemplars.Count;
            _previousLastSeenAt = LastSeenAt;
            _hasObservationSnapshot = true;

            var coverage = Similarity(normalized);
            ObservationCount++;
            LastSeenAt = DateTimeOffset.UtcNow;

            var rate = Math.Min(options.MaxAdaptationRate, Math.Max(0.05f, 1f / ObservationCount));
            var updated = new float[Centroid.Length];
            for (var i = 0; i < updated.Length; i++)
            {
                updated[i] = (Centroid[i] * (1f - rate)) + (normalized[i] * rate);
            }

            Centroid = SpeakerMath.Normalize(updated);

            // Only samples that add coverage are worth keeping.
            if (coverage < options.ExemplarNoveltyThreshold)
            {
                if (_exemplars.Count >= options.MaxExemplars) _exemplars.RemoveAt(0);
                _exemplars.Add(normalized);
            }
        }

        /// <summary>Folds another speaker in: weighted centroid, combined samples.</summary>
        public void Absorb(SpeakerState other, SpeakerMatchOptions options)
        {
            var mine = Math.Max(1, ObservationCount);
            var theirs = Math.Max(1, other.ObservationCount);
            var merged = new float[Centroid.Length];
            var total = (float)(mine + theirs);
            for (var i = 0; i < merged.Length; i++)
            {
                merged[i] = ((Centroid[i] * mine) + (other.Centroid[i] * theirs)) / total;
            }

            // Merging two very different voices can cancel out; keeping the
            // target centroid in that case is better than an unusable zero vector.
            var normalized = SpeakerMath.Normalize(merged);
            if (normalized.Length > 0) Centroid = normalized;
            ObservationCount = mine + theirs;
            if (other.LastSeenAt > LastSeenAt) LastSeenAt = other.LastSeenAt;
            if (string.IsNullOrWhiteSpace(Name)) Name = other.Name;
            foreach (var exemplar in other._exemplars)
            {
                if (_exemplars.Count >= options.MaxExemplars) _exemplars.RemoveAt(0);
                _exemplars.Add(exemplar);
            }
        }

        public SpeakerVoiceprintRecord ToRecord() => new(
            Id,
            Name ?? string.Empty,
            Centroid,
            _exemplars.ToArray(),
            ObservationCount,
            CreatedAt,
            LastSeenAt);

        public bool RejectLastObservation()
        {
            if (!_hasObservationSnapshot || _previousCentroid is null) return false;
            Centroid = _previousCentroid;
            ObservationCount = _previousObservationCount;
            LastSeenAt = _previousLastSeenAt;
            while (_exemplars.Count > _previousExemplarCount) _exemplars.RemoveAt(_exemplars.Count - 1);
            _hasObservationSnapshot = false;
            return true;
        }

        public void Restore(SpeakerVoiceprintRecord record, SpeakerMatchOptions options)
        {
            ObservationCount = Math.Max(0, record.ObservationCount);
            LastSeenAt = record.LastSeenAt;
            _exemplars.Clear();
            foreach (var exemplar in record.Exemplars ?? [])
            {
                if (exemplar is null || exemplar.Length != Centroid.Length) continue;
                if (_exemplars.Count >= options.MaxExemplars) break;
                var normalized = SpeakerMath.Normalize(exemplar);
                if (normalized.Length == 0) continue;
                _exemplars.Add(normalized);
            }
        }
    }
}
