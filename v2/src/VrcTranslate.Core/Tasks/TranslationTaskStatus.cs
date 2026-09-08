namespace VrcTranslate.Core.Tasks;

public enum TranslationTaskKind
{
    TextTranslation,
    SpeechTranslation
}

public enum TranslationTaskStatus
{
    Idle,
    Starting,
    Running,
    Pausing,
    Paused,
    Stopping,
    Completed,
    Failed,
    Cancelled
}

public sealed record TranslationTaskSnapshot
{
    public TranslationTaskSnapshot(
        string taskId,
        TranslationTaskKind kind,
        TranslationTaskStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        int processedItems = 0,
        string? lastError = null)
    {
        TaskId = string.IsNullOrWhiteSpace(taskId) ? throw new ArgumentException("Task ID is required.", nameof(taskId)) : taskId.Trim();
        if (updatedAt < createdAt)
        {
            throw new ArgumentException("UpdatedAt cannot precede CreatedAt.", nameof(updatedAt));
        }

        if (processedItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processedItems));
        }

        TaskKind = kind;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ProcessedItems = processedItems;
        LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError.Trim();
    }

    public string TaskId { get; }
    public TranslationTaskKind TaskKind { get; }
    public TranslationTaskStatus Status { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public int ProcessedItems { get; }
    public string? LastError { get; }
}
