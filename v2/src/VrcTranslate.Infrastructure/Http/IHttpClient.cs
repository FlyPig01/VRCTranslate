namespace VrcTranslate.Infrastructure.Http;

/// <summary>
/// Small transport seam for provider adapters. The default implementation wraps
/// <see cref="System.Net.Http.HttpClient"/>; tests can provide an in-memory fake.
/// </summary>
public interface IHttpClient
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}

public sealed class HttpClientAdapter(HttpClient client) : IHttpClient
{
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
