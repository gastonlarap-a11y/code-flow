using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Ipc;

namespace CodeFlow.ApiClient;

/// <summary>
/// Every live streaming connection, and the commands that drive one.
/// See <c>docs/business-rules/08-api-client.md</c>, <c>API-025</c>–<c>API-048</c>,
/// <c>API-060</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>DIVERGENCE-API-a</c>: nothing here reconnects.</b> Not the WebSocket, not Socket.IO, not
/// MQTT. 1.7.2's own module comment confirms it is deliberate, and the reason is the
/// product's: an API testing tool that silently re-establishes a connection is falsifying the thing
/// the user is measuring. Do not add backoff.
/// </para>
/// <para>
/// A connection outlives the command that opened it, so its pump holds the publisher rather than a
/// request-scoped token.
/// </para>
/// </remarks>
public sealed class StreamRegistry(PublishEvent publish) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IStreamConnection> _live = new(StringComparer.Ordinal);

    /// <summary>Opens a WebSocket and starts pumping it.</summary>
    public Task ConnectWebSocketAsync(string id, WsConnectRequest request, CancellationToken cancellationToken) =>
        StartAsync(id, new WebSocketConnection(id, this, request), cancellationToken);

    /// <summary>Opens a Socket.IO connection over its own WebSocket.</summary>
    public Task ConnectSocketIoAsync(
        string id, SocketIoConnectRequest request, CancellationToken cancellationToken) =>
        StartAsync(id, new SocketIoConnection(id, this, request), cancellationToken);

    /// <summary>Opens an MQTT connection.</summary>
    public Task ConnectMqttAsync(string id, MqttConnectRequest request, CancellationToken cancellationToken) =>
        StartAsync(id, new MqttConnection(id, this, request), cancellationToken);

    /// <summary>
    /// Registers a connection, starts it, and owns it either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposal on failure is the point. A connection that cannot dial leaves behind a
    /// <c>ClientWebSocket</c> or an <c>IMqttClient</c> and a <c>CancellationTokenSource</c>, and
    /// nothing else can reach them: <c>StartAsync</c>'s own error path calls <c>Forget</c>, so by
    /// the time the exception arrives here the entry is already out of <c>_live</c> and the
    /// <c>DisconnectAsync</c> that precedes the next attempt finds nothing to release. In an
    /// API-testing tool, dialling a host that is down is the normal case while the user debugs it,
    /// so this leaked once per attempt.
    /// </para>
    /// <para>
    /// Cleanup lives here rather than inside each <c>StartAsync</c> so there is exactly one owner
    /// and no chance of a double dispose — and so a failure *after* the dial (<c>OnOpenAsync</c>,
    /// which no inner handler covers) is cleaned up by the same path.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Internal rather than private so a test can hand it a connection that fails on demand — a
    /// real WebSocket dialling a closed port cannot be asked whether it was disposed afterwards.
    /// </remarks>
    internal async Task StartAsync(string id, IStreamConnection connection, CancellationToken cancellationToken)
    {
        await DisconnectAsync(id).ConfigureAwait(false);

        _live[id] = connection;

        try
        {
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // TryRemove first: if StartAsync already called Forget this is a no-op, and if it
            // failed later the entry is still here. Either way the object is disposed exactly once.
            _live.TryRemove(id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Publishes to a topic.</summary>
    public Task PublishAsync(string id, string topic, string payload, int qos, bool retain) =>
        Mqtt(id).PublishAsync(topic, payload, qos, retain);

    /// <summary>Subscribes to a topic filter.</summary>
    public Task SubscribeAsync(string id, string topic, int qos) => Mqtt(id).SubscribeAsync(topic, qos);

    /// <summary>Stops receiving a topic filter.</summary>
    public Task UnsubscribeAsync(string id, string topic) => Mqtt(id).UnsubscribeAsync(topic);

    private MqttConnection Mqtt(string id) =>
        Require(id) as MqttConnection
        ?? throw new InvalidOperationException($"connection '{id}' is not an MQTT connection");

    /// <summary>Sends a raw frame down a WebSocket.</summary>
    public Task SendAsync(string id, string payload, bool binary) =>
        Require(id).SendAsync(payload, binary);

    /// <summary>Emits a Socket.IO event.</summary>
    public Task EmitAsync(string id, string @event, string payloadJson) =>
        Require(id) is SocketIoConnection socket
            ? socket.EmitAsync(@event, payloadJson)
            : throw new InvalidOperationException($"connection '{id}' is not a Socket.IO connection");

    /// <summary>Closes one connection. Not an error if there is none.</summary>
    public async Task DisconnectAsync(string id)
    {
        if (_live.TryRemove(id, out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How many connections are live, so a test can prove a reconnect replaced one.</summary>
    internal int Count => _live.Count;

    internal IStreamConnection Require(string id) =>
        _live.TryGetValue(id, out var connection)
            ? connection
            : throw new InvalidOperationException($"no such connection '{id}'");

    internal async Task PublishMessageAsync(StreamMessage message)
    {
        using var payload = JsonSerializer.SerializeToDocument(message, StreamJsonContext.Default.StreamMessage);

        await publish("api:stream-message", payload.RootElement, CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task PublishStatusAsync(StreamStatusEvent status)
    {
        using var payload = JsonSerializer.SerializeToDocument(status, StreamJsonContext.Default.StreamStatusEvent);

        await publish("api:stream-status", payload.RootElement, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Forgets a connection that ended on its own.</summary>
    internal void Forget(string id) => _live.TryRemove(id, out _);

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _live.Keys)
        {
            await DisconnectAsync(id).ConfigureAwait(false);
        }
    }
}

/// <summary>What every live connection can do.</summary>
public interface IStreamConnection : IAsyncDisposable
{
    /// <summary>
    /// Dials the endpoint and begins pumping it.
    /// </summary>
    /// <remarks>
    /// On the interface rather than on each concrete type so the registry owns the lifetime of all
    /// three the same way — register, start, and dispose if starting throws. Three copies of that
    /// sequence is three chances for one of them to forget the dispose.
    /// </remarks>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Sends a frame. Binary payloads arrive base64-encoded.</summary>
    Task SendAsync(string payload, bool binary);
}

/// <summary>One WebSocket, its pump, and its optional keepalive.</summary>
internal class WebSocketConnection(string id, StreamRegistry registry, WsConnectRequest request) : IStreamConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _pump = Task.CompletedTask;

    protected string Id => id;

    protected StreamRegistry Registry => registry;

    protected ClientWebSocket Socket => _socket;

    /// <summary>Dials, then starts reading. Registration happened before this, so sends queue.</summary>
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        await registry.PublishStatusAsync(new StreamStatusEvent(id, "connecting")).ConfigureAwait(false);

        WebSocketStream.ApplyHeaders(_socket.Options, request.HeaderPairs, request.Protocols);

        if (request.PingIntervalMs > 0)
        {
            _socket.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(request.PingIntervalMs);
        }

        if (!request.Transport.VerifySsl)
        {
            _socket.Options.RemoteCertificateValidationCallback = AcceptUntrusted;
        }

        try
        {
            await _socket.ConnectAsync(new Uri(Target()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or UriFormatException or InvalidOperationException)
        {
            await registry.PublishStatusAsync(new StreamStatusEvent(id, "error", e.Message)).ConfigureAwait(false);
            registry.Forget(id);

            throw new InvalidOperationException($"could not open {Target()}: {e.Message}");
        }

        await OnOpenAsync().ConfigureAwait(false);

        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>Where the socket dials. Socket.IO rewrites this into its handshake URL.</summary>
    protected virtual string Target() => WebSocketStream.NormalizeScheme(request.Url);

    /// <summary>
    /// What "open" means for this protocol.
    /// </summary>
    /// <remarks>
    /// For a plain WebSocket the upgrade is the whole handshake. Socket.IO overrides this, because
    /// its <c>open</c> is not the upgrade — it is the server's own CONNECT reply.
    /// </remarks>
    protected virtual Task OnOpenAsync() => registry.PublishStatusAsync(new StreamStatusEvent(id, "open"));

    /// <summary>
    /// What <c>verify_ssl: false</c> means on a WebSocket: an untrusted issuer or a name mismatch
    /// is accepted, and nothing else is.
    /// </summary>
    /// <remarks>
    /// <b>This is the rule <c>BUG-API-d</c> asks the three streaming verifiers to share</b>, and
    /// since that bug was closed it lives in <see cref="StreamTlsPolicy"/> rather than here, so MQTT
    /// runs the same code instead of a copy that drifted. This callback is only reached when
    /// verification is off, hence the constant.
    /// </remarks>
    private static bool AcceptUntrusted(
        object sender,
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain,
        System.Net.Security.SslPolicyErrors errors) =>
        StreamTlsPolicy.Accepts(errors, verifySsl: false);

    public async Task SendAsync(string payload, bool binary)
    {
        var bytes = binary ? Convert.FromBase64String(payload) : Encoding.UTF8.GetBytes(payload);

        await _socket.SendAsync(
            bytes,
            binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
            endOfMessage: true,
            _stopping.Token).ConfigureAwait(false);

        await registry.PublishMessageAsync(new StreamMessage(id, "sent", payload, Binary: binary))
            .ConfigureAwait(false);
    }

    /// <summary>Publishes each frame as it arrives.</summary>
    protected virtual Task OnFrameAsync(string text) =>
        registry.PublishMessageAsync(new StreamMessage(id, "received", text));

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var frame = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                frame.Write(buffer, 0, received.Count);

                if (!received.EndOfMessage)
                {
                    continue;
                }

                var bytes = frame.ToArray();
                frame.SetLength(0);

                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    // DIVERGENCE-API-b: a binary attachment is its own transcript line, never
                    // reassembled into the packet that referenced it.
                    await registry.PublishMessageAsync(
                        new StreamMessage(id, "received", Convert.ToBase64String(bytes), Binary: true))
                        .ConfigureAwait(false);
                }
                else
                {
                    await OnFrameAsync(Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
                }
            }

            await registry.PublishStatusAsync(new StreamStatusEvent(id, "closed")).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await registry.PublishStatusAsync(new StreamStatusEvent(id, "closed")).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            await registry.PublishStatusAsync(new StreamStatusEvent(id, "error", e.Message)).ConfigureAwait(false);
        }
        finally
        {
            // Nothing reconnects: the connection is gone and the user decides what happens next.
            registry.Forget(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // Stopping is how the pump ends.
        }

        _socket.Dispose();
        _stopping.Dispose();
    }
}

/// <summary>Socket.IO, framed by hand on top of a WebSocket.</summary>
internal sealed class SocketIoConnection(string id, StreamRegistry registry, SocketIoConnectRequest request)
    : WebSocketConnection(id, registry, new WsConnectRequest(
        Url: request.Url, Headers: request.Headers, Options: request.Options))
{
    protected override string Target() =>
        SocketIoFraming.HandshakeUrl(request.Url, request.Path, request.IsV4 ? 4 : 3, request.QueryPairs);

    /// <summary>
    /// The upgrade is not "open" here.
    /// </summary>
    /// <remarks>
    /// A Socket.IO connection is open once the <em>server</em> answers the CONNECT packet, which is
    /// two framing layers above the WebSocket handshake. Reporting open on the upgrade would let a
    /// user emit into a namespace the server has not accepted them into.
    /// </remarks>
    protected override async Task OnOpenAsync()
    {
        var body = SocketIoFraming.ConnectBody(request.AuthJson, request.IsV4);
        var frame = SocketIoFraming.MessageFrame(SocketIoFraming.Connect, request.Namespace, body);

        await SendRawAsync(frame).ConfigureAwait(false);
    }

    public Task EmitAsync(string @event, string payloadJson)
    {
        var frame = SocketIoFraming.MessageFrame(
            SocketIoFraming.Event, request.Namespace, SocketIoFraming.EventArgs(@event, payloadJson));

        return SendRawAsync(frame);
    }

    protected override async Task OnFrameAsync(string text)
    {
        if (SocketIoFraming.DecodeEngine(text) is not { } engine)
        {
            return;
        }

        switch (engine.Kind)
        {
            case '0':
                await Registry.PublishMessageAsync(new StreamMessage(Id, "system", $"handshake {engine.Body}"))
                    .ConfigureAwait(false);
                return;

            case '2':
                // A v3 server pings and expects a pong; a v4 server is pinged by the client. Both
                // are answered the same way, which costs nothing and covers the asymmetry.
                await SendRawAsync(SocketIoFraming.EngineFrame('P')).ConfigureAwait(false);
                return;

            case '1':
                await Registry.PublishStatusAsync(new StreamStatusEvent(Id, "closed", "server closed the transport"))
                    .ConfigureAwait(false);
                return;

            case '4':
                await OnPacketAsync(SocketIoFraming.DecodePacket(engine.Body)).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

    private async Task OnPacketAsync(SocketIoPacket packet)
    {
        switch (packet.Kind)
        {
            case SocketIoFraming.Connect:
                await Registry.PublishStatusAsync(new StreamStatusEvent(Id, "open", packet.Namespace))
                    .ConfigureAwait(false);
                return;

            case SocketIoFraming.ConnectError:
                await Registry.PublishStatusAsync(new StreamStatusEvent(Id, "error", packet.Data))
                    .ConfigureAwait(false);
                return;

            case SocketIoFraming.Disconnect:
                await Registry.PublishStatusAsync(new StreamStatusEvent(Id, "closed", packet.Namespace))
                    .ConfigureAwait(false);
                return;

            case SocketIoFraming.Event or SocketIoFraming.BinaryEvent:
                var (name, payload) = SocketIoFraming.SplitEvent(packet.Data);
                await Registry.PublishMessageAsync(new StreamMessage(Id, "received", payload, name))
                    .ConfigureAwait(false);
                return;

            default:
                // AMBIGUOUS-API-a: an ACK is shown but never correlated back to the emit that asked
                // for it — 1.7.2 keeps no pending-ack registry either.
                await Registry.PublishMessageAsync(new StreamMessage(Id, "system", packet.Data))
                    .ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Sends a frame without echoing it as a raw transcript line.</summary>
    private async Task SendRawAsync(string frame)
    {
        await Socket.SendAsync(
            Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
            .ConfigureAwait(false);

        await Registry.PublishMessageAsync(new StreamMessage(Id, "sent", frame)).ConfigureAwait(false);
    }
}

/// <summary>The two streaming events this feature publishes.</summary>
/// <remarks>
/// camelCase, like the terminal's — these payloads are assembled by hand rather than serialised
/// from a record, and the UI reads them as written.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StreamMessage))]
[JsonSerializable(typeof(StreamStatusEvent))]
// For SocketIoFraming.EventArgs, which JSON-escapes an event name. A bare string needs no naming
// policy, but it does need to come from a context rather than the reflection-based overload —
// that overload is the one thing in this codebase a trimmed or AOT publish would break.
[JsonSerializable(typeof(string))]
internal sealed partial class StreamJsonContext : JsonSerializerContext;
