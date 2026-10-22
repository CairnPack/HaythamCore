namespace Haytham.Core.Testing;

/// <summary>A request received by <see cref="FakeHttpHandler"/>, copied so it stays readable after the caller disposes it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The absolute request URL.</param>
/// <param name="Headers">Request and content headers; names compare case-insensitively.</param>
/// <param name="Body">The body as a string, or <see langword="null"/> when the request had no content.</param>
public sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri? Url,
    IReadOnlyDictionary<string, string[]> Headers,
    string? Body)
{
    internal static async Task<RecordedHttpRequest> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = [.. values];
        }

        string? body = null;
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = [.. values];
            }

            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RecordedHttpRequest(request.Method, request.RequestUri, headers, body);
    }
}
