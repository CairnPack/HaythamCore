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

    [Theory]
    [InlineData("https://example.invalid/a/b?access_token=SECRET", "/a/b")]
    [InlineData("https://example.invalid/a/b?x=1&access_token=SECRET&y=2", "/a/b")]
    [InlineData("https://user:SECRET@example.invalid/a/b", "/a/b")]
    [InlineData("https://SECRET@example.invalid/a/b", "/a/b")]
    [InlineData("https://user:SECRET@example.invalid/a/b?access_token=SECRET", "/a/b")]
    [InlineData("https://example.invalid/a/b#SECRET", "/a/b")]
    [InlineData("https://example.invalid/a/b?t=SECRET#SECRET", "/a/b")]
    [InlineData("https://example.invalid/a/b?t=%53ECRET", "/a/b")]
    [InlineData("https://example.invalid/a/b?t=a%20SECRET%2Fc", "/a/b")]
    [InlineData("https://user%3ASECRET@example.invalid/a%20b/c", "/a%20b/c")]
    [InlineData("https://example.invalid:8443/a/b?t=SECRET", "/a/b")]
    [InlineData("https://example.invalid/a/b", "/a/b")]
    [InlineData("https://example.invalid/", "/")]
    public async Task Unmatched_request_message_leaves_out_query_and_user_info(string url, string expectedPath)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            using var handler = new FakeHttpHandler();
            using var invoker = new HttpMessageInvoker(handler);
            using var request = new HttpRequestMessage(method, url);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, Token));

            Assert.DoesNotContain("SECRET", error.Message);
            Assert.DoesNotContain("?", error.Message);
            Assert.DoesNotContain("@", error.Message);
            Assert.DoesNotContain("#", error.Message);
            Assert.Contains(method.Method, error.Message);
            Assert.Contains(expectedPath, error.Message);
            Assert.Contains("example.invalid", error.Message);
        }
    }

    [Fact]
    public async Task Unmatched_request_message_handles_a_missing_request_uri()
    {
        using var handler = new FakeHttpHandler();
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage { Method = HttpMethod.Put };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, Token));

        Assert.Contains("PUT", error.Message);
    }

    [Fact]
    public async Task Unmatched_request_with_a_matching_url_but_other_method_names_the_method()
    {
        using var handler = new FakeHttpHandler().Respond(Url + "?token=SECRET", HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteAsync(Url + "?token=SECRET", Token));

        Assert.Contains("DELETE", error.Message);
        Assert.DoesNotContain("SECRET", error.Message);
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
    public async Task ToString_leaves_out_credentials()
    {
        using var handler = new FakeHttpHandler().Respond(HttpMethod.Post, Url + "?token=secret", HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "secret");

        using var content = new StringContent("refresh_token=secret");
        using var response = await client.PostAsync(Url + "?token=secret", content, Token);

        var text = Assert.Single(handler.Requests).ToString();
        Assert.Equal($"POST {Url}", text);
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
