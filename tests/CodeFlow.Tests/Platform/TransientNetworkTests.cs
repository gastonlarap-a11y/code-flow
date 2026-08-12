using System.Net.Sockets;
using CodeFlow.Platform;
using Xunit;

namespace CodeFlow.Tests.Platform;

/// <summary>
/// Which failures count as "the network was briefly gone".
/// </summary>
/// <remarks>
/// The three resolver wordings are the same outage as reported by three different runtimes, and all
/// three were seen within ninety seconds of each other on 2026-08-12 — the .NET one from the
/// sidecar's own HTTP client, the Go one from an AI CLI. Matching only one of them would have left
/// half the app unprotected.
/// </remarks>
public sealed class TransientNetworkTests
{
    [Theory]
    [InlineData("Error: dial tcp: lookup dev.azure.com: no such host")]
    [InlineData("couldn't reach Azure DevOps: nodename nor servname provided, or not known")]
    [InlineData("Name or service not known")]
    [InlineData("Connection refused")]
    [InlineData("Network is unreachable")]
    public void A_connection_that_never_got_made_is_worth_repeating(string message) =>
        Assert.True(TransientNetwork.Matches(message));

    [Theory]
    // A timeout is the dangerous one, and its absence is deliberate: the far side may well have
    // received the request and still be working on it, so repeating it is how one write becomes two.
    [InlineData("The request timed out")]
    [InlineData("The SSL connection could not be established")]
    [InlineData("401 Unauthorized")]
    [InlineData("QUOTA_EXCEEDED::you have reached your usage limit")]
    [InlineData("")]
    public void Everything_else_is_left_alone(string message) =>
        Assert.False(TransientNetwork.Matches(message));

    [Fact]
    public void Nothing_is_not_a_transient_failure() => Assert.False(TransientNetwork.Matches(null));

    [Fact]
    public void The_reason_is_found_however_deeply_dotnet_buried_it()
    {
        // .NET's own message says nothing useful; the socket's words are two levels down, which is
        // exactly why `StatusText.Reason` exists as well.
        var failure = new InvalidOperationException(
            "the work item could not be read",
            new HttpRequestException(
                "An error occurred while sending the request.",
                new SocketException((int)SocketError.HostNotFound, "no such host")));

        Assert.True(TransientNetwork.Caused(failure));
    }

    [Fact]
    public void A_chain_with_nothing_transient_in_it_is_not_transient()
    {
        var failure = new InvalidOperationException("stale review", new InvalidOperationException("409 Conflict"));

        Assert.False(TransientNetwork.Caused(failure));
    }
}
