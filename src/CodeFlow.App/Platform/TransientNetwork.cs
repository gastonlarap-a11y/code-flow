namespace CodeFlow.Platform;

/// <summary>
/// Whether a failure is the network being briefly unavailable rather than something being wrong.
/// </summary>
/// <remarks>
/// <para>
/// Recognised by text, which is unusual in this codebase and deliberate here: one outage has to be
/// recognised through two reporters that share nothing. .NET raises a <c>SocketException</c> the
/// sidecar could type against, but an AI CLI is a separate process whose entire account of itself is
/// a line of English on stderr — <c>agy</c> calls a failed lookup <c>no such host</c>, and there is
/// nothing typed about that. A table matched against the innermost message is what lets both halves
/// agree on "this one is worth trying again".
/// </para>
/// <para>
/// The list is short on purpose. Every entry names a failure that happened <em>before</em> the
/// request reached the far side, and that is the whole argument for retrying: nothing arrived, so
/// nothing can have been acted on. A timeout is absent for exactly that reason — it may well mean
/// the far side received the request and is still working on it, and repeating that is how one
/// comment becomes two.
/// </para>
/// </remarks>
internal static class TransientNetwork
{
    /// <remarks>
    /// The first three are one failure — a name that would not resolve — as worded by Go's resolver,
    /// by <c>getaddrinfo</c> on macOS and by glibc. All three were observed on 2026-08-12 within
    /// ninety seconds of each other, from two different processes.
    /// </remarks>
    private static readonly string[] Signatures =
    [
        "no such host",
        "nodename nor servname provided",
        "name or service not known",
        "temporary failure in name resolution",
        "no address associated with hostname",
        "connection refused",
        "network is unreachable",
        "network is down",
        "no route to host",
    ];

    /// <summary>Whether a message reads like a connection that never got made.</summary>
    public static bool Matches(string? message) =>
        message is not null
        && Array.Exists(
            Signatures,
            signature => message.Contains(signature, StringComparison.OrdinalIgnoreCase));

    /// <summary>The same question of an exception and everything it wraps.</summary>
    /// <remarks>
    /// .NET buries the useful text: an <see cref="HttpRequestException"/> says only "An error
    /// occurred while sending the request", and the socket's own words are one or two levels down.
    /// </remarks>
    public static bool Caused(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (Matches(current.Message))
            {
                return true;
            }
        }

        return false;
    }
}
