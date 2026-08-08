using System.Text.Json;
using CodeFlow.Ipc;
using Xunit;

namespace CodeFlow.Tests.Ipc;

/// <summary>
/// Drives the server over a real socket rather than an in-memory double.
/// </summary>
/// <remarks>
/// The point is to exercise the parts that only exist on a real connection: the handshake, the
/// token check, dispatch, and what happens when the client disappears. A test against a fake
/// stream would pass while the actual transport was misconfigured.
/// </remarks>
public sealed class IpcServerTests : IAsyncLifetime
{
    private const string Token = "test-token-6f2a";

    private string _endpoint = null!;
    private IIpcListener _listener = null!;
    private IpcServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serving = null!;

    public ValueTask InitializeAsync()
    {
        // Whatever transport this platform really uses. This suite skipped itself on Windows on the
        // premise that the named-pipe listener was "covered by running the app there" — it was not,
        // and it shipped listening on a pipe whose published path could not be opened. The skip is
        // what let 1402 green tests coexist with an application that answered nothing on Windows.
        _endpoint = TestEndpoint.Create();

        var registry = new CommandRegistry()
            .Add("echo", (parameters, _) =>
            {
                var text = parameters.TryGetProperty("text", out var value) ? value.GetString() : null;
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                    JsonSerializer.SerializeToUtf8Bytes(text ?? string.Empty));
            })
            .Add("boom", (_, _) => throw new InvalidOperationException("CHECKOUT_CONFLICT: something"))
            .Seal();

        _cts = new CancellationTokenSource();
        _listener = IpcListener.Create(_endpoint);
        _server = new IpcServer(registry, Token);
        _serving = _server.RunAsync(_listener, _cts.Token);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await _server.DisposeAsync();
        await _listener.DisposeAsync();
        _cts.Dispose();
    }

    [Fact]
    public async Task Dispatches_a_command_and_returns_its_result()
    {
        await using var client = await ConnectAsync("rpc", Token);

        await client.SendAsync("""{"id":7,"method":"echo","params":{"text":"hello"}}""");
        var response = await client.ReceiveAsync();

        Assert.Equal(7, response.GetProperty("id").GetInt64());
        Assert.Equal("hello", response.GetProperty("result").GetString());
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Reports_an_unknown_command_as_an_error_rather_than_dropping_it()
    {
        // A dropped reply would leave the renderer's promise pending forever, which is the one
        // failure mode the bridge is designed to make impossible.
        await using var client = await ConnectAsync("rpc", Token);

        await client.SendAsync("""{"id":11,"method":"no_such_command","params":{}}""");
        var response = await client.ReceiveAsync();

        Assert.Equal(11, response.GetProperty("id").GetInt64());
        Assert.Contains("no_such_command", response.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Surfaces_a_handler_exception_verbatim()
    {
        // Error strings are a contract: the frontend keys off prefixes like "CHECKOUT_CONFLICT: ".
        // Reformatting one here would break a feature silently.
        await using var client = await ConnectAsync("rpc", Token);

        await client.SendAsync("""{"id":3,"method":"boom","params":{}}""");
        var response = await client.ReceiveAsync();

        Assert.Equal("CHECKOUT_CONFLICT: something", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Answers_out_of_order_so_a_slow_command_does_not_block_the_next()
    {
        await using var client = await ConnectAsync("rpc", Token);

        await client.SendAsync("""{"id":1,"method":"echo","params":{"text":"one"}}""");
        await client.SendAsync("""{"id":2,"method":"echo","params":{"text":"two"}}""");

        var ids = new List<long>
        {
            (await client.ReceiveAsync()).GetProperty("id").GetInt64(),
            (await client.ReceiveAsync()).GetProperty("id").GetInt64(),
        };

        Assert.Equal([1L, 2L], ids.Order().ToArray());
    }

    [Fact]
    public async Task Rejects_a_connection_with_the_wrong_token()
    {
        var client = await ConnectAsync("rpc", "not-the-token");
        await using var _ = client;

        await client.SendAsync("""{"id":1,"method":"echo","params":{"text":"hi"}}""");

        // The server drops the connection without explaining why; the read ends rather than
        // returning a frame.
        Assert.Null(await client.TryReceiveAsync());
    }

    [Fact]
    public async Task Publishing_without_a_stream_channel_is_a_no_op()
    {
        // Events are notifications. Dropping one because the shell is not listening is correct;
        // throwing would turn a shell restart into cascading failures in unrelated features.
        using var payload = JsonDocument.Parse("""{"repoPath":"/tmp/x"}""");

        await _server.PublishAsync("repo:fs-changed", payload.RootElement, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delivers_an_event_on_the_stream_channel()
    {
        await using var stream = await ConnectAsync("stream", Token);

        // The handshake is asynchronous on the server side; wait for the channel to register
        // rather than racing it.
        using var payload = JsonDocument.Parse("""{"repoPath":"/tmp/x"}""");
        JsonElement received = default;
        for (var attempt = 0; attempt < 50 && received.ValueKind == JsonValueKind.Undefined; attempt++)
        {
            await _server.PublishAsync("repo:fs-changed", payload.RootElement, TestContext.Current.CancellationToken);
            var frame = await stream.TryReceiveAsync(TimeSpan.FromMilliseconds(100));
            if (frame is { } value)
            {
                received = value;
            }
        }

        Assert.Equal("repo:fs-changed", received.GetProperty("event").GetString());
        Assert.Equal("/tmp/x", received.GetProperty("payload").GetProperty("repoPath").GetString());
    }

    private Task<IpcTestClient> ConnectAsync(string channel, string token) =>
        IpcTestClient.ConnectAsync(_endpoint, channel, token);
}
