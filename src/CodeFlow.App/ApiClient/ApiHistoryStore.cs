using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.ApiClient;

/// <summary>The record of every send.</summary>
internal static class ApiHistoryStore
{
    private const string Columns =
        "id, workspace_id, request_id, name, protocol, method, url, status, duration_ms, size_bytes, snapshot, created_at";

    /// <summary>
    /// How many entries one workspace keeps, whatever the UI asks to see.
    /// </summary>
    /// <remarks>
    /// Trimmed on every insert rather than by a background sweep: history rows carry whole
    /// request/response snapshots, so an unbounded table is the one thing here that can grow
    /// without limit.
    /// </remarks>
    private const long HardCap = 2000;

    public static List<ApiHistoryEntry> List(SqliteConnection connection, string workspaceId, long limit) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM api_history WHERE workspace_id = $workspaceId ORDER BY created_at DESC LIMIT $limit",
            Read,
            ("$workspaceId", workspaceId),
            ("$limit", Math.Max(limit, 0)));

    /// <summary>Inserts one send and trims that workspace's history back to <see cref="HardCap"/>.</summary>
    /// <remarks>
    /// <c>ON CONFLICT(id) DO NOTHING</c>: the frontend mints the id, and a retry that re-sends the
    /// same entry must not duplicate it. An empty <c>created_at</c> is stamped here, so a caller
    /// that leaves it blank still sorts correctly.
    /// </remarks>
    public static void Add(SqliteConnection connection, ApiHistoryEntry entry)
    {
        var createdAt = string.IsNullOrWhiteSpace(entry.CreatedAt) ? Clock.Now() : entry.CreatedAt;

        Sql.Execute(connection,
            """
            INSERT INTO api_history
                (id, workspace_id, request_id, name, protocol, method, url, status, duration_ms,
                 size_bytes, snapshot, created_at)
            VALUES ($id, $workspaceId, $requestId, $name, $protocol, $method, $url, $status, $durationMs,
                    $sizeBytes, $snapshot, $createdAt)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", entry.Id),
            ("$workspaceId", entry.WorkspaceId),
            ("$requestId", entry.RequestId),
            ("$name", entry.Name),
            ("$protocol", entry.Protocol),
            ("$method", entry.Method),
            ("$url", entry.Url),
            ("$status", entry.Status),
            ("$durationMs", entry.DurationMs),
            ("$sizeBytes", entry.SizeBytes),
            ("$snapshot", entry.Snapshot),
            ("$createdAt", createdAt));

        Sql.Execute(connection,
            """
            DELETE FROM api_history WHERE workspace_id = $workspaceId AND id NOT IN (
                SELECT id FROM api_history WHERE workspace_id = $workspaceId ORDER BY created_at DESC LIMIT $cap
            )
            """,
            ("$workspaceId", entry.WorkspaceId),
            ("$cap", HardCap));
    }

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM api_history WHERE id = $id", ("$id", id));

    public static void Clear(SqliteConnection connection, string workspaceId) =>
        Sql.Execute(connection,
            "DELETE FROM api_history WHERE workspace_id = $workspaceId", ("$workspaceId", workspaceId));

    private static ApiHistoryEntry Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.TextOrNull(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.GetString(10),
        reader.GetString(11));
}
