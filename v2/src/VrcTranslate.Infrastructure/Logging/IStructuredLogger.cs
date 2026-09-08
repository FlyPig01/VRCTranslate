namespace VrcTranslate.Infrastructure.Logging;

public interface IStructuredLogger
{
    void Log(
        LogLevel level,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null);

    void Trace(string message, IReadOnlyDictionary<string, object?>? properties = null);

    void Info(string message, IReadOnlyDictionary<string, object?>? properties = null);

    void Warning(string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null);

    void Error(string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null);
}

public enum LogLevel
{
    Trace,
    Info,
    Warning,
    Error
}
