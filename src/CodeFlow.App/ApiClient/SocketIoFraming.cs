using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeFlow.ApiClient;

/// <summary>An Engine.IO frame, before any Socket.IO meaning is read from it.</summary>
internal readonly record struct EngineFrame(char Kind, string Body);

/// <summary>One decoded Socket.IO packet.</summary>
internal readonly record struct SocketIoPacket(
    int Kind,
    string Namespace,
    string Data,
    int? AckId,
    int Attachments);

/// <summary>
/// Engine.IO and Socket.IO framing, hand-rolled rather than taken from a client library.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-030</c>–<c>API-037</c>.
/// </summary>
/// <remarks>
/// <para>
/// No client library, deliberately. Every Socket.IO client comes with a reconnection state machine,
/// and <c>DIVERGENCE-API-a</c> is explicit that this application has none: an API testing tool that
/// silently reconnects is falsifying the thing the user is measuring.
/// </para>
/// <para>
/// There are two framing layers per text frame — Engine.IO's single-digit opcode, then Socket.IO's
/// own packet type, optional attachment count, optional namespace and optional ack id — and this
/// file is only the framing. The socket underneath it is <see cref="WebSocketStream"/>.
/// </para>
/// </remarks>
internal static class SocketIoFraming
{
    // Socket.IO packet types.
    public const int Connect = 0;
    public const int Disconnect = 1;
    public const int Event = 2;
    public const int Ack = 3;
    public const int ConnectError = 4;
    public const int BinaryEvent = 5;
    public const int BinaryAck = 6;

    /// <summary>Wraps a Socket.IO packet in its Engine.IO MESSAGE frame.</summary>
    /// <remarks>
    /// The root namespace contributes <em>no</em> segment at all — not <c>/</c> — while a named one
    /// is comma-terminated. Sending <c>40/,</c> where a server expects <c>40</c> is refused by some
    /// implementations and silently ignored by others, which is the worse outcome.
    /// </remarks>
    public static string MessageFrame(int kind, string @namespace, string body)
    {
        var segment = NamespaceSegment(@namespace);

        return $"4{kind.ToString(CultureInfo.InvariantCulture)}{segment}{body}";
    }

    /// <summary>The namespace part of a frame: empty for the root, comma-terminated otherwise.</summary>
    internal static string NamespaceSegment(string @namespace)
    {
        var trimmed = @namespace.Trim();
        if (trimmed.Length == 0 || trimmed == "/")
        {
            return string.Empty;
        }

        var normalized = trimmed.StartsWith('/') ? trimmed : $"/{trimmed}";

        return $"{normalized},";
    }

    /// <summary>
    /// The CONNECT payload, which carries auth only on v4 and only when there is some.
    /// </summary>
    /// <remarks>
    /// Socket.IO 2 has no auth field in its CONNECT packet at all, so sending one there produces a
    /// packet the server cannot parse. An empty object is not "some": it would be an auth handshake
    /// claiming nothing.
    /// </remarks>
    public static string ConnectBody(string authJson, bool v4)
    {
        if (!v4)
        {
            return string.Empty;
        }

        var trimmed = authJson.Trim();

        return trimmed.Length == 0 || trimmed == "{}" ? string.Empty : trimmed;
    }

