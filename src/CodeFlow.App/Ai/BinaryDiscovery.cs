using System.Diagnostics;

namespace CodeFlow.Ai;

/// <summary>
/// Finding an AI CLI that <c>PATH</c> alone would not.
/// </summary>
/// <remarks>
/// <para>
/// <c>.claude/rules/dotnet.md</c> calls this "the single most common cause of 'works in my terminal, not
/// in the app'", and it is not hypothetical: a macOS app launched from Finder inherits launchd's
/// minimal <c>PATH</c>, with no Homebrew, no <c>~/.local/bin</c> and no npm global bin; a Windows
/// app already running when a CLI was installed keeps the stale pre-install <c>PATH</c>. So the
/// known installer directories are searched first, and then prepended to the child's own
/// <c>PATH</c> so the CLI's subprocesses (git, node) see the same space.
/// </para>
/// <para>
/// See <c>docs/business-rules/05-ai-engines.md</c> <c>AI-005</c>, <c>AI-006</c>, <c>AI-007</c>.
/// </para>
/// </remarks>
internal static class BinaryDiscovery
{
    /// <summary>Executable extensions tried when resolving a bare name.</summary>
    /// <remarks>
    /// Windows only, and the order is the contract: within one directory a native <c>.exe</c> beats
    /// a <c>.cmd</c>/<c>.bat</c> shim. Across directories, an earlier directory wins regardless of
    /// extension.
    /// </remarks>
    private static readonly string[] WindowsExtensions = ["exe", "cmd", "bat"];

    /// <summary>The directories the AI CLI installers are known to drop binaries in.</summary>
    public static IReadOnlyList<string> InstallDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            if (home.Length > 0)
            {
                dirs.Add(Path.Combine(home, ".local", "bin"));
                dirs.Add(Path.Combine(home, ".claude", "local"));
                dirs.Add(Path.Combine(home, ".opencode", "bin"));
            }

            // npm's global bin on Windows is %APPDATA%\npm — Roaming, not Local.
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (roaming.Length > 0)
            {
                dirs.Add(Path.Combine(roaming, "npm"));
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0)
            {
                dirs.Add(Path.Combine(local, "agy", "bin"));

                // The Codex desktop app ships its CLI here and deliberately does not put it on
                // PATH. Omitting this entry makes a working install probe as "not found".
                dirs.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"));
            }

            return dirs;
        }

        if (home.Length > 0)
        {
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(home, ".claude", "local"));
            dirs.Add(Path.Combine(home, ".opencode", "bin"));
            dirs.Add(Path.Combine(home, ".bun", "bin"));
            dirs.Add(Path.Combine(home, "Library", "pnpm"));
            dirs.Add(Path.Combine(home, ".npm-global", "bin"));
        }

        dirs.Add("/opt/homebrew/bin");
        dirs.Add("/usr/local/bin");
        return dirs;
    }

    /// <summary>The install dirs followed by the process's own <c>PATH</c>, in that order.</summary>
    public static IReadOnlyList<string> SearchDirs()
    {
        var dirs = new List<string>(InstallDirs());
        var current = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(current))
        {
            dirs.AddRange(current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        return dirs;
    }

    /// <summary>Gives the child the augmented search space as its own <c>PATH</c>.</summary>
    public static void ApplyPath(ProcessStartInfo startInfo, IReadOnlyList<string> dirs) =>
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, dirs);

    /// <summary>Turns a bare command name into what actually gets executed.</summary>
    /// <remarks>
    /// <para>
    /// On Windows the extension matters — process creation only auto-appends <c>.exe</c>, so a Node
    /// CLI installed as a <c>&lt;name&gt;.cmd</c> shim, which is how opencode and agy land via npm,
    /// is invisible to a bare name and could not be executed directly anyway. A name that already
    /// carries a separator or an extension is trusted as-is, which is what makes an explicit
    /// <c>binary_path</c> setting bypass all of this (<c>AI-007</c>).
    /// </para>
    /// <para>
    /// <c>XLANG-AI-a</c>. Resolving to a full path is required on <em>every</em> platform, not just
    /// Windows, and the reason is subtle enough to be worth stating: one might assume the augmented
    /// <c>PATH</c> prepared for the child is also the one the binary is looked up in. It is not.
    /// </para>
    /// <para>
    /// <c>Process.Unix.cs</c>'s <c>ResolvePath</c> falls through to
    /// <c>FindProgramInPath</c>, which reads
    /// <c>Environment.GetEnvironmentVariable("PATH")</c> — <em>this</em> process's <c>PATH</c>, not
    /// the <see cref="ProcessStartInfo.Environment"/> being prepared for the child. The augmented
    /// search space reaches the child only after it has started; the lookup happens before. So a
    /// CLI sitting in an install directory that is absent from the app's own <c>PATH</c> probes as
    /// found by <see cref="FindOnPath"/> and then fails to launch — which is exactly what a macOS
    /// app opened from Finder sees, since launchd hands it a minimal <c>PATH</c>.
    /// </para>
    /// <para>
    /// Resolving to an absolute path on every platform closes that gap: launching now searches
    /// where detection searches, which is what <see cref="FindOnPath"/> already claims. Falling
    /// back to the bare name means this can only ever do what it does today or better.
    /// </para>
    /// </remarks>
    public static string ResolveBinary(string binary, IReadOnlyList<string> dirs)
    {
        if (HasPathSeparator(binary) || (OperatingSystem.IsWindows() && Path.HasExtension(binary)))
        {
            return binary;
        }

        // Windows tries the extensions in order within each directory, so a native .exe beats a
        // .cmd shim in the same place; Unix has no extension to append.
        string[] extensions = OperatingSystem.IsWindows() ? WindowsExtensions : [""];

        foreach (var dir in dirs)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, extension.Length == 0 ? binary : $"{binary}.{extension}");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return binary;
    }

    /// <summary>
    /// Locates a binary the same way a run would, for the Settings availability badge.
    /// </summary>
    /// <remarks>
    /// Null means launching it would fail. Two deliberate differences from
    /// <see cref="ResolveBinary"/>: the extensionless candidate is also tried on Windows, because
    /// the badge answers "is it there" rather than "what exactly do I exec"; and a bare name with
    /// an extension is still searched, because <c>claude.exe</c> on its own is a name to look for,
    /// not a path to check.
    /// </remarks>
    public static string? FindOnPath(string binary)
    {
        if (HasPathSeparator(binary) || Path.IsPathRooted(binary))
        {
            return File.Exists(binary) ? binary : null;
        }

        string[] extensions = OperatingSystem.IsWindows() ? [.. WindowsExtensions, ""] : [""];

        foreach (var dir in SearchDirs())
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, extension.Length == 0 ? binary : $"{binary}.{extension}");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Whether the caller named a path rather than something to search for.</summary>
    /// <remarks>
    /// Both separators on both platforms, as 1.7.2 does: a Windows-style path pasted into
    /// the settings field on macOS should still read as a path, not as a filename to hunt for.
    /// </remarks>
    private static bool HasPathSeparator(string binary) =>
        binary.Contains('/', StringComparison.Ordinal) || binary.Contains('\\', StringComparison.Ordinal);
}
