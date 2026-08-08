using System.Text;
using Porta.Pty;

namespace CodeFlow.Diagnostics;

/// <summary>
/// Spawns a real pseudo-terminal, drives it, and reads its output back.
/// </summary>
/// <remarks>
/// <para>
/// This is the highest-risk dependency in the port. .NET has no first-party PTY:
/// <c>dotnet/runtime#128565</c> is open against milestone 12.0.0 with no design yet, and
/// Porta.Pty is a single-maintainer package. If it does not work here, the terminal slice has no
/// foundation, and that is a finding to report rather than a problem to work around quietly.
/// </para>
/// <para>
/// The probe deliberately uses the PTY the way the app will: write a command, read the echoed
/// output, resize, and observe exit. It does not assert on exact bytes — a PTY echoes the command
/// itself and may emit escape sequences — only that the shell genuinely ran and its output came
/// back through the pseudo-terminal.
/// </para>
/// </remarks>
internal static class PtyProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<(bool Ok, string Detail)> RunAsync(CancellationToken cancellationToken)
    {
        var marker = $"codeflow-pty-{Guid.NewGuid():N}"[..24];
        var (shell, args) = ResolveShell();

        var options = new PtyOptions
        {
            Name = "codeflow-smoke",
            Cols = 120,
            Rows = 30,
            Cwd = Path.GetTempPath(),
            App = shell,
            CommandLine = args,
            Environment = new Dictionary<string, string>
            {
                // A PTY inherits a login-ish environment; TERM has to be something sane or some
                // shells emit nothing readable at all.
                ["TERM"] = "xterm-256color",
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        IPtyConnection? terminal = null;
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            terminal = await PtyProvider.SpawnAsync(options, timeout.Token).ConfigureAwait(false);
            terminal.ProcessExited += (_, e) => exitCode.TrySetResult(e.ExitCode);

            // Resize before writing: the app resizes on every pane layout change, so a PTY that
            // spawns but cannot be resized is only half a result.
            terminal.Resize(100, 24);

            var command = Encoding.UTF8.GetBytes($"echo {marker}{Environment.NewLine}exit{Environment.NewLine}");
            await terminal.WriterStream.WriteAsync(command, timeout.Token).ConfigureAwait(false);
            await terminal.WriterStream.FlushAsync(timeout.Token).ConfigureAwait(false);

            // Drain to EOF rather than stopping at the marker. A real terminal keeps reading until
            // the child closes its side, and stopping early leaves output buffered — which is
            // also what made exit detection look broken on the first run of this probe.
            var output = await ReadToEndAsync(terminal, timeout.Token).ConfigureAwait(false);
            var saw = output.Contains(marker, StringComparison.Ordinal);

            var exited = await Task.WhenAny(exitCode.Task, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None))
                .ConfigureAwait(false) == exitCode.Task;

            var detail =
                $"shell '{shell}', {output.Length} bytes read to EOF, marker {(saw ? "echoed" : "NOT seen")}, " +
                $"resize ok, exit {(exited ? $"reported (code {exitCode.Task.Result})" : "NOT reported within 5s")}";

            // Exit detection is an explicit requirement of the terminal slice, so a PTY that
            // echoes but never reports exit is a partial failure, not a pass.
            return (saw && exited, detail);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return (false, $"timed out after {Timeout.TotalSeconds:0}s waiting for the PTY");
        }
        finally
        {
            terminal?.Dispose();
        }
    }

    /// <summary>Reads until the child closes its side of the pty, or the token trips.</summary>
    private static async Task<string> ReadToEndAsync(IPtyConnection terminal, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await terminal.ReaderStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The child exited and closed its side of the pty. On Unix this surfaces as an
                // I/O error rather than a clean zero-length read, and it is the normal ending.
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Picks a shell for the probe.
    /// </summary>
    /// <remarks>
    /// Not the app's real resolution logic. CodeFlow 1.7.2 resolves Git Bash on
    /// Windows through <c>git --exec-path</c> plus a six-level ancestor walk and deliberately
    /// refuses to fall back to PowerShell (see <c>docs/business-rules/11-files-search-terminal.md</c>,
    /// <c>DIVERGENCE-FILE-c</c>). That behaviour lives in <see cref="Terminal.ShellResolver"/>;
    /// deliberately not reused here, because this probe has to answer whether a pseudo-terminal can
    /// be created at all even on a machine where the real resolution would refuse.
    /// </remarks>
    private static (string App, string[] CommandLine) ResolveShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", []);
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        return (string.IsNullOrWhiteSpace(shell) ? "/bin/sh" : shell, []);
    }
}