    /// <summary>Builds an EVENT frame's argument array.</summary>
    /// <remarks>
    /// One payload keeps its own shape rather than being wrapped, because that is what a Socket.IO
    /// handler receives as its first argument. An unparseable payload is refused rather than sent as
    /// a string, which would silently change the event's type on the other side.
    /// </remarks>
    public static string EventArgs(string @event, string payloadJson)
    {
        var name = JsonSerializer.Serialize(@event, StreamJsonContext.Default.String);
        var trimmed = payloadJson.Trim();

        if (trimmed.Length == 0)
        {
            return $"[{name}]";
        }

        try
        {
            using var parsed = JsonDocument.Parse(trimmed);

            return $"[{name},{trimmed}]";
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"The event payload is not valid JSON: {e.Message}");
        }
    }

    /// <summary>Splits a decoded EVENT array back into its name and the rest.</summary>
    /// <remarks>
    /// One remaining argument keeps its shape; several become an array, which is how the transcript
    /// shows what the handler would have received.
    /// </remarks>
    public static (string Event, string Payload) SplitEvent(string data)
    {
        try
        {
            using var parsed = JsonDocument.Parse(data);

            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() == 0)
            {
                return (string.Empty, data);
            }

            var items = parsed.RootElement.EnumerateArray().ToArray();
            var name = items[0].ValueKind == JsonValueKind.String ? items[0].GetString()! : items[0].GetRawText();

            return items.Length switch
            {
                1 => (name, string.Empty),
                2 => (name, items[1].GetRawText()),
                _ => (name, $"[{string.Join(",", items.Skip(1).Select(i => i.GetRawText()))}]"),
            };
        }
        catch (JsonException)
        {
            return (string.Empty, data);
        }
    }

    /// <summary>Reads one Engine.IO frame, or nothing when the text is not one.</summary>
    public static EngineFrame? DecodeEngine(string frame) =>
        frame.Length == 0 ? null : new EngineFrame(frame[0], frame[1..]);

    /// <summary>The bare Engine.IO frame for a control opcode.</summary>
    public static string EngineFrame(char kind) => kind switch
    {
        'O' => "0",
        'C' => "1",
        'I' => "2",
        'P' => "3",
        _ => "4",
    };

    /// <summary>
    /// Reads a Socket.IO packet body — everything after the Engine.IO opcode.
    /// </summary>
    /// <remarks>
    /// The order is fixed and every part after the type is optional: type digit, then an attachment
    /// count terminated by <c>-</c> for the binary types, then a namespace terminated by <c>,</c>,
    /// then an ack id, then the data.
    /// </remarks>
    public static SocketIoPacket DecodePacket(string body)
    {
        if (body.Length == 0)
        {
            return new SocketIoPacket(-1, "/", string.Empty, null, 0);
        }

        var kind = body[0] - '0';
        var index = 1;
        var attachments = 0;

        if (kind is BinaryEvent or BinaryAck)
        {
            var dash = body.IndexOf('-', index);
            if (dash > 0 && int.TryParse(body[index..dash], CultureInfo.InvariantCulture, out var count))
            {
                attachments = count;
                index = dash + 1;
            }
        }

        var @namespace = "/";
        if (index < body.Length && body[index] == '/')
        {
            var comma = body.IndexOf(',', index);

            if (comma > 0)
            {
                @namespace = body[index..comma];
                index = comma + 1;
            }
            else
            {
                // A namespace with nothing after it: the whole remainder is the name.
                @namespace = body[index..];
                index = body.Length;
            }
        }

        int? ackId = null;
        var digits = index;
        while (digits < body.Length && char.IsAsciiDigit(body[digits]))
        {
            digits++;
        }

        if (digits > index)
        {
            ackId = int.Parse(body[index..digits], CultureInfo.InvariantCulture);
            index = digits;
        }

        return new SocketIoPacket(kind, @namespace, body[index..], ackId, attachments);
    }

    /// <summary>
    /// The WebSocket URL a Socket.IO handshake opens.
    /// </summary>
    /// <remarks>
    /// Websocket-only: no long-polling upgrade dance, which is why <c>transport=websocket</c> is
    /// fixed rather than negotiated. The scheme is upgraded to <c>ws</c>/<c>wss</c>, the path is
    /// normalised to a single trailing slash, and any caller query is appended after the two the
    /// protocol requires.
    /// </remarks>
    public static string HandshakeUrl(string url, string path, int eio, IReadOnlyList<(string Key, string Value)> query)
    {
        var normalized = WebSocketStream.NormalizeScheme(url);

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("ws" or "wss"))
        {
            throw new InvalidOperationException($"'{url}' is not a URL a Socket.IO connection can open");
        }

        var route = path.Trim();
        route = route.Length == 0 ? "/socket.io/" : $"/{route.Trim('/')}/";

        var existing = parsed.Query.TrimStart('?');
        var parts = new List<string>();

        if (existing.Length > 0)
        {
            parts.Add(existing);
        }

        parts.Add($"EIO={eio.ToString(CultureInfo.InvariantCulture)}");
        parts.Add("transport=websocket");
        parts.AddRange(query.Select(q => $"{Encode(q.Key)}={Encode(q.Value)}"));

        return $"{parsed.Scheme}://{parsed.Authority}{route}?{string.Join("&", parts)}";
    }

    /// <summary>Form encoding, which is what 1.7.2's query serialiser emits.</summary>
    /// <remarks>A space becomes <c>+</c> here, not <c>%20</c>.</remarks>
    private static string Encode(string value)
    {
        var encoded = new StringBuilder(value.Length);

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '*')
            {
                encoded.Append(c);
            }
            else if (c == ' ')
            {
                encoded.Append('+');
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}
