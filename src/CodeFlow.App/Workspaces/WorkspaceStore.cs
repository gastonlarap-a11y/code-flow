using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// Workspace rows and the queries behind them.
/// </summary>
/// <remarks>
/// A workspace groups projects plus everything scoped to it — prompt overrides, review contexts,
/// the agent roster, MCP servers, installed skills and saved review runs. See
/// <c>docs/business-rules/09-workspace-scoped.md</c>.
/// </remarks>
internal static class WorkspaceStore
{
    private const string Columns = "id, name, icon, color, sort_order, created_at, git_name, git_email";

    /// <summary>
    /// Creates a workspace and seeds its two editable prompt overrides.
    /// </summary>
    /// <remarks>
    /// The seeding writes the real built-in text, not blanks, so the settings editor opens on the
    /// actual methodology the user is about to change. It also means the two-level fallback in
    /// <see cref="Settings.GetWorkspacePrompt"/> only ever fires for workspaces created before this
    /// behaviour existed, or for a row the user has since blanked out.
    /// </remarks>
    public static Workspace Create(SqliteConnection connection, string name, string icon, string color)
    {
        var workspace = new Workspace(
            Guid.NewGuid().ToString(), name, icon, color, SortOrder: 0, Clock.Now(),
            GitName: null, GitEmail: null);

        Sql.Execute(connection,
            """
            INSERT INTO workspaces (id, name, icon, color, sort_order, created_at)
            VALUES ($id, $name, $icon, $color, $sortOrder, $createdAt)
            """,
            ("$id", workspace.Id),
            ("$name", workspace.Name),
            ("$icon", workspace.Icon),
            ("$color", workspace.Color),
            ("$sortOrder", workspace.SortOrder),
            ("$createdAt", workspace.CreatedAt));

        foreach (var (kind, content) in Settings.SeededPrompts)
        {
            Sql.Execute(connection,
                """
                INSERT INTO workspace_prompts (workspace_id, kind, content, updated_at)
                VALUES ($workspaceId, $kind, $content, $updatedAt)
                """,
                ("$workspaceId", workspace.Id),
                ("$kind", kind),
                ("$content", content),
                ("$updatedAt", workspace.CreatedAt));
        }

        return workspace;
    }

    /// <summary>Every workspace, in the order the sidebar renders them.</summary>
    /// <remarks>
    /// <c>sort_order</c> then <c>created_at</c>, and the tie-break matters: nothing in the app sets
    /// a non-zero <c>sort_order</c>, so in practice this is creation order compared as text — which
    /// is why <see cref="Clock"/> reproduces 1.7.2's timestamp format exactly.
    /// </remarks>
    public static List<Workspace> List(SqliteConnection connection) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM workspaces ORDER BY sort_order, created_at",
            Read);

    /// <summary>Deletes a workspace and, through the foreign keys, everything hanging off it.</summary>
    /// <remarks>
    /// One statement. The cascade to projects, prompts, review contexts, agents, MCP servers and
    /// skills — and transitively to each project's review runs, activity log, job history and
    /// conversation titles — is SQLite's, which is why <c>PRAGMA foreign_keys</c> is set on the
    /// connection rather than left to the default. Nothing on disk is touched: a project's clone
    /// and the workspace's skills directory outlive the row (<c>WS-002</c>).
    /// </remarks>
    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM workspaces WHERE id = $id", ("$id", id));

    /// <summary>Renames a workspace (WS-009).</summary>
    /// <remarks>
    /// Trimmed, and blank is refused rather than stored. A workspace is chosen from a list by its
    /// name and nothing else — the header menu, the command bar's workspace scope and the settings
    /// list all render exactly that string — so an empty one leaves a row that cannot be told from
    /// its neighbours or picked with confidence. Refusing here rather than only in the UI keeps the
    /// guarantee true for every caller of the transport.
    /// </remarks>
    public static void Rename(SqliteConnection connection, string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("workspace name cannot be blank", nameof(name));
        Sql.Execute(connection, "UPDATE workspaces SET name = $name WHERE id = $id",
            ("$id", id), ("$name", trimmed));
    }

    public static void UpdateColor(SqliteConnection connection, string id, string color) =>
        Sql.Execute(connection, "UPDATE workspaces SET color = $color WHERE id = $id",
            ("$id", id), ("$color", color));

    /// <summary>Sets or clears the workspace's commit-identity override (WS-008).</summary>
    /// <remarks>
    /// Both halves travel together: the caller sends both values to set an override and both
    /// nulls to clear it. A partial pair is stored as sent, and the resolution side discards it
    /// — the same both-or-neither rule <c>Diff.CommitIndex</c> applies (GIT-028).
    /// </remarks>
    public static void UpdateGitIdentity(SqliteConnection connection, string id, string? name, string? email) =>
        Sql.Execute(connection, "UPDATE workspaces SET git_name = $name, git_email = $email WHERE id = $id",
            ("$id", id), ("$name", name), ("$email", email));

    /// <summary>
    /// The commit identity for a repository, found through the project registered at that path.
    /// </summary>
    /// <remarks>
    /// The join is an exact match on <c>local_path</c>: every commit-creating command receives the
    /// path the project row itself supplied, so no normalisation is needed. A repository that is
    /// not a registered project — or whose workspace has no override — resolves to
    /// <c>(null, null)</c>, which the caller reads as "fall back to the global identity". Two
    /// projects sharing one path resolve to the first row, an edge documented in WS-008 rather
    /// than a guarantee.
    /// </remarks>
    public static (string? Name, string? Email) ResolveGitIdentity(SqliteConnection connection, string repoPath) =>
        Sql.Query(connection,
                """
                SELECT w.git_name, w.git_email
                FROM projects p JOIN workspaces w ON w.id = p.workspace_id
                WHERE p.local_path = $repoPath
                LIMIT 1
                """,
                reader => (reader.TextOrNull(0), reader.TextOrNull(1)),
                ("$repoPath", repoPath))
            .FirstOrDefault();

    private static Workspace Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        reader.GetString(5),
        reader.TextOrNull(6),
        reader.TextOrNull(7));
}
