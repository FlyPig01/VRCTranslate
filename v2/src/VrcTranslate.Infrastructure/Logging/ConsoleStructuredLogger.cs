using System.Text.Json;

namespace VrcTranslate.Infrastructure.Logging;

/// <summary>
/// Minimal JSON-lines logger. A later host can adapt this contract to
/// Microsoft.Extensions.Logging without changing provider code.
/// </summary>
public sealed class ConsoleStructuredLogger(string category) : IStructuredLogger
{
    private readonly string _category = string.IsNullOrWhiteSpace(category) ? "VrcTranslate" : category;

    public void Log(
        LogLevel level,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["level"] = level.ToString().ToLowerInvariant(),
            ["category"] = _category,
            ["message"] = message
        };

        if (properties is not null)
        {
            foreach (var pair in properties)
            {
                entry[pair.Key] = pair.Value;
            }
        }

        if (exception is not null)
        {
            entry["exception"] = exception.ToString();
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(entry));
    }

    public void Trace(string message, IReadOnlyDictionary<string, object?>? properties = null) =>
        Log(LogLevel.Trace, message, properties);

    public void Info(string message, IReadOnlyDictionary<string, object?>? properties = null) =>
        Log(LogLevel.Info, message, properties);

    public void Warning(string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null) =>
        Log(LogLevel.Warning, message, properties, exception);

    public void Error(string message, IReadOnlyDictionary<string, object?>? properties = null, Exception? exception = null) =>
        Log(LogLevel.Error, message, properties, exception);
}
