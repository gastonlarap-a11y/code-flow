using System.IO.Pipes;
using System.Net.Sockets;

namespace CodeFlow.Ipc;

/// <summary>Accepts connections from the shell. One implementation per platform.</summary>
public interface IIpcListener : IAsyncDisposable
{
    /// <summary>The address the shell was told to connect to.</summary>
    string Endpoint { get; }

    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the right listener for the current platform.</summary>
/// <remarks>
/// Neither a loopback TCP port nor stdio. A listening TCP socket triggers the macOS
/// incoming-connections firewall prompt, can collide with whatever else holds the port, and is
/// reachable by any local process. Stdio has a worse problem: it head-of-line blocks, and the
/// sidecar's own <c>stdout</c>/<c>stderr</c> — runtime warnings, exception traces, anything a
/// spawned child inherits — would corrupt a framed stream sharing those descriptors.
/// </remarks>
public static class IpcListener
{
    public static IIpcListener Create(string endpoint) =>
        OperatingSystem.IsWindows()
            ? new NamedPipeIpcListener(endpoint)
            : new UnixSocketIpcListener(endpoint);
}

/// <summary>Unix domain socket listener, used on macOS.</summary>
/// <remarks>
/// Access control is the socket file's permissions, and the file lives under the app's own base
/// directory. Unlike a named pipe, one listening socket accepts any number of connections, so
/// both channels are served by a single <see cref="Socket"/>.
/// </remarks>
internal sealed class UnixSocketIpcListener : IIpcListener
{
    private readonly Socket _socket;
    private readonly string _path;

    public UnixSocketIpcListener(string path)
    {
        _path = path;

        // A socket file left behind by a crashed run would make Bind fail with "address already
        // in use". The file is not a lock, so removing a stale one is safe and expected.
        DeleteSocketFile(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        _socket.Listen(backlog: 4);

        if (!OperatingSystem.IsWindows())
        {
            // Owner only. Without this the socket inherits the process umask, which on a shared
            // machine can leave it group- or world-writable.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string Endpoint => _path;

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var accepted = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new NetworkStream(accepted, ownsSocket: true);
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        DeleteSocketFile(_path);
        return ValueTask.CompletedTask;
    }

    private static void DeleteSocketFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Bind will fail with a clearer message than anything we could raise here.
        }
    }
}

/// <summary>Named pipe listener, used on Windows.</summary>
/// <remarks>
/// <para>
/// <see cref="NamedPipeServerStream"/> serves <b>one</b> connection per instance. A single
/// instance would accept the first channel and leave the second connect attempt hanging with no
/// error — an easy bug to miss because it looks like a slow start rather than a wrong design. A
/// fresh instance is therefore created for every accept.
/// </para>
/// <para>
/// <b>The endpoint and the pipe name are not the same string, and conflating them is what broke
/// this on Windows.</b> .NET hides the pipe namespace: <see cref="NamedPipeServerStream"/> takes the
/// bare name and prepends <c>\\.\pipe\</c> itself. Node does the opposite — <c>net.connect</c> wants
/// the full path, because it treats a pipe as a filesystem-like address. Handing the full path to
/// the constructor as if it were a name therefore listened somewhere the shell never looked, and the
/// only symptom was <c>connect ENOENT \\.\pipe\codeflow-&lt;pid&gt;</c> after a 15s retry loop, with an
/// application that started, showed its window, and answered nothing.
/// </para>
/// <para>
/// So the published <see cref="Endpoint"/> stays the full path — it is what crosses stdout and what
/// the shell feeds to <c>net.connect</c> unchanged, on both platforms — and the name is derived from
/// it here, at the one place that needs the other form.
/// </para>
/// </remarks>
internal sealed class NamedPipeIpcListener(string endpoint) : IIpcListener
{
    private const int MaxInstances = 4;

    private readonly string _name = PipeNameFrom(endpoint);

    public string Endpoint => endpoint;

    /// <summary>Strips the pipe namespace prefix, leaving the name the constructor wants.</summary>
    /// <remarks>
    /// Both prefixes, because <c>net.connect</c> accepts either and <c>--ipc-endpoint</c> can supply
    /// anything. A value with no prefix is returned untouched: it is already a name.
    /// </remarks>
    internal static string PipeNameFrom(string endpoint)
    {
        foreach (var prefix in (ReadOnlySpan<string>)[@"\\.\pipe\", @"\\?\pipe\"])
        {
            if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return endpoint[prefix.Length..];
            }
        }

        return endpoint;
    }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var server = new NamedPipeServerStream(
            _name,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
