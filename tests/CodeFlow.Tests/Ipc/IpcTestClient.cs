using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeFlow.Ipc;
using Xunit;

namespace CodeFlow.Tests.Ipc;

/// <summary>
/// A minimal client speaking the same framing the Electron shell does.
/// </summary>
/// <remarks>
/// Shared rather than duplicated per test class: it is the only thing in the suite that encodes the
/// wire format from the caller's side, so a second copy would be a second place to get the
/// handshake or the length prefix wrong — and a wrong copy fails as a hang, not as a diff.
/// </remarks>
internal sealed class IpcTestClient(Stream stream) : IAsyncDisposable
{
    private readonly PipeReader _reader = PipeReader.Create(stream);
    private readonly PipeWriter _writer = PipeWriter.Create(stream);

    /// <summary>Connects to a listening endpoint and completes the channel handshake.</summary>
    /// <remarks>
    /// Retries the connect: the listener binds during construction, but its accept loop may not
    /// have started by the time a test reaches here.
    /// </remarks>
    public static async Task<IpcTestClient> ConnectAsync(string endpoint, string channel, string token)
    {
        var client = new IpcTestClient(await OpenAsync(endpoint));
        await client.SendAsync($$"""{"channel":"{{channel}}","token":"{{token}}"}""");
        return client;
    }

    /// <summary>
    /// Opens the transport the shell would open, for the platform under test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was a Unix socket unconditionally, and every IPC suite skipped itself on Windows as a
    /// result — with a comment saying the named-pipe listener was "covered by running the app there".
    /// It never was, and the listener shipped passing the full <c>\\.\pipe\…</c> path to
    /// <c>NamedPipeServerStream</c> as if it were a pipe name. Nothing failed until a user launched
    /// it on Windows and got <c>connect ENOENT</c>.
    /// </para>
    /// <para>
    /// <b>The Windows branch opens the published endpoint by its literal path</b>, with
    /// <see cref="FileStream"/> — which is what <c>net.connect</c> does underneath, and the only form
    /// that would have caught that bug. <c>NamedPipeClientStream</c> takes a bare name and derives
    /// the path exactly as the server does, so a test built on it would have agreed with a broken
    /// server and passed.
    /// </para>
    /// </remarks>
    private static async Task<Stream> OpenAsync(string endpoint)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return new FileStream(
                        endpoint,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        useAsync: true);
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), TestContext.Current.CancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (attempt < 50 && ex is SocketException or IOException or UnauthorizedAccessException)
            {
                // The listener binds during construction, but its accept loop may not have started —
                // and on Windows a pipe instance exists only while one is waiting for a connection.
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
    }

    public async Task SendAsync(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await _writer.WriteAsync(header);
        await _writer.WriteAsync(payload);
        await _writer.FlushAsync();
    }

    public async Task<JsonElement> ReceiveAsync() =>
        await TryReceiveAsync() ?? throw new InvalidOperationException("connection closed before a reply arrived");

    public async Task<JsonElement?> TryReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            var frame = await FrameCodec.ReadFrameAsync(_reader, cts.Token);
            if (frame is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(frame);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CompleteAsync();
        await _writer.CompleteAsync();
        await stream.DisposeAsync();
    }
}
