using VrcTranslate.Core.Speech;
using Xunit;

namespace VrcTranslate.Core.Tests;

public sealed class SpeakerRegistryTests
{
    /// <summary>Unit vector at the given cosine distance from <see cref="Vector(0)"/>.</summary>
    private static float[] AtAngle(float cosine) =>
        [cosine, MathF.Sqrt(Math.Max(0f, 1f - (cosine * cosine)))];

    private static float[] Vector(float x, float y) => [x, y];

    [Fact]
    public void First_voice_registers_speaker_a()
    {
        var registry = new SpeakerRegistry();

        var match = registry.Match(AtAngle(1f));

        Assert.Equal(SpeakerMatchKind.New, match.Kind);
        Assert.Equal("A", match.Speaker.Label);
        Assert.Equal("A", match.Speaker.DisplayName);
        Assert.Null(match.Speaker.Name);
    }

    [Fact]
    public void A_similar_voice_reuses_the_session_speaker()
    {
        var registry = new SpeakerRegistry();
        var first = registry.Match(AtAngle(1f));

        var second = registry.Match(AtAngle(0.80f));

        Assert.Equal(SpeakerMatchKind.Session, second.Kind);
        Assert.Equal(first.Speaker.Id, second.Speaker.Id);
        Assert.Single(registry.Speakers);
    }

    [Fact]
    public void A_distant_voice_becomes_the_next_label()
    {
        var registry = new SpeakerRegistry();
        registry.Match(AtAngle(1f));

        var second = registry.Match(AtAngle(-0.5f));

        Assert.Equal(SpeakerMatchKind.New, second.Kind);
        Assert.Equal("B", second.Speaker.Label);
        Assert.Equal(2, registry.Speakers.Count);
    }

    [Fact]
    public void An_empty_embedding_is_rejected()
    {
        var registry = new SpeakerRegistry();

        Assert.Throws<ArgumentException>(() => registry.Match([]));
        Assert.Throws<ArgumentException>(() => registry.Match(Vector(0f, 0f)));
    }

    [Fact]
    public void Remembered_speakers_need_the_stricter_threshold()
    {
        var registry = new SpeakerRegistry(new SpeakerMatchOptions
        {
            SessionThreshold = 0.50f,
            RememberedThreshold = 0.65f,
        });
        registry.Load(Document("小明", AtAngle(1f), 2));

        // 0.60 clears the session bar but not the remembered one, so the library
        // must not claim this is 小明.
        var cautious = registry.Match(AtAngle(0.60f));

        Assert.Equal(SpeakerMatchKind.New, cautious.Kind);
        Assert.Null(cautious.Speaker.Name);
    }

    [Fact]
    public void A_clear_remembered_match_reuses_the_name()
    {
        var registry = new SpeakerRegistry();
        registry.Load(Document("小明", AtAngle(1f), 2));

        var match = registry.Match(AtAngle(0.80f));

        Assert.Equal(SpeakerMatchKind.Remembered, match.Kind);
        Assert.Equal("小明", match.Speaker.Name);
        Assert.Equal("小明", match.Speaker.DisplayName);
    }

    [Fact]
    public void Only_named_speakers_are_written_to_the_library()
    {
        var registry = new SpeakerRegistry();
        var first = registry.Match(AtAngle(1f));
        registry.Match(AtAngle(-0.5f));

        Assert.Empty(registry.Snapshot("model.onnx").Speakers);

        registry.Rename(first.Speaker.Id, "小明");

        var document = registry.Snapshot("model.onnx");
        var record = Assert.Single(document.Speakers);
        Assert.Equal("小明", record.Name);
        Assert.Equal(1, registry.RememberedCount);
    }

    [Fact]
    public void A_snapshot_restores_the_name_in_a_new_session()
    {
        var registry = new SpeakerRegistry();
        var match = registry.Match(AtAngle(1f));
        registry.Rename(match.Speaker.Id, "小明");
        var document = registry.Snapshot("model.onnx");

        var nextSession = new SpeakerRegistry();
        nextSession.Load(document);
        var restored = nextSession.Match(AtAngle(0.95f));

        Assert.Equal(SpeakerMatchKind.Remembered, restored.Kind);
        Assert.Equal("小明", restored.Speaker.DisplayName);
    }

