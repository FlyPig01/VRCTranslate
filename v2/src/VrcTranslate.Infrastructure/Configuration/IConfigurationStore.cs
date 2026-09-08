namespace VrcTranslate.Infrastructure.Configuration;

public interface IConfigurationStore<T>
    where T : class
{
    Task<T> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(T value, CancellationToken cancellationToken = default);
}
