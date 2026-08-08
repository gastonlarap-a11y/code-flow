namespace CodeFlow.Platform;

/// <summary>
/// Every directory and file CodeFlow persists.
/// </summary>
/// <remarks>
/// See <c>docs/business-rules/02-bootstrap-platform.md</c>. The constants here are user-visible
/// locations that an existing CodeFlow 1.7.2 install already uses, so they stay exactly as they
/// are rather than being modernised — changing one strands a user's data.
/// </remarks>
public static class AppPaths
{
    /// <summary>
    /// Root directory holding the database, logs, cloned repositories and workspace skills.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows is literally <c>C:\CodeFlow</c>, not <c>%LOCALAPPDATA%</c>. This is an explicit
    /// product decision in 1.7.2, recorded as <c>DIVERGENCE-BOOT-a</c>, and the NSIS
    /// uninstaller hardcodes the same literal independently. <c>.claude/rules/dotnet.md</c> forbids
    /// hardcoded paths, but changing this one strands every existing Windows user's database and
    /// credentials — so following §9 here would mean writing a migration, not editing a constant.
    /// </para>
    /// <para>
    /// macOS uses <c>~/CodeFlow</c> for the same reason: a fixed, predictable location the
    /// installer's keep-or-wipe prompt can target, and one that never needs elevated permissions.
    /// </para>
    /// </remarks>
    public static string BaseDirectory { get; } = ResolveBaseDirectory();

    public static string DatabaseFile => Path.Combine(BaseDirectory, "codeflow.db");

    public static string LogsDirectory => Path.Combine(BaseDirectory, "logs");

    /// <summary>Default destination for repositories cloned from within the app.</summary>
    public static string CloneRoot => Path.Combine(BaseDirectory, "repos");

    /// <summary>
    /// The marker that asks the <em>next</em> launch to wipe everything.
    /// </summary>
    /// <remarks>
    /// A reset cannot delete the database from under this process's own open connection —
    /// on Windows the file is locked. Requesting a reset drops this marker and quits; startup
    /// checks for it before anything touches the directory.
    /// </remarks>
    public static string ResetMarkerFile => Path.Combine(BaseDirectory, ".reset-pending");

    /// <summary>
    /// Working directories for reviews of pull requests reached by link alone, one per link.
    /// </summary>
    /// <remarks>
    /// Under the application directory rather than in a temporary one, and reused across re-runs of
    /// the same link rather than accumulating. Nothing ever deletes a directory here for a link that
    /// is never revisited — 1.7.2 does not either, and this document cannot establish that
    /// cleanup happens anywhere else.
    /// </remarks>
    public static string PrLinkReviewsDirectory => Path.Combine(BaseDirectory, "pr-link-reviews");

    /// <summary>The canonical, workspace-scoped copy of a workspace's installed skills.</summary>
    public static string WorkspaceSkillsDirectory(string workspaceId) =>
        Path.Combine(BaseDirectory, "workspaces", workspaceId, "skills");

    /// <summary>The generated <c>--mcp-config</c> file for a workspace's enabled MCP servers.</summary>
    /// <remarks>
    /// Rewritten before every run that can use tools, rather than kept in sync with the settings
    /// screen: the file is a derived artefact, and regenerating it is cheaper than reasoning about
    /// when it went stale.
    /// </remarks>
    public static string WorkspaceMcpConfigFile(string workspaceId) =>
        Path.Combine(BaseDirectory, "workspaces", workspaceId, "mcp.json");

    /// <summary>The IPC endpoint the shell connects to.</summary>
    /// <remarks>
    /// On macOS this is a socket file inside the base directory, so its access control is the
    /// directory's own permissions. On Windows it is a named-pipe name, which is not a filesystem
    /// path at all — hence the platform split rather than one path with a different extension.
    /// The process id keeps two concurrently running builds from colliding.
    /// </remarks>
    public static string IpcEndpoint(int processId) =>
        OperatingSystem.IsWindows()
            ? $@"\\.\pipe\codeflow-{processId}"
            : Path.Combine(BaseDirectory, $".ipc-{processId}.sock");

    /// <summary>
    /// Creates the directories the app assumes exist.
    /// </summary>
    /// <remarks>
    /// Only three of the five derived locations are pre-created: the per-workspace skills
    /// directory is made on demand when a skill is installed, and the reset marker is a file.
    /// This must run before the SQLite connection is opened.
    /// </remarks>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CloneRoot);
    }

    private static string ResolveBaseDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"C:\CodeFlow";
        }

        // CodeFlow 1.7.2 falls back to "." when the home directory cannot be resolved, which
        // keeps the app running in a sandbox or a service account rather than failing at startup.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(home) ? "." : home, "CodeFlow");
    }
}
