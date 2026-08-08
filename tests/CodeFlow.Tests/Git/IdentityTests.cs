using System.Diagnostics;
using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// The global commit identity (GIT-027).
/// </summary>
/// <remarks>
/// Only the read is covered. <see cref="Identity.Set"/> writes the real <c>~/.gitconfig</c> — the
/// same file the developer's own commits are signed with — and no test may edit that. Redirecting
/// it through <c>GlobalSettings.SetConfigSearchPaths</c> would work, but it is process-global
/// state that every other test opening a repository would then inherit. The write is verified by
/// hand in the running application instead, and that is said here rather than left implied.
/// </remarks>
public sealed class IdentityTests
{
    [Fact]
    public void Reads_the_same_global_identity_the_git_binary_reports()
    {
        // Against `git config --global`, not against a literal: the point is that this reaches the
        // same configuration stack a terminal would, without opening a repository.
        var identity = Identity.Get();

        Assert.Equal(GitConfig("user.name"), identity.Name);
        Assert.Equal(GitConfig("user.email"), identity.Email);
    }

    /// <summary>The configured value, or <c>null</c> when the key is unset — `git` exits 1 for that.</summary>
    private static string? GitConfig(string key)
    {
        using var process = Process.Start(new ProcessStartInfo("git", ["config", "--global", key])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var value = process.StandardOutput.ReadToEnd().TrimEnd('\n', '\r');
        process.WaitForExit();

        return process.ExitCode == 0 && value.Length > 0 ? value : null;
    }
}
