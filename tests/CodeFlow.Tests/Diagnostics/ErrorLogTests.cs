using CodeFlow.Diagnostics;
using Xunit;

namespace CodeFlow.Tests.Diagnostics;

/// <summary>
/// The error log, and the credentials that must never reach it.
/// </summary>
/// <remarks>
/// The log was added so a failed command could be reported instead of retyped, and on its first day
/// it wrote whatever git and the provider APIs put in an exception message — which includes remote
/// URLs carrying tokens. A file whose whole purpose is to be sent to somebody else is the worst
/// possible place for one.
/// </remarks>
public sealed class ErrorLogTests
{
    [Theory]
    // What git prints back when an authenticated fetch fails, token and all.
    [InlineData(
        "fatal: could not read from 'https://gaston:ghp_AbCdEf0123456789xyz@github.com/acme/api.git'",
        "fatal: could not read from 'https://***:***@github.com/acme/api.git'")]
    // A provider echoing the header back inside its own error body. The JSON around it survives:
    // a value that ran to the first space would have swallowed the closing quote and brace.
    [InlineData(
        """GitHub returned 401: {"message":"Bad credentials","header":"Authorization: Bearer ghs_0123456789abcdefghij"}""",
        """GitHub returned 401: {"message":"Bad credentials","header":"Authorization: ***"}""")]
    // A bare scheme, with no header name in front of it.
    [InlineData("azure refused: Bearer eyJhbGciOiJIUzI1NiJ9.abc", "azure refused: Bearer ***")]
    // A token on its own, with nothing around it to give it away.
    [InlineData(
        "azure returned 401 for github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz",
        "azure returned 401 for ***")]
    [InlineData("ssh://git@github.com/acme/api.git is unreachable", "ssh://git@github.com/acme/api.git is unreachable")]
    public void A_credential_never_reaches_the_file(string message, string expected) =>
        Assert.Equal(expected, ErrorLog.Redact(message));

    [Theory]
    // The shape of the failure is what a report needs, and none of it looks like a credential.
    [InlineData("GitHub returned 422 Unprocessable Entity: {\"message\":\"line must be part of the diff\"}")]
    [InlineData("No Azure DevOps token saved for organization \"contoso\" — connect it in Settings first")]
    [InlineData("STALE_REVIEW: the pull request moved to a1b2c3d since this review ran")]
    [InlineData("")]
    public void Everything_that_is_not_a_credential_survives_intact(string message) =>
        Assert.Equal(message, ErrorLog.Redact(message));

    [Fact]
    public void Redaction_happens_before_the_line_reaches_disk()
    {
        // The check that matters: not that `Redact` works, but that `Record` is the caller.
        var directory = Path.Combine(Path.GetTempPath(), "codeflow-errorlog-" + Guid.NewGuid().ToString("N"));

        try
        {
            ErrorLog.Record(
                directory,
                "fetch_pull_requests",
                new InvalidOperationException("remote https://u:ghp_AbCdEf0123456789xyz@github.com/a/b.git refused"));

            var written = File.ReadAllText(ErrorLog.FileIn(directory));

            Assert.DoesNotContain("ghp_", written, StringComparison.Ordinal);
            Assert.Contains("://***:***@github.com/a/b.git", written, StringComparison.Ordinal);
            Assert.Contains("fetch_pull_requests", written, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void The_directory_is_the_caller_s_to_choose()
    {
        // Why this seam exists: without it the suite wrote its fixtures into the user's real
        // ~/CodeFlow/logs, which is the file that exists to tell them what actually went wrong.
        var directory = Path.Combine(Path.GetTempPath(), "codeflow-errorlog-" + Guid.NewGuid().ToString("N"));

        try
        {
            ErrorLog.Record(directory, "boom", new InvalidOperationException("something"));

            Assert.True(File.Exists(ErrorLog.FileIn(directory)));
            Assert.NotEqual(ErrorLog.Path, ErrorLog.FileIn(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
