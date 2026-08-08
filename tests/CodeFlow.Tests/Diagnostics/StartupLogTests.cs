using CodeFlow.Diagnostics;
using Xunit;

namespace CodeFlow.Tests.Diagnostics;

/// <summary>
/// The log that exists because a sidecar which dies before step 4 left no artefact at all.
/// </summary>
/// <remarks>
/// <see cref="ErrorLog"/> is written from <c>IpcServer</c>'s catch-all, so it can only ever see a
/// command that was dispatched. A failure in <c>Program.RunAsync</c>'s steps 1–3 happens before an
/// <c>IpcServer</c> exists: the process exited, the shell's console output was discarded by the
/// packaged build, and the user was shown a window whose every button did nothing. That is the shape
/// the first real Windows install reported, and it was undiagnosable by construction (BOOT-030).
/// </remarks>
public sealed class StartupLogTests
{
    [Fact]
    public void The_stage_name_and_the_whole_exception_reach_the_file()
    {
        // The stack is the point, not the message: a failed migration's message names neither the
        // step nor the table it was working on, and the stack is the only thing that does.
        var directory = FreshDirectory();

        try
        {
            var failure = Caught(() => throw new UnauthorizedAccessException(@"Access to the path 'C:\CodeFlow' is denied."));

            StartupLog.Record(directory, "directories", failure);

            var written = File.ReadAllText(StartupLog.FileIn(directory));
            Assert.Contains("startup/directories", written, StringComparison.Ordinal);
            Assert.Contains(@"Access to the path 'C:\CodeFlow' is denied.", written, StringComparison.Ordinal);
            Assert.Contains(nameof(StartupLogTests), written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_credential_in_a_startup_failure_is_redacted_like_any_other_line()
    {
        // The file is written to be sent to somebody else, which is the whole reason it must not opt
        // out of the redaction the rest of the app applies.
        var directory = FreshDirectory();

        try
        {
            StartupLog.Record(
                directory,
                "storage",
                new InvalidOperationException("could not reach https://u:ghp_AbCdEf0123456789xyz@github.com/acme/api.git"));

            var written = File.ReadAllText(StartupLog.FileIn(directory));
            Assert.Contains("https://***:***@github.com/acme/api.git", written, StringComparison.Ordinal);
            Assert.DoesNotContain("ghp_AbCdEf0123456789xyz", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_directory_that_cannot_be_written_is_not_a_reason_to_throw()
    {
        // This is the case the whole class exists for: the `directories` stage failing is exactly
        // the state in which the log directory does not exist and cannot be made. Recording that
        // must not replace a diagnosable crash with an undiagnosable one — it falls back to the temp
        // directory, and if that fails too it gives up silently.
        var unwritable = Path.Combine(Path.GetTempPath(), "codeflow-startup-\0-" + Guid.NewGuid().ToString("N"));

        StartupLog.Record(unwritable, "directories", new IOException("the disk is full"));
    }

    private static string FreshDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codeflow-startuplog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Exception Caught(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("the action was expected to throw");
    }
}
