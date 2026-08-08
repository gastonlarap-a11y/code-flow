using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Workspaces;

/// <summary>
/// The review-context roster.
/// </summary>
/// <remarks>
/// A review context is a named block of instructions folded into the review, analyse and chat
/// prompts whenever it is enabled. Consumers filter on <c>enabled</c> and drop the rest silently,
/// so a disabled context is indistinguishable — to the model — from one that never existed. See
/// <c>docs/business-rules/09-workspace-scoped.md</c> §"Review contexts".
/// </remarks>
internal static class ReviewContextStore
{
    private const string Columns = "id, workspace_id, name, content, enabled, created_at";

    /// <summary>A workspace's contexts, in insertion order.</summary>
    /// <remarks>
    /// <c>created_at</c> alone: this table has no <c>sort_order</c> column, so the user cannot rank
    /// contexts the way they can rank agents.
    /// </remarks>
    public static List<ReviewContext> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM review_contexts WHERE workspace_id = $workspaceId ORDER BY created_at",
            Read,
            ("$workspaceId", workspaceId));

    /// <summary>Creates a context, or updates the one already carrying <paramref name="id"/>.</summary>
    /// <remarks>
    /// <para>
    /// The stored <c>created_at</c> survives an edit — the conflict clause rewrites the other four
    /// columns and leaves it alone. A <see langword="null"/> id, or one matching no row, inserts
    /// instead.
    /// </para>
    /// <para>
    /// <b>The returned record carries a freshly stamped <c>created_at</c> on the update branch,
    /// not the stored one.</b> The record is built before the statement runs, so an edit hands the
    /// caller a timestamp that is not what a later list will show. Kept deliberately: the frontend
    /// splices this value
    /// into its local state, and correcting it here would change what the settings screen displays
    /// between an edit and the next refresh.
    /// </para>
    /// </remarks>
    public static ReviewContext Upsert(
        SqliteConnection connection,
        string? id,
        string workspaceId,
        string name,
        string content,
        bool enabled)
    {
        var row = new ReviewContext(
            id ?? Guid.NewGuid().ToString(),
            workspaceId,
            name,
            content,
            enabled,
            Clock.Now());

        Sql.Execute(connection,
            """
            INSERT INTO review_contexts (id, workspace_id, name, content, enabled, created_at)
            VALUES ($id, $workspaceId, $name, $content, $enabled, $createdAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, content = excluded.content, enabled = excluded.enabled
            """,
            ("$id", row.Id),
            ("$workspaceId", row.WorkspaceId),
            ("$name", row.Name),
            ("$content", row.Content),
            ("$enabled", row.Enabled ? 1 : 0),
            ("$createdAt", row.CreatedAt));

        return row;
    }

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM review_contexts WHERE id = $id", ("$id", id));

    private static ReviewContext Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        reader.GetString(5));
}
