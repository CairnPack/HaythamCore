namespace Haytham.Core.Testing;

/// <summary>A request received by <see cref="FakeHttpHandler"/>, copied so it stays readable after the caller disposes it.</summary>
/// <remarks>
/// Not a record on purpose: headers and body may hold credentials, and a record's generated <c>ToString()</c>
/// would print them into assertion messages and CI logs. <see cref="ToString"/> shows only the method and the URL
/// without query or user info.
/// </remarks>
public sealed class RecordedHttpRequest
{
    private RecordedHttpRequest(HttpMethod method, Uri? url, IReadOnlyDictionary<string, string[]> headers, string? body)
    {
        Method = method;
        Url = url;
        Headers = headers;
        Body = body;
    }

    /// <summary>The HTTP method.</summary>
    public HttpMethod Method { get; }

    /// <summary>The absolute request URL.</summary>
    public Uri? Url { get; }

    /// <summary>Request and content headers; names compare case-insensitively.</summary>
    public IReadOnlyDictionary<string, string[]> Headers { get; }

    /// <summary>The body as a string, or <see langword="null"/> when the request had no content.</summary>
    public string? Body { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var url = Url?.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return $"{Method} {url}";
    }

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
