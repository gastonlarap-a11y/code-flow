using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeFlow.Ipc;
using Porta.Pty;

namespace CodeFlow.Terminal;

/// <summary>
/// The live PTY sessions, and the pump that carries their bytes to the renderer.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no first-party pseudo-terminal — <c>dotnet/runtime#128565</c> is open against
/// milestone 12 with no design — so this sits on Porta.Pty, a small single-maintainer package.
/// That is the reason the terminal was one of the four risk items: if it does not work, the
/// feature has no foundation.
/// </para>
/// <para>
/// Output flows through a <b>bounded</b> channel. An unbounded buffer lets a command like
/// <c>yes</c> grow this process's memory without limit while the renderer falls behind; a bounded
/// one stalls the reader, which stalls draining the OS PTY buffer, which is the backpressure a
/// real terminal already has. The port must not accidentally remove it.
/// </para>
/// </remarks>
public sealed class TerminalRegistry(PublishEvent publish) : IAsyncDisposable
{
    /// <summary>The size a session starts at, matching 1.7.2.</summary>
    private const int InitialRows = 30;
    private const int InitialCols = 100;

    /// <summary>
    /// How many output chunks may be queued before the reader stalls.
    /// </summary>
    /// <remarks>
    /// Deep enough that ordinary bursts never block, shallow enough that a runaway producer is
    /// throttled within a few hundred kilobytes rather than a few hundred megabytes.
    /// </remarks>
    private const int OutputQueueDepth = 64;

    private const string NoSuchSession = "no such terminal session";

    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    /// <summary>Opens a shell in <paramref name="workingDirectory"/> and returns its session id.</summary>
    public async Task<string> OpenAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var shell = ShellResolver.Resolve();

        var connection = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "xterm-256color",
            Cols = InitialCols,
            Rows = InitialRows,
            Cwd = workingDirectory,
            App = shell.Executable,
            CommandLine = shell.Arguments,
            Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color" },
        }, cancellationToken).ConfigureAwait(false);

        var id = Guid.NewGuid().ToString();
        var session = new TerminalSession(id, connection);
        _sessions[id] = session;

        session.Start(publish);
        return id;
    }

    /// <summary>Writes user input into the shell.</summary>
    public async Task WriteAsync(string id, string data, CancellationToken cancellationToken)
    {
        var session = Find(id);
        var bytes = Encoding.UTF8.GetBytes(data);
        await session.Connection.WriterStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await session.Connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resizes the pseudo-terminal after a pane layout change.</summary>
    public void Resize(string id, int cols, int rows) => Find(id).Connection.Resize(cols, rows);

    /// <summary>Closes a session and stops its shell.</summary>
    public async Task CloseAsync(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TerminalSession Find(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : throw new InvalidOperationException(NoSuchSession);

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys)
        {
            await CloseAsync(id).ConfigureAwait(false);
        }
    }
}

/// <summary>One PTY session: the connection, its reader task and its output channel.</summary>
internal sealed class TerminalSession(string id, IPtyConnection connection) : IAsyncDisposable
{
    private readonly Channel<string> _output = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64)
        {
            // Wait, never drop. Dropped terminal output is corrupted output — a missing chunk
            // mid-escape-sequence leaves the emulator in a state the user cannot recover from.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _reader;
    private Task? _writer;

    public IPtyConnection Connection { get; } = connection;

    /// <summary>Starts the reader and the publisher for this session.</summary>
    /// <remarks>
    /// <para>
    /// <c>ProcessExited</c> is deliberately <b>not</b> subscribed to. <c>FILE-015</c> is explicit
    /// that 1.7.2 learns of an exit only by its reader loop ending and never reads the
    /// child's status, and the ordering that gives is the point: the exit is published after the
    /// channel below has drained, so it can never overtake the last <c>terminal:output</c>. That
    /// matters because the renderer writes <c>[process exited]</c> the moment it arrives, and a
    /// user reading that line above the output it followed would be reading a lie.
    /// </para>
    /// <para>
    /// Publishing from the exit event instead was measured here and did <em>not</em> reorder
    /// anything: on macOS, Porta.Pty raises it after the reader has already drained. So this is not
    /// a fix for an observed defect — it is the difference between an ordering that holds because
    /// of how one PTY library happens to sequence an event, and one that holds by construction on
    /// every platform. Windows is unverified, which is exactly why the guarantee should not rest on
    /// the library.
    /// </para>
    /// </remarks>
    public void Start(PublishEvent publish)
    {
        _reader = Task.Run(() => ReadAsync(_lifetime.Token));
        _writer = Task.Run(() => PublishAsync(publish, _lifetime.Token));
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await Connection.ReaderStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // Lossy UTF-8, matching 1.7.2. A multi-byte sequence split across a read
                // boundary produces a replacement character rather than stalling — recorded as
                // AMBIGUOUS-FILE-c, since whether that is acceptable depends on the emulator.
                var text = Encoding.UTF8.GetString(buffer, 0, read);

                // WriteAsync on a full bounded channel is what applies backpressure: it stalls
                // here, which stops draining the OS PTY buffer, which throttles the shell.
                await _output.Writer.WriteAsync(text, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The child closed its side of the pty. On Unix this surfaces as an I/O error rather
            // than a clean zero-length read, and it is the normal ending.
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    private async Task PublishAsync(PublishEvent publish, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using var payload = JsonSerializer.SerializeToDocument(
                    new TerminalOutputEvent(id, chunk), TerminalJsonContext.Default.TerminalOutputEvent);

                await publish("terminal:output", payload.RootElement, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session closed. The exit still goes out below, exactly as 1.7.2 emits one
            // after its loop ends however it ended — including a deliberate close, which kills the
            // child and so ends the loop too.
        }
        finally
        {
            await PublishExitAsync(publish).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Announces that this session's shell is gone. Runs once, after the last output was published.
    /// </summary>
    /// <remarks>
    /// Its own token is <see cref="CancellationToken.None"/>: this is the one message that still has
    /// to reach the renderer when the session is being torn down, and the token that tore it down
    /// would cancel exactly that.
    /// </remarks>
    private async Task PublishExitAsync(PublishEvent publish)
    {
        using var payload = JsonSerializer.SerializeToDocument(
            new TerminalExitEvent(id), TerminalJsonContext.Default.TerminalExitEvent);

        await publish("terminal:exit", payload.RootElement, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Connection.Dispose();

        foreach (var task in new[] { _reader, _writer })
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }
        }

        _lifetime.Dispose();
    }
}

/// <summary>Payload of <c>terminal:output</c>.</summary>
public sealed record TerminalOutputEvent(string Id, string Data);

/// <summary>Payload of <c>terminal:exit</c>.</summary>
public sealed record TerminalExitEvent(string Id);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalOutputEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalExitEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class TerminalJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
