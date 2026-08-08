using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// The SDD/Harness agent roster.
/// </summary>
/// <remarks>
/// An agent is a user-authored role with its own AI routing. When one is active its provider and
/// model <em>replace</em> the per-task routing cascade rather than overriding its result, and its
/// prompt is inserted ahead of every review context. That consumption lives with the AI dispatch
/// (slice 4); this file only stores the roster. The roster starts empty for every workspace and
/// nothing seeds it. See <c>docs/business-rules/09-workspace-scoped.md</c> §"SDD / Harness agent
/// roster".
/// </remarks>
internal static class WorkspaceAgentStore
{
    private const string Columns =
        "id, workspace_id, name, role, provider, model, prompt, enabled, sort_order, created_at";

    public static List<WorkspaceAgent> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM workspace_agents WHERE workspace_id = $workspaceId ORDER BY sort_order, created_at",
            Read,
            ("$workspaceId", workspaceId));

    /// <summary>Creates an agent, or updates the one already carrying <paramref name="id"/>.</summary>
    /// <remarks>
    /// An existing row's <c>sort_order</c> and <c>created_at</c> are read first and carried into
    /// the statement, so neither is lost to an edit. A new row always gets <c>sort_order = 0</c>:
    /// no command in 1.7.2 ever writes a different value, which is why the roster has no
    /// reorder affordance despite the column existing.
    /// </remarks>
    public static WorkspaceAgent Upsert(
        SqliteConnection connection,
        string? id,
        string workspaceId,
        string name,
        string role,
        string provider,
        string model,
        string prompt,
        bool enabled)
    {
        var existing = id is null ? null : Get(connection, id);

        var row = new WorkspaceAgent(
            id ?? Guid.NewGuid().ToString(),
            workspaceId,
            name,
            role,
            provider,
            model,
            prompt,
            enabled,
            existing?.SortOrder ?? 0,
            existing?.CreatedAt ?? Clock.Now());

        Sql.Execute(connection,
            """
            INSERT INTO workspace_agents (
                id, workspace_id, name, role, provider, model, prompt, enabled, sort_order, created_at)
            VALUES (
                $id, $workspaceId, $name, $role, $provider, $model, $prompt, $enabled, $sortOrder, $createdAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, role = excluded.role, provider = excluded.provider,
                model = excluded.model, prompt = excluded.prompt, enabled = excluded.enabled
            """,
            ("$id", row.Id),
            ("$workspaceId", row.WorkspaceId),
            ("$name", row.Name),
            ("$role", row.Role),
            ("$provider", row.Provider),
            ("$model", row.Model),
            ("$prompt", row.Prompt),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$sortOrder", row.SortOrder),
            ("$createdAt", row.CreatedAt));

        return row;
    }

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM workspace_agents WHERE id = $id", ("$id", id));

    private static WorkspaceAgent? Get(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection, $"SELECT {Columns} FROM workspace_agents WHERE id = $id", Read, ("$id", id));

    private static WorkspaceAgent Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetBoolean(7),
        reader.GetInt64(8),
        reader.GetString(9));
}
