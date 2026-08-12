using System.Net;
using System.Net.Sockets;
using CodeFlow.Platform;
using Xunit;

namespace CodeFlow.Tests.Platform;

/// <summary>
/// Which HTTP requests survive a moment without a network, and which deliberately do not.
/// </summary>
/// <remarks>
/// The division is the whole design: a read repeated is the same read, and a write repeated is a
/// second comment on somebody's pull request. See <c>docs/business-rules/06-providers.md</c>.
/// </remarks>
public sealed class TransientRetryHandlerTests
{
    private static readonly Uri Endpoint = new("https://dev.azure.com/org/_apis/wit/workitems/3");

    [Fact]
    public async Task A_read_that_could_not_resolve_its_host_is_sent_again()
    {
        var inner = new FailingHandler(ResolutionFailure(), attemptsThatFail: 1);
        using var client = new HttpClient(new TransientRetryHandler(inner));

        var response = await client.GetAsync(Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task The_repeat_carries_the_headers_the_first_attempt_had()
    {
        // Losing these would turn a retried call into an unauthenticated one, which reads back as a
        // credential problem — the single most misleading thing this could do.
        var inner = new FailingHandler(ResolutionFailure(), attemptsThatFail: 1);
        using var client = new HttpClient(new TransientRetryHandler(inner));

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "Basic dGVzdA==");
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("Basic dGVzdA==", inner.LastAuthorization);
    }

    [Fact]
    public async Task A_write_is_never_repeated()
    {
        var inner = new FailingHandler(ResolutionFailure(), attemptsThatFail: 1);
        using var client = new HttpClient(new TransientRetryHandler(inner));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostAsync(Endpoint, new StringContent("{}"), TestContext.Current.CancellationToken));

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task A_failure_that_is_not_the_network_is_not_retried()
    {
        var inner = new FailingHandler(new HttpRequestException("the response ended prematurely"), attemptsThatFail: 1);
        using var client = new HttpClient(new TransientRetryHandler(inner));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(Endpoint, TestContext.Current.CancellationToken));

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task An_outage_that_outlasts_the_retry_reports_the_original_reason()
    {
        var inner = new FailingHandler(ResolutionFailure(), attemptsThatFail: 2);
        using var client = new HttpClient(new TransientRetryHandler(inner));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(Endpoint, TestContext.Current.CancellationToken));

        // `StatusText.Reason` digs the socket's own words out of this, and the providers put them in
        // front of the user — so the second failure has to keep the inner exception the first had.
        Assert.Contains("nodename nor servname", failure.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, inner.Attempts);
    }

    /// <summary>The failure macOS raises for a name that will not resolve, wrapped as .NET wraps it.</summary>
    private static HttpRequestException ResolutionFailure() =>
        new(
            "An error occurred while sending the request.",
            new SocketException((int)SocketError.HostNotFound, "nodename nor servname provided, or not known"));

    private sealed class FailingHandler(HttpRequestException failure, int attemptsThatFail) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(string.Empty, values)
                : null;

            return Attempts <= attemptsThatFail
                ? throw failure
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
