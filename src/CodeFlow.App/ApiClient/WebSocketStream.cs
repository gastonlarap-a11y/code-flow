using System.Net.WebSockets;

namespace CodeFlow.ApiClient;

/// <summary>
/// The WebSocket transport.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-025</c>–<c>API-029</c>.
/// </summary>
internal static class WebSocketStream
{
    /// <summary>
    /// Rewrites an http(s) URL to its WebSocket scheme.
    /// </summary>
    /// <remarks>
    /// People paste the address they see in a browser, and <c>https://</c> is what a WebSocket
    /// endpoint's documentation usually shows. Only the scheme is touched — the case of the rest of
    /// the URL is left alone, because a path can be case-sensitive.
    /// </remarks>
    public static string NormalizeScheme(string url)
    {
        var trimmed = url.Trim();

        var separator = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return trimmed;
        }

        var scheme = trimmed[..separator].ToLowerInvariant();
        var rest = trimmed[(separator + 3)..];

        return scheme switch
        {
            "https" or "wss" => $"wss://{rest}",
            "http" or "ws" => $"ws://{rest}",
            _ => trimmed,
        };
    }

    /// <summary>
    /// Applies the caller's headers to a client, and its subprotocols.
    /// </summary>
    /// <remarks>
    /// The first occurrence of a name replaces whatever the client already had; repeats of the same
    /// name are appended rather than overwriting each other. That distinction is the difference
    /// between one <c>Origin</c> and two, and between one <c>X-Tag</c> and the several a caller
    /// meant to send.
    /// </remarks>
    public static void ApplyHeaders(
        ClientWebSocketOptions options,
        IReadOnlyList<IReadOnlyList<string>> headers,
        IReadOnlyList<string> subprotocols)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers.Where(h => h.Count >= 2))
        {
            var name = header[0].Trim();

            // ClientWebSocketOptions has no "append"; setting the same name twice replaces. Joining
            // repeats with a comma is the wire-equivalent form, and it is what keeps two X-Tags two
            // rather than one.
            if (seen.Add(name))
            {
                options.SetRequestHeader(name, header[1]);
            }
            else
            {
                var joined = string.Join(", ", headers
                    .Where(h => h.Count >= 2 && string.Equals(h[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    .Select(h => h[1]));

                options.SetRequestHeader(name, joined);
            }
        }

        foreach (var subprotocol in subprotocols.Where(s => s.Trim().Length > 0))
        {
            options.AddSubProtocol(subprotocol.Trim());
        }
    }

    /// <summary>
    /// How many distinct values a header name ends up with.
    /// </summary>
    /// <remarks>
    /// Exposed for the vector that asserts the merge rule, since
    /// <see cref="ClientWebSocketOptions"/> does not let a caller read back what it was given.
    /// </remarks>
    internal static IReadOnlyList<string> MergedValues(
        IReadOnlyList<IReadOnlyList<string>> headers, string name) =>
        [.. headers
            .Where(h => h.Count >= 2 && string.Equals(h[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h[1])];
}
