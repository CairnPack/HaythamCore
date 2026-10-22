using System.Net;
using Haytham.Core.Testing;

namespace Haytham.Core.Testing.Tests;

public class FakeHttpHandlerTests
{
    private const string Url = "https://example.invalid/v1/packages/1.21.json";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Answers_with_the_configured_status_and_body()
    {
        using var handler = new FakeHttpHandler().Respond(Url, HttpStatusCode.OK, """{"id":"1.21"}""");
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(Url, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":"1.21"}""", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Uses_responses_in_order_and_repeats_the_last_one()
    {
        using var handler = new FakeHttpHandler()
            .Respond(Url, HttpStatusCode.ServiceUnavailable)
            .Respond(Url, HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(Url, Token);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK], statuses);
    }

    [Fact]
    public async Task Unconfigured_requests_fail_outside_HttpRequestException()
    {
        using var handler = new FakeHttpHandler().Respond(Url, HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(Url + "?loader=fabric", Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteAsync(Url, Token));
    }

    [Fact]
    public async Task Fail_throws_the_given_error()
    {
        using var handler = new FakeHttpHandler().Fail(Url, new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url, Token));
        Assert.Equal("connection reset", error.Message);
    }

    [Fact]
    public async Task Records_headers_and_body()
    {
        using var handler = new FakeHttpHandler().Respond(HttpMethod.Post, Url, HttpStatusCode.Created);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haytham-launcher/0.1");

        using var content = new StringContent("payload");
        using var response = await client.PostAsync(Url, content, Token);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(Url), request.Url);
        Assert.Equal(["haytham-launcher/0.1"], request.Headers["user-agent"]);
        Assert.Equal("payload", request.Body);
    }

    [Fact]
    public async Task A_responder_can_wait_until_the_request_is_cancelled()
    {
        using var handler = new FakeHttpHandler().Respond(HttpMethod.Get, Url, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var request = client.GetAsync(Url, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }
}
