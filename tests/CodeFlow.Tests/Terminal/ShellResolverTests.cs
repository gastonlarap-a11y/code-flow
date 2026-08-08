using CodeFlow.Terminal;
using Xunit;

namespace CodeFlow.Tests.Terminal;

/// <summary>
/// Which shell a terminal session runs.
/// See <c>docs/business-rules/11-files-search-terminal.md</c> §Shell selection, <c>FILE-014</c>.
/// </summary>
/// <remarks>
/// the implementation has no extracted cases, so there are no extracted vectors here
/// either — the executable specification is that document's acceptance checklist, and these are its
/// shell-selection bullets.
/// </remarks>
public sealed class ShellResolverTests
{
    [Fact]
    public void A_unix_session_runs_whatever_shell_the_user_has()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows terminals are always Git Bash.");

        using var _ = new TemporaryShell("/usr/bin/fish");

        var choice = ShellResolver.Resolve();

        Assert.Equal("/usr/bin/fish", choice.Executable);
        // No --login -i: that is Git Bash's, and a user's own shell is launched as 1.7.2
        // launches it, bare.
        Assert.Empty(choice.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_unix_session_with_no_shell_set_falls_back_to_bash(string? shell)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows terminals are always Git Bash.");

        using var _ = new TemporaryShell(shell);

        // /bin/bash, not /bin/sh: 1.7.2 picks the shell a user would recognise, and the two
        // differ in which profile they read.
        Assert.Equal("/bin/bash", ShellResolver.Resolve().Executable);
    }

    [Fact]
    public void A_windows_session_is_git_bash_as_a_login_shell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "There is no Git Bash to resolve elsewhere.");
        Assert.SkipWhen(ShellResolver.FindGitBash() is null, "Git for Windows is not installed here.");

        var choice = ShellResolver.Resolve();

        Assert.EndsWith("bash.exe", choice.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["--login", "-i"], choice.Arguments);
    }

    [Fact]
    public void A_windows_machine_without_git_bash_refuses_rather_than_substituting_a_shell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The refusal only exists on Windows.");
        Assert.SkipWhen(ShellResolver.FindGitBash() is not null, "Git for Windows is installed here.");

        var failure = Assert.Throws<InvalidOperationException>(() => ShellResolver.Resolve());

        Assert.Equal(ShellResolver.GitBashMissing, failure.Message);
    }

    [Fact]
    public void The_refusal_message_is_the_reference_string()
    {
        // `VERBATIM`, and asserted on every platform rather than only where it can be triggered:
        // it is the whole of DIVERGENCE-FILE-c that the user ever sees, and the point of the
        // divergence is that this appears instead of a silently different shell.
        Assert.Equal(
            "Git Bash not found — install Git for Windows (https://git-scm.com/download/win)",
            ShellResolver.GitBashMissing);
    }

    /// <summary>Sets <c>SHELL</c> for the duration of one test and puts it back afterwards.</summary>
    /// <remarks>
    /// Process-wide, so nothing else may read the variable concurrently. Nothing else does — this is
    /// its only reader in the whole application.
    /// </remarks>
    private sealed class TemporaryShell : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("SHELL");

        public TemporaryShell(string? shell) => Environment.SetEnvironmentVariable("SHELL", shell);

        public void Dispose() => Environment.SetEnvironmentVariable("SHELL", _previous);
    }
}