    [Fact]
    public void Renaming_to_nothing_drops_the_speaker_from_the_library()
    {
        var registry = new SpeakerRegistry();
        var match = registry.Match(AtAngle(1f));
        registry.Rename(match.Speaker.Id, "小明");

        registry.Rename(match.Speaker.Id, "   ");

        Assert.Empty(registry.Snapshot("model.onnx").Speakers);
    }

    [Fact]
    public void Merging_folds_the_samples_into_the_target()
    {
        var registry = new SpeakerRegistry();
        var first = registry.Match(AtAngle(1f));
        var second = registry.Match(AtAngle(0.3f));
        Assert.Equal(SpeakerMatchKind.New, second.Kind);
        registry.Rename(second.Speaker.Id, "小明");

        Assert.True(registry.Merge(first.Speaker.Id, second.Speaker.Id));

        Assert.Single(registry.Speakers);
        Assert.Equal("小明", registry.Speakers[0].Name);
        // The merged voiceprint covers both voices, so the first one matches.
        var merged = registry.Match(AtAngle(0.9f));
        Assert.Equal(second.Speaker.Id, merged.Speaker.Id);
        // The target was created in this session, so it keeps the session threshold.
        Assert.Equal(SpeakerMatchKind.Session, merged.Kind);
    }

    [Fact]
    public void Merging_a_speaker_into_itself_is_refused()
    {
        var registry = new SpeakerRegistry();
        var match = registry.Match(AtAngle(1f));

        Assert.False(registry.Merge(match.Speaker.Id, match.Speaker.Id));
    }

    [Fact]
    public void Forget_and_clear_remove_speakers()
    {
        var registry = new SpeakerRegistry();
        var first = registry.Match(AtAngle(1f));
        var second = registry.Match(AtAngle(-1f));

        Assert.True(registry.Forget(first.Speaker.Id));
        Assert.False(registry.Forget(first.Speaker.Id));
        Assert.Single(registry.Speakers);

        registry.Clear();
        Assert.Empty(registry.Speakers);
        Assert.Equal("A", registry.Match(AtAngle(1f)).Speaker.Label);
        Assert.NotNull(second);
    }

    [Fact]
    public void A_library_from_another_embedding_model_is_ignored()
    {
        var registry = new SpeakerRegistry();
        registry.Load(Document("小明", [1f, 0f, 0f], 3));

        var match = registry.Match(AtAngle(1f));

        // A three-dimensional voiceprint cannot be compared with a two-dimensional
        // embedding, so this is a new speaker rather than a wrong name.
        Assert.Equal(SpeakerMatchKind.New, match.Kind);
        Assert.Equal("B", match.Speaker.Label);
    }

    [Fact]
    public void Exemplars_stay_bounded_and_only_keep_new_coverage()
    {
        var registry = new SpeakerRegistry(new SpeakerMatchOptions { MaxExemplars = 3 });
        var match = registry.Match(AtAngle(1f));
        registry.Rename(match.Speaker.Id, "小明");

        // Repeats of the same voice add nothing.
        for (var i = 0; i < 20; i++) registry.Match(AtAngle(1f));
        Assert.Empty(registry.Snapshot("model.onnx").Speakers[0].Exemplars);

        // Voices that widen the coverage are stored, up to the bound.
        foreach (var cosine in new[] { 0.85f, 0.75f, 0.65f, 0.55f, 0.5f }) registry.Match(AtAngle(cosine));
        var exemplars = registry.Snapshot("model.onnx").Speakers[0].Exemplars;
        Assert.InRange(exemplars.Length, 1, 3);
    }

    [Fact]
    public void Loading_replaces_the_previous_library()
    {
        var registry = new SpeakerRegistry();
        registry.Load(Document("小明", AtAngle(1f), 2));
        registry.Load(Document("小红", AtAngle(1f), 2));

        var match = registry.Match(AtAngle(1f));

        Assert.Equal("小红", match.Speaker.Name);
        Assert.Single(registry.Speakers);
    }

    private static SpeakerVoiceprintDocument Document(string name, float[] centroid, int dimension) => new(
        SpeakerVoiceprintDocument.CurrentVersion,
        "model.onnx",
        dimension,
        [new SpeakerVoiceprintRecord(
            "spk-imported",
            name,
            centroid,
            [],
            3,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)]);
}