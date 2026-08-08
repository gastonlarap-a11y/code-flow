using System.Text.Json;
using CodeFlow.Diagnostics;

namespace CodeFlow.Ipc;

/// <summary>Pushes an event to the shell, which rebroadcasts it to every window.</summary>
/// <remarks>
/// Features take this delegate rather than the whole <see cref="IpcServer"/>: publishing is all
/// they need, it breaks the cycle between the server and the state that pushes through it, and it
/// is a seam a test can observe without standing up a socket. A delegate rather than an interface,
/// because there is one real implementation and no second one in sight.
/// </remarks>
public delegate ValueTask PublishEvent(string eventName, JsonElement payload, CancellationToken cancellationToken);

/// <summary>
/// Accepts the shell's two connections, dispatches commands, and pushes events.
/// </summary>
/// <remarks>
/// There is exactly one trusted client — the Electron main process this sidecar was spawned by —
/// so this needs no general multi-client routing: two fields, not a connection registry.
/// </remarks>
/// <param name="record">
/// Where a failed command is written down, or nothing when it is not written down at all. Injected
/// rather than reached for: the sidecar passes <see cref="ErrorLog.Record(string, Exception)"/>, and
/// a test that dispatches a deliberately failing command passes nothing — otherwise the suite files
/// its own fixtures in the user's real error log, which is exactly what it did.
/// </param>
public sealed class IpcServer(CommandRegistry registry, string token, Action<string, Exception>? record = null)
    : IAsyncDisposable
{
    private readonly List<IpcConnection> _connections = [];
    private readonly SemaphoreSlim _connectionsLock = new(1, 1);
    private IpcConnection? _stream;

    /// <summary>Raised when a connection drops, so the host can decide whether to keep running.</summary>
    public event EventHandler<IpcChannelKind>? ChannelClosed;

    /// <summary>Serves until <paramref name="cancellationToken"/> trips.</summary>
    public async Task RunAsync(IIpcListener listener, CancellationToken cancellationToken)
    {
        var pumps = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                pumps.Add(ServeAsync(stream, cancellationToken));
                pumps.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        await Task.WhenAll(pumps).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes an event to the shell, which rebroadcasts it to every window.
    /// </summary>
    /// <remarks>
    /// Silently does nothing when the stream channel is not connected. An event is a
    /// notification, not a request: dropping one because nobody is listening is correct, and
    /// throwing here would turn a shell restart into cascading failures in unrelated features.
    /// </remarks>
    public async ValueTask PublishAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
    {
        var connection = _stream;
        if (connection is null)
        {
            return;
        }

        var frame = new IpcEvent { Event = eventName, Payload = payload };
        try
        {
            await connection.SendAsync(frame, IpcJsonContext.Default.IpcEvent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The shell went away mid-write; the accept loop will notice and clean up.
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        IpcConnection? connection = null;

        try
        {
            connection = await HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                // HandshakeAsync already disposed the connection, which owns the stream.
                return;
            }

            await _connectionsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _connections.Add(connection);
                if (connection.Kind == IpcChannelKind.Stream)
                {
                    _stream = connection;
                }
            }
            finally
            {
                _connectionsLock.Release();
            }

            await PumpAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            // A dropped or malformed connection is expected at shutdown and when the shell dies.
        }
        finally
        {
            if (connection is not null)
            {
                if (ReferenceEquals(_stream, connection))
                {
                    _stream = null;
                }

                ChannelClosed?.Invoke(this, connection.Kind);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads the opening frame that says which channel this connection is.</summary>
    private async Task<IpcConnection?> HandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        // The handshake must not be able to hang the accept loop: a connection that opens and
        // then says nothing would otherwise hold a pipe instance open indefinitely.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        // One connection for the whole conversation, including the handshake. A second reader
        // over the same stream would not see what the first had already buffered.
        var connection = new IpcConnection(stream);
        var frame = await connection.ReadFrameAsync(timeout.Token).ConfigureAwait(false);
        if (frame is null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var hello = JsonSerializer.Deserialize(frame, IpcJsonContext.Default.IpcHello);
        var kind = hello?.Channel switch
        {
            "rpc" => IpcChannelKind.Rpc,
            "stream" => IpcChannelKind.Stream,
            _ => (IpcChannelKind?)null,
        };

        // Wrong token or unknown channel: drop it without explaining why.
        if (hello is null || kind is null || !CryptographicEquals(hello.Token, token))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        connection.AssignKind(kind.Value);
        return connection;
    }

    private async Task PumpAsync(IpcConnection connection, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, connection.Lifetime.Token);

        while (!linked.IsCancellationRequested)
        {
            var frame = await connection.ReadFrameAsync(linked.Token).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            if (connection.Kind != IpcChannelKind.Rpc)
            {
                // The stream channel is server-to-client only. A frame arriving on it means the
                // shell is confused about which connection is which — worth failing loudly.
                throw new InvalidDataException("unexpected inbound frame on the stream channel");
            }

            // Do not await: one slow command must not block the next. Correlation is by id, so
            // out-of-order replies are expected and fine.
            _ = DispatchAsync(connection, frame, linked.Token);
        }
    }

    private async Task DispatchAsync(IpcConnection connection, byte[] frame, CancellationToken cancellationToken)
    {
        IpcRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize(frame, IpcJsonContext.Default.IpcRequest);
            if (request is null)
            {
                return;
            }

            if (!registry.TryGet(request.Method, out var handler))
            {
                await RespondErrorAsync(connection, request.Id, $"unknown command '{request.Method}'", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var result = await handler(request.Params, cancellationToken).ConfigureAwait(false);
            await RespondAsync(connection, request.Id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; the shell is not waiting for anything any more.
        }
        catch (Exception ex)
        {
            // Handlers surface failures as Err(String) in 1.7.2, which the renderer sees
            // as a rejected promise. An unhandled exception here would drop the reply and leave
            // the caller's promise pending for the life of the process.
            if (request is not null)
            {
                // Recorded on the way past, because this is the one place every command failure
                // goes through. Until now the message existed only in the renderer's memory: a
                // failed publish had to be retyped by hand to be reported at all.
                record?.Invoke(request.Method, ex);

                await RespondErrorAsync(connection, request.Id, ex.Message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask RespondAsync(
        IpcConnection connection, long id, ReadOnlyMemory<byte> result, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(result);
        var response = new IpcResponse { Id = id, Result = document.RootElement.Clone() };
        await connection.SendAsync(response, IpcJsonContext.Default.IpcResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask RespondErrorAsync(
        IpcConnection connection, long id, string message, CancellationToken cancellationToken)
    {
        var response = new IpcResponse { Id = id, Error = message };
        await connection.SendAsync(response, IpcJsonContext.Default.IpcResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Compares the handshake token in time that does not depend on how many bytes matched.
    /// </summary>
    /// <remarks>
    /// The comparison time still depends on the token's <em>length</em> — that is inherent to
    /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>, which
    /// also returns <see langword="false"/> outright when the lengths differ. Length is not the
    /// secret here; the bytes are.
    /// </remarks>
    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
        _connectionsLock.Dispose();
    }
}
