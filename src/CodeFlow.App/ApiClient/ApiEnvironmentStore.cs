using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.ApiClient;

/// <summary>Variable sets scoped to a workspace.</summary>
internal static class ApiEnvironmentStore
{
    private const string Columns = "id, workspace_id, name, variables, is_global, sort_order, created_at";

    /// <summary>One workspace's environments, its own Globals row included.</summary>
    /// <remarks>
    /// Every workspace has a Globals row, seeded by the migration runner with
    /// <c>sort_order = -1</c>, which is what makes it sort first.
    /// </remarks>
    public static List<ApiEnvironment> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM api_environments WHERE workspace_id = $workspaceId ORDER BY sort_order, created_at",
            Read,
            ("$workspaceId", workspaceId));

    public static ApiEnvironment Create(SqliteConnection connection, string workspaceId, string name)
    {
        var row = new ApiEnvironment(
            Guid.NewGuid().ToString(),
            workspaceId,
            name,
            "[]",
            false,
            NextOrder(connection, workspaceId),
            Clock.Now());

        Insert(connection, row);

        return row;
    }

    /// <summary><c>is_global</c> is never written back.</summary>
    /// <remarks>
    /// Which row is the Globals pseudo-environment is the database's business, not something a
    /// client round trip gets to reassign.
    /// </remarks>
    public static void Update(SqliteConnection connection, ApiEnvironment row) =>
        Sql.Execute(connection,
            "UPDATE api_environments SET name = $name, variables = $variables WHERE id = $id",
            ("$id", row.Id),
            ("$name", row.Name),
            ("$variables", row.Variables));

    /// <summary>A no-op on the Globals row: it is always in scope and there is no UI to recreate it.</summary>
    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM api_environments WHERE id = $id AND is_global = 0", ("$id", id));

    /// <summary>Duplicating Globals is allowed and yields an ordinary environment.</summary>
    /// <remarks>
    /// Its variables are a perfectly reasonable starting point for one. The copy stays in the
    /// source's workspace and is never itself global.
    /// </remarks>
    public static ApiEnvironment Duplicate(SqliteConnection connection, string id)
    {
        var source = Sql.QuerySingle(connection,
            $"SELECT {Columns} FROM api_environments WHERE id = $id",
            Read,
            ("$id", id)) ?? throw new InvalidOperationException($"Unknown environment {id}");

        var copy = source with
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{source.Name} copy",
            IsGlobal = false,
            SortOrder = NextOrder(connection, source.WorkspaceId),
            CreatedAt = Clock.Now(),
        };

        Insert(connection, copy);

        return copy;
    }

    private static long NextOrder(SqliteConnection connection, string workspaceId)
    {
        var value = Sql.QueryText(connection,
            "SELECT CAST(COALESCE(MAX(sort_order) + 1, 0) AS TEXT) FROM api_environments WHERE workspace_id = $workspaceId",
            ("$workspaceId", workspaceId));

        return long.TryParse(value, out var order) ? order : 0;
    }

    private static void Insert(SqliteConnection connection, ApiEnvironment row) =>
        Sql.Execute(connection,
            """
            INSERT INTO api_environments (id, workspace_id, name, variables, is_global, sort_order, created_at)
            VALUES ($id, $workspaceId, $name, $variables, $isGlobal, $sortOrder, $createdAt)
            """,
            ("$id", row.Id),
            ("$workspaceId", row.WorkspaceId),
            ("$name", row.Name),
            ("$variables", row.Variables),
            ("$isGlobal", row.IsGlobal ? 1 : 0),
            ("$sortOrder", row.SortOrder),
            ("$createdAt", row.CreatedAt));

    private static ApiEnvironment Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        reader.GetInt64(5),
        reader.GetString(6));
}
