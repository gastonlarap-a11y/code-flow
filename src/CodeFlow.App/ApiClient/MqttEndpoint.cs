using System.Globalization;

namespace CodeFlow.ApiClient;

/// <summary>Where an MQTT connection is going.</summary>
internal readonly record struct MqttEndpointInfo(string Host, int Port, bool Tls);

/// <summary>
/// The pure parts of the MQTT client.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-040</c>–<c>API-048</c>.
/// </summary>
internal static class MqttEndpoint
{
    /// <summary>
    /// Reads a broker endpoint out of whatever the user typed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four schemes and a bare <c>host:port</c>. <c>mqtt</c> and <c>tcp</c> are plain; <c>mqtts</c>
    /// and <c>ssl</c> are TLS, and each carries its own default port — 1883 and 8883.
    /// </para>
    /// <para>
    /// The WebSocket schemes are rejected rather than quietly treated as TCP: MQTT over WebSocket
    /// is a different transport, and connecting to port 8083 as if it spoke raw MQTT would hang
    /// rather than fail.
    /// </para>
    /// </remarks>
    public static MqttEndpointInfo Parse(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("An MQTT connection needs a broker address");
        }

        var scheme = string.Empty;
        var rest = trimmed;

        var separator = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (separator > 0)
        {
            scheme = trimmed[..separator].ToLowerInvariant();
            rest = trimmed[(separator + 3)..];
        }

        var tls = scheme switch
        {
            "mqtt" or "tcp" => false,
            "mqtts" or "ssl" or "tls" => true,
            "" => false,
            _ => throw new InvalidOperationException(
                $"'{scheme}' is not an MQTT scheme; use mqtt, mqtts, tcp or ssl " +
                "(MQTT over WebSocket is a different transport and is not supported here)"),
        };

        // Anything after the authority is a path the broker never sees on a raw MQTT connection.
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            rest = rest[..slash];
        }

        string host;
        int? port = null;

        if (rest.StartsWith('['))
        {
            // An IPv6 literal, whose own colons must not be read as a port separator.
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new InvalidOperationException($"'{url}' has an unterminated IPv6 address");
            }

            host = rest[1..close];

            if (close + 1 < rest.Length && rest[close + 1] == ':')
            {
                port = ParsePort(rest[(close + 2)..], url);
            }
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                host = rest[..colon];
                port = ParsePort(rest[(colon + 1)..], url);
            }
            else
            {
                host = rest;
            }
        }

        if (host.Length == 0)
        {
            throw new InvalidOperationException($"'{url}' names no broker host");
        }

        return new MqttEndpointInfo(host, port ?? (tls ? 8883 : 1883), tls);
    }

    /// <summary>
    /// The client id a connection announces.
    /// </summary>
    /// <remarks>
    /// A generated one is prefixed so a broker's session list says where it came from, and it is
    /// always 17 characters: <c>codeflow-</c> plus eight hexadecimal digits.
    /// </remarks>
    public static string ResolveClientId(string requested)
    {
        var trimmed = requested.Trim();

        return trimmed.Length > 0
            ? trimmed
            : $"codeflow-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}";
    }

    /// <summary>
    /// Clamps a quality of service to the three MQTT defines.
    /// </summary>
    /// <remarks>
    /// Anything out of range becomes 0 rather than the nearest valid value. At-most-once is the
    /// weakest guarantee, so a malformed request cannot accidentally ask the broker for more
    /// delivery effort than the user chose.
    /// </remarks>
    public static int ClampQos(int qos) => qos is >= 0 and <= 2 ? qos : 0;

    private static int ParsePort(string text, string url) =>
        int.TryParse(text, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new InvalidOperationException($"'{url}' does not name a valid port");
}
