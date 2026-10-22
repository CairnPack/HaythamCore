using System.Net;
using System.Text;

namespace Haytham.Core.Testing;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers with configured responses, so tests never touch the network.
/// Pass it to the code under test as <c>new HttpClient(handler)</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Responses are matched by method and absolute URL (query included).</item>
/// <item>Several responses for the same request are used in the order they were added; the last one keeps
/// answering after that. This is how retry tests are written: 503, 503, then 200.</item>
/// <item>A request without a configured response throws <see cref="InvalidOperationException"/>, not
/// <see cref="HttpRequestException"/>, so retry logic does not swallow a missing setup.</item>
/// <item>Every request is recorded in <see cref="Requests"/>, with its headers and body, before it is answered.</item>
/// <item>The handler is thread-safe; concurrent requests may consume a response queue in any order.</item>
/// </list>
/// </remarks>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(HttpMethod Method, Uri Url), Queue<Responder>> _responders = [];
    private readonly List<RecordedHttpRequest> _requests = [];

    /// <summary>Computes a response; it receives the request and the caller's cancellation token.</summary>
    public delegate Task<HttpResponseMessage> Responder(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>The requests received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedHttpRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answers <c>GET <paramref name="url"/></c> with <paramref name="status"/> and an optional UTF-8 body.</summary>
    public FakeHttpHandler Respond(string url, HttpStatusCode status, string? body = null) =>
        Respond(HttpMethod.Get, url, status, body);

    /// <summary>Answers <paramref name="method"/> <paramref name="url"/> with <paramref name="status"/> and an optional UTF-8 body.</summary>
    public FakeHttpHandler Respond(HttpMethod method, string url, HttpStatusCode status, string? body = null) =>
        Respond(method, url, (_, _) =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8);
            }

            return Task.FromResult(response);
        });

    /// <summary>
    /// Answers <paramref name="method"/> <paramref name="url"/> with <paramref name="responder"/>, for responses that
    /// need headers (such as <c>Retry-After</c>), streamed bodies, delays, or waiting until cancelled.
    /// </summary>
    public FakeHttpHandler Respond(HttpMethod method, string url, Responder responder)
    {
        var key = (method, new Uri(url, UriKind.Absolute));
        lock (_lock)
        {
            if (!_responders.TryGetValue(key, out var queue))
            {
                queue = new Queue<Responder>();
                _responders[key] = queue;
            }

            queue.Enqueue(responder);
        }

        return this;
    }

    /// <summary>Makes <c>GET <paramref name="url"/></c> throw <paramref name="error"/>, for example an I/O failure.</summary>
    public FakeHttpHandler Fail(string url, Exception error) =>
        Respond(HttpMethod.Get, url, (_, _) => Task.FromException<HttpResponseMessage>(error));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var recorded = await RecordedHttpRequest.FromAsync(request, cancellationToken).ConfigureAwait(false);
        Responder? responder;
        lock (_lock)
        {
            _requests.Add(recorded);
            responder = NextResponder(request);
        }

        if (responder is null)
        {
            throw new InvalidOperationException($"No fake response is configured for {request.Method} {request.RequestUri}.");
        }

        var response = await responder(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }

    private Responder? NextResponder(HttpRequestMessage request)
    {
        if (request.RequestUri is null || !_responders.TryGetValue((request.Method, request.RequestUri), out var queue))
        {
            return null;
        }

        return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
    }
}
