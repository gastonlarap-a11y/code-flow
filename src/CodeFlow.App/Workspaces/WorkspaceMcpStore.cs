using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// The per-workspace MCP server list.
/// </summary>
/// <remarks>
/// Stored exactly as typed: <c>args</c> as one space-separated string, <c>env</c> as
/// <c>KEY=value</c> lines. The AI dispatch re-splits both when it writes a run's <c>mcp.json</c>
/// (slice 4), and only enabled rows reach that file. Parsing them here would change what a
/// round-trip through the settings screen preserves. See
/// <c>docs/business-rules/09-workspace-scoped.md</c> §"MCP servers".
/// </remarks>
internal static class WorkspaceMcpStore
{
    private const string Columns = "id, workspace_id, name, command, args, env, enabled, created_at";

    public static List<WorkspaceMcp> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM workspace_mcps WHERE workspace_id = $workspaceId ORDER BY created_at",
            Read,
            ("$workspaceId", workspaceId));

    /// <inheritdoc cref="ReviewContextStore.Upsert"/>
    public static WorkspaceMcp Upsert(
        SqliteConnection connection,
        string? id,
        string workspaceId,
        string name,
        string command,
        string args,
        string env,
        bool enabled)
    {
        var row = new WorkspaceMcp(
            id ?? Guid.NewGuid().ToString(),
            workspaceId,
            name,
            command,
            args,
            env,
            enabled,
            Clock.Now());

        Sql.Execute(connection,
            """
            INSERT INTO workspace_mcps (id, workspace_id, name, command, args, env, enabled, created_at)
            VALUES ($id, $workspaceId, $name, $command, $args, $env, $enabled, $createdAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, command = excluded.command, args = excluded.args,
                env = excluded.env, enabled = excluded.enabled
            """,
            ("$id", row.Id),
            ("$workspaceId", row.WorkspaceId),
            ("$name", row.Name),
            ("$command", row.Command),
            ("$args", row.Args),
            ("$env", row.Env),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$createdAt", row.CreatedAt));

        return row;
    }

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM workspace_mcps WHERE id = $id", ("$id", id));

    private static WorkspaceMcp Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetBoolean(6),
        reader.GetString(7));
}
