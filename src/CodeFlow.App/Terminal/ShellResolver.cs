using System.Diagnostics;

namespace CodeFlow.Terminal;

/// <summary>Which shell a new terminal session runs.</summary>
/// <param name="Executable">Absolute path or a name resolvable on PATH.</param>
/// <param name="Arguments">Arguments the shell needs to behave as an interactive login shell.</param>
public readonly record struct ShellChoice(string Executable, string[] Arguments);

/// <summary>
/// Picks the shell for a terminal session.
/// </summary>
/// <remarks>
/// The Windows path is deliberately strict and is documented as <c>DIVERGENCE-FILE-c</c>:
/// terminals are always Git Bash, and PowerShell is <b>not</b> an acceptable fallback. An earlier
/// version of 1.7.2 did fall back silently when <c>bash.exe</c> was missing from one of
/// two hardcoded paths, and handing a user a different shell than the one every command assumes
/// is worse than failing. If Git for Windows genuinely is not installed, that surfaces as a
/// normal command error.
/// </remarks>
public static class ShellResolver
{
    /// <summary>The exact message the frontend shows when Git Bash cannot be found. `VERBATIM`.</summary>
    public const string GitBashMissing =
        "Git Bash not found — install Git for Windows (https://git-scm.com/download/win)";

    /// <summary>How far up from <c>git --exec-path</c> to look for <c>bin\bash.exe</c>.</summary>
    /// <remarks>
    /// <c>--exec-path</c> prints something like <c>&lt;root&gt;\mingw64\libexec\git-core</c>, so the
    /// install root is three levels up; six gives room for layouts that nest differently without
    /// walking to the drive root.
    /// </remarks>
    private const int AncestorWalkDepth = 6;

    private static readonly string[] WindowsFallbacks =
    [
        @"C:\Program Files\Git\bin\bash.exe",
        @"C:\Program Files (x86)\Git\bin\bash.exe",
    ];

    public static ShellChoice Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Note the fallback is /bin/bash, not /bin/sh: 1.7.2 picks a shell the user
            // would recognise, and profile-dependent behaviour differs between the two.
            var shell = Environment.GetEnvironmentVariable("SHELL");
            return new ShellChoice(string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell, []);
        }

        var bash = FindGitBash() ?? throw new InvalidOperationException(GitBashMissing);
        return new ShellChoice(bash, ["--login", "-i"]);
    }

    /// <summary>
    /// Locates Git Bash by asking git where it lives, then walking up to its <c>bin</c>.
    /// </summary>
    /// <remarks>
    /// Asking git rather than guessing is what makes this work for a standard Program Files
    /// install, a custom drive, scoop or chocolatey, and a portable copy alike — the app already
    /// requires <c>git</c> on PATH for clone, fetch, pull and push, so nothing new is assumed.
    /// </remarks>
    internal static string? FindGitBash()
    {
        var execPath = TryGitExecPath();
        if (execPath is not null)
        {
            var directory = new DirectoryInfo(execPath);
            for (var level = 0; level < AncestorWalkDepth && directory is not null; level++)
            {
                var candidate = Path.Combine(directory.FullName, "bin", "bash.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        // Reached two ways, both of which 1.7.2 treats the same: `git --exec-path` did not
        // resolve at all, or it did and no ancestor within the walk carried a bin\bash.exe.
        return WindowsFallbacks.FirstOrDefault(File.Exists);
    }

    private static string? TryGitExecPath()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "--exec-path" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(TimeSpan.FromSeconds(5));

            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not on PATH. The hardcoded fallbacks are the next thing to try.
            return null;
        }
    }
}
