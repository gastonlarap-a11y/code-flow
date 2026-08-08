using System.Globalization;
using System.Text;
using CodeFlow.Platform;

namespace CodeFlow.Diagnostics;

/// <summary>
/// Why the sidecar died before it could answer anything.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ErrorLog"/> only ever sees a command that was dispatched, because that is where its one
/// call site lives (<see cref="Ipc.IpcServer"/>'s catch-all). A failure in steps 1–3 of
/// <c>Program.RunAsync</c> — the reset sweep, <see cref="AppPaths.EnsureDirectories"/>,
/// <c>Database.Open</c> and its migrations — happens before an <c>IpcServer</c> exists, so it left no
/// trace at all: the process exited, the shell logged nothing a packaged build keeps, and the user
/// was shown a window whose every button did nothing. That is the shape the first real Windows
/// install reported, and it was undiagnosable by construction.
/// </para>
/// <para>
/// Two sinks, because either can be the only one that works. The file survives the process, which is
/// what a user can be asked for; stderr reaches the shell, which now keeps its own log and is the
/// only sink left if the failure was <em>writing to the log directory</em>.
/// </para>
/// <para>
/// <b>Never throws.</b> The last thing a dying process does must not be to die differently.
/// </para>
/// </remarks>
internal static class StartupLog
{
    /// <summary>The file a user can be pointed at.</summary>
    public static string FileIn(string directory) => Path.Combine(directory, "startup.log");

    /// <summary>
    /// Records a startup stage that threw, then leaves the exception to the caller.
    /// </summary>
    /// <remarks>
    /// The whole <see cref="Exception.ToString"/> rather than just the message: the message of a
    /// failed migration names neither the step nor the table, and the stack is the only thing that
    /// does. It is redacted on the same terms as every other line the app writes.
    /// </remarks>
    public static void Record(string stage, Exception failure) =>
        Record(AppPaths.LogsDirectory, stage, failure);

    /// <summary>Records a failed startup stage into a named directory.</summary>
    /// <remarks>
    /// The directory is a parameter for the same reason <see cref="ErrorLog.Record(string, string, Exception)"/>
    /// takes one: without it the suite files its fixtures in the user's real <c>~/CodeFlow/logs</c>.
    /// </remarks>
    public static void Record(string directory, string stage, Exception failure)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  startup/{stage}  {ErrorLog.Redact(failure.ToString())}");

        WriteToConsole(line);
        if (!WriteTo(directory, line))
        {
            // The log directory is itself under the directory whose creation may be what failed.
            // The temp directory is the one place that is writable when that is true.
            WriteTo(Path.GetTempPath(), line);
        }
    }

    private static void WriteToConsole(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // A closed or redirected stderr. Nothing to do about it, and nothing that justifies
            // replacing the real failure with this one.
        }
    }

    private static bool WriteTo(string directory, string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(FileIn(directory), line + Environment.NewLine, Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
