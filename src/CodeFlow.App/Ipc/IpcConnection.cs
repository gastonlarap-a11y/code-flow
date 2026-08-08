using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeFlow.Ipc;

/// <summary>
/// One framed connection to the shell.
/// </summary>
/// <remarks>
/// Writes are serialised behind a semaphore. Two frames interleaved on one ordered stream would
/// corrupt both, and the stream channel has several concurrent producers by design — PTY output
/// and every event source push through the same connection.
/// </remarks>
public sealed class IpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IpcConnection(Stream stream)
    {
        _stream = stream;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>Which channel this connection is, once the opening frame has said so.</summary>
    /// <remarks>
    /// Assigned after the handshake rather than passed to the constructor, because the handshake
    /// has to be read through <em>this</em> connection's reader. Creating a throwaway connection
    /// to peek at the hello frame and then a second one over the same stream loses whatever the
    /// first reader had already buffered — which, since a client can pipeline its first request
    /// immediately behind the hello, silently swallows it.
    /// </remarks>
    public IpcChannelKind Kind { get; private set; }

    internal void AssignKind(IpcChannelKind kind) => Kind = kind;

    /// <summary>
    /// Trips when this connection drops.
    /// </summary>
    /// <remarks>
    /// Every operation started on behalf of the shell links its own token to this one. If the
    /// shell dies, its AI subprocesses and PTYs must die with it — in 1.7.2 the whole
    /// process tree went down together, and splitting the process is what makes this explicit
    /// work rather than something the OS did for free.
    /// </remarks>
    public CancellationTokenSource Lifetime { get; } = new();

    public ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellationToken) =>
        FrameCodec.ReadFrameAsync(_reader, cancellationToken);

    /// <summary>Serialises a value and writes it as one frame.</summary>
    public async ValueTask SendAsync<T>(T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await SendRawAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes an already-serialised frame.</summary>
    public async ValueTask SendRawAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_writer, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Lifetime.IsCancellationRequested)
        {
            await Lifetime.CancelAsync().ConfigureAwait(false);
        }

        await _reader.CompleteAsync().ConfigureAwait(false);
        await _writer.CompleteAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);

        Lifetime.Dispose();
        _writeLock.Dispose();
    }
}
