using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeFlow.ApiClient;
using CodeFlow.Ipc;
using Xunit;

namespace CodeFlow.Tests.ApiClient;

/// <summary>
/// The ten streaming commands, and a WebSocket's life against a loopback server.
/// See <c>docs/business-rules/08-api-client.md</c>.
/// </summary>
/// <remarks>
/// The framing has vectors and is covered in <c>StreamFramingTests</c>. What is asserted here is
/// the part no vector can reach: that a connection opens, carries frames both ways, reports its
/// status, and is gone when it is closed. MQTT is the exception — see the note on
/// <c>MqttConnection</c> and the README: nothing here has ever spoken to a broker.
/// </remarks>
public sealed class StreamCommandsTests
{
    /// <summary>The exact set this group registers.</summary>
    private static readonly string[] Expected =
    [
        "api_ws_connect", "api_ws_send", "api_socketio_connect", "api_socketio_emit",
        "api_mqtt_connect", "api_mqtt_publish", "api_mqtt_subscribe", "api_mqtt_unsubscribe",
        "api_stream_disconnect",
    ];

    [Fact]
    public async Task The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        await using var streams = new StreamRegistry((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddApiStreamCommands(streams);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("api_ws_send")]
    [InlineData("api_socketio_emit")]
    [InlineData("api_mqtt_publish")]
    [InlineData("api_mqtt_subscribe")]
    [InlineData("api_mqtt_unsubscribe")]
    [InlineData("api_stream_disconnect")]
    public async Task A_command_missing_its_connection_id_says_so(string command)
    {
        await using var streams = new StreamRegistry((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddApiStreamCommands(streams);
        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse("{}");

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => handler(arguments.RootElement, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("missing required parameter 'id'", failure.Message);
    }

    [Theory]
    [InlineData("api_ws_connect")]
    [InlineData("api_socketio_connect")]
    [InlineData("api_mqtt_connect")]
    public async Task A_connect_command_wants_its_request_object(string command)
    {
        await using var streams = new StreamRegistry((_, _, _) => ValueTask.CompletedTask);
        var registry = new CommandRegistry().AddApiStreamCommands(streams);
        Assert.True(registry.TryGet(command, out var handler));

        using var arguments = JsonDocument.Parse("""{"id":"c1"}""");

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => handler(arguments.RootElement, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("missing required parameter 'request'", failure.Message);
    }

    /// <summary>Closing a connection nobody opened is not an error.</summary>
    /// <remarks>
    /// The socket may have dropped between the user pressing disconnect and the command arriving,
    /// and that race is the normal case.
    /// </remarks>
    [Fact]
    public async Task Disconnecting_a_connection_that_is_not_there_is_a_no_op()
    {
        await using var streams = new StreamRegistry((_, _, _) => ValueTask.CompletedTask);

        await streams.DisconnectAsync("never-opened");

        Assert.Equal(0, streams.Count);
    }

    [Fact]
    public async Task Sending_to_a_connection_nobody_opened_says_so()
    {
        await using var streams = new StreamRegistry((_, _, _) => ValueTask.CompletedTask);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => streams.SendAsync("no-such", "hello", binary: false));

        Assert.Equal("no such connection 'no-such'", failure.Message);
    }

    // ---------- a real socket ----------

    [Fact]
    public async Task A_websocket_opens_carries_a_frame_both_ways_and_closes()
    {
        await using var server = new EchoServer();
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        await streams.ConnectWebSocketAsync("c1", new WsConnectRequest(Url: server.Url), Ct);

        await events.WaitForStatusAsync("open");

        await streams.SendAsync("c1", "hello", binary: false);
        await events.WaitForMessageAsync("received", "hello");

        Assert.Equal("hello", Assert.Single(events.Messages, m => m.Direction == "sent").Data);

        await streams.DisconnectAsync("c1");
        Assert.Equal(0, streams.Count);
    }

    /// <summary>An http URL is rewritten rather than refused, because that is what people paste.</summary>
    [Fact]
    public async Task A_socket_opened_with_an_http_url_still_connects()
    {
        await using var server = new EchoServer();
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        await streams.ConnectWebSocketAsync(
            "c1", new WsConnectRequest(Url: server.Url.Replace("ws://", "http://", StringComparison.Ordinal)), Ct);

        await events.WaitForStatusAsync("open");
    }

    [Fact]
    public async Task A_binary_frame_arrives_base64_encoded_on_its_own_line()
    {
        await using var server = new EchoServer();
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        await streams.ConnectWebSocketAsync("c1", new WsConnectRequest(Url: server.Url), Ct);
        await events.WaitForStatusAsync("open");

        await streams.SendAsync("c1", Convert.ToBase64String([1, 2, 3]), binary: true);

        // DIVERGENCE-API-b: an attachment is its own transcript line, never folded into a packet.
        await events.WaitForMessageAsync("received", Convert.ToBase64String([1, 2, 3]));
        Assert.Contains(events.Messages, m => m.Direction == "received" && m.Binary);
    }

    /// <summary>Reconnecting on the same id replaces the connection rather than doubling it.</summary>
    [Fact]
    public async Task Opening_twice_on_one_id_leaves_one_connection()
    {
        await using var server = new EchoServer();
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        await streams.ConnectWebSocketAsync("c1", new WsConnectRequest(Url: server.Url), Ct);
        await events.WaitForStatusAsync("open");

        await streams.ConnectWebSocketAsync("c1", new WsConnectRequest(Url: server.Url), Ct);

        Assert.Equal(1, streams.Count);
    }

    [Fact]
    public async Task A_url_that_cannot_be_opened_reports_an_error_and_leaves_nothing_behind()
    {
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => streams.ConnectWebSocketAsync("c1", new WsConnectRequest(Url: "ws://127.0.0.1:1/"), Ct));

        Assert.Contains(events.Statuses, s => s.Status == "error");
        Assert.Equal(0, streams.Count);
    }

    /// <summary>
    /// A connection that fails to start is disposed, not merely forgotten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Leaves nothing behind" above only asserts the registry is empty, and emptiness was never
    /// the problem: the failing <c>StartAsync</c> removes its own entry, so the socket and its
    /// <c>CancellationTokenSource</c> were dropped on the floor with nothing left holding a
    /// reference. One leak per attempt, and attempting against a host that is down is the normal
    /// case while a user debugs an endpoint.
    /// </para>
    /// <para>
    /// A real <c>ClientWebSocket</c> cannot be asked whether it was disposed, so the seam is the
    /// registry's own start method taking an <c>IStreamConnection</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_connection_that_fails_to_start_is_disposed()
    {
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        var connection = new FailingConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => streams.StartAsync("c1", connection, Ct));

        Assert.True(connection.Disposed, "the failed connection was forgotten without being disposed");
        Assert.Equal(0, streams.Count);
    }

    /// <summary>
    /// The dispose still happens when the connection has already removed itself.
    /// </summary>
    /// <remarks>
    /// This is the real shape, and the reason the leak existed: every concrete <c>StartAsync</c>
    /// calls <c>registry.Forget(id)</c> before it throws, so the cleanup runs against an entry
    /// that is already gone from the registry. Disposing the local reference rather than looking
    /// the id up again is what makes it reliable — and the count proves it does not double up when
    /// the entry *is* still there.
    /// </remarks>
    [Fact]
    public async Task A_connection_that_forgot_itself_first_is_still_disposed_exactly_once()
    {
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        var connection = new FailingConnection(streams, "c1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => streams.StartAsync("c1", connection, Ct));

        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(0, streams.Count);
    }

    /// <summary>A connection whose dial always fails, and which remembers being disposed.</summary>
    /// <param name="registry">
    /// When given, the fake calls <c>Forget</c> before throwing, exactly as the three real
    /// connections do — that is the state the registry's cleanup has to survive.
    /// </param>
    private sealed class FailingConnection(StreamRegistry? registry = null, string id = "") : IStreamConnection
    {
        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            registry?.Forget(id);
            throw new InvalidOperationException("could not open ws://example.invalid/");
        }

        public Task SendAsync(string payload, bool binary) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Nothing reconnects — the status says what happened and stops there.</summary>
    [Fact]
    public async Task A_server_that_closes_ends_the_connection_rather_than_reopening_it()
    {
        await using var server = new EchoServer(closeAfterFirstFrame: true);
        var events = new Events();
        await using var streams = new StreamRegistry(events.Record);

        await streams.ConnectWebSocketAsync("c1", new WsConnectRequest(Url: server.Url), Ct);
        await events.WaitForStatusAsync("open");

        await streams.SendAsync("c1", "bye", binary: false);
        await events.WaitForStatusAsync("closed");

        Assert.Equal(0, streams.Count);
        Assert.DoesNotContain(events.Statuses.Skip(1), s => s.Status == "connecting");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every event a registry published, and the waits the timing needs.</summary>
    private sealed class Events
    {
        private readonly ConcurrentQueue<StreamMessage> _messages = new();
        private readonly ConcurrentQueue<StreamStatusEvent> _statuses = new();

        public IReadOnlyList<StreamMessage> Messages => [.. _messages];

        public IReadOnlyList<StreamStatusEvent> Statuses => [.. _statuses];

        public ValueTask Record(string name, JsonElement payload, CancellationToken cancellationToken)
        {
            if (name == "api:stream-message")
            {
                _messages.Enqueue(new StreamMessage(
                    payload.GetProperty("id").GetString()!,
                    payload.GetProperty("direction").GetString()!,
                    payload.GetProperty("data").GetString()!,
                    payload.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString()
                        : null,
                    payload.GetProperty("binary").GetBoolean()));
            }
            else
            {
                Assert.Equal("api:stream-status", name);
                _statuses.Enqueue(new StreamStatusEvent(
                    payload.GetProperty("id").GetString()!,
                    payload.GetProperty("status").GetString()!,
                    payload.GetProperty("detail").GetString()!));
            }

            return ValueTask.CompletedTask;
        }

        public Task WaitForStatusAsync(string status) =>
            WaitAsync(() => _statuses.Any(s => s.Status == status), $"status '{status}'");

        public Task WaitForMessageAsync(string direction, string data) =>
            WaitAsync(
                () => _messages.Any(m => m.Direction == direction && m.Data == data),
                $"a {direction} message '{data}'");

        private static async Task WaitAsync(Func<bool> until, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);

            while (DateTime.UtcNow < deadline)
            {
                if (until())
                {
                    return;
                }

                await Task.Delay(20, Ct);
            }

            Assert.Fail($"timed out waiting for {what}");
        }
    }

    /// <summary>A real WebSocket server on loopback that echoes what it is sent.</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serving;

        public EchoServer(bool closeAfterFirstFrame = false)
        {
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Url = $"ws://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/";
                probe.Stop();
            }

            _listener.Prefixes.Add(Url.Replace("ws://", "http://", StringComparison.Ordinal));
            _listener.Start();

            _serving = Task.Run(async () =>
            {
                while (!_stopping.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    _ = Task.Run(() => EchoAsync(context, closeAfterFirstFrame), CancellationToken.None);
                }
            });
        }

        public string Url { get; }

        private async Task EchoAsync(HttpListenerContext context, bool closeAfterFirstFrame)
        {
            WebSocket socket;
            try
            {
                socket = (await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false)).WebSocket;
            }
            catch (Exception e) when (e is WebSocketException or HttpListenerException)
            {
                return;
            }

            var buffer = new byte[8192];

            try
            {
                while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
                {
                    var received = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);

                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    await socket.SendAsync(
                        buffer.AsMemory(0, received.Count), received.MessageType, true, _stopping.Token)
                        .ConfigureAwait(false);

                    if (closeAfterFirstFrame)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", _stopping.Token)
                            .ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, which several of these tests do on purpose.
            }
            finally
            {
                socket.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Close();

            try
            {
                await _serving.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception e) when (e is TimeoutException or HttpListenerException or ObjectDisposedException)
            {
                // Stopping is how the loop ends.
            }

            _stopping.Dispose();
        }
    }
}
