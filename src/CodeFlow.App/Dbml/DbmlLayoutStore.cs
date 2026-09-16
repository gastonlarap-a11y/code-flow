using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Dbml;

/// <summary>The positions a person gave the tables of a schema document (DBML-005).</summary>
/// <remarks>
/// <para>
/// Only what was dragged is stored. A table with no row here is placed by the renderer's
/// auto-layout every time the document renders, which is what lets a newly added table land
/// somewhere sensible instead of at the origin.
/// </para>
/// <para>
/// <b>A position outlives its table.</b> Nothing here prunes rows for tables the document no longer
/// declares: renaming a table and undoing the rename must not cost its position, and the renderer
/// simply ignores keys it has no table for. The only way rows leave is <see cref="Clear"/> — the
/// explicit "arrange everything" — or the cascade when the project is removed.
/// </para>
/// </remarks>
internal static class DbmlLayoutStore
{
    /// <summary>
    /// Ceiling on how many positions one save may carry. Far past any schema a person arranges by
    /// hand, and it keeps the single statement <see cref="Save"/> builds inside SQLite's parameter
    /// limit with room to spare.
    /// </summary>
    public const int MaxPositions = 2_000;

    public static IReadOnlyList<DbmlTablePosition> Load(SqliteConnection connection, string projectId, string relPath) =>
        Sql.Query(connection,
            """
            SELECT table_key, x, y FROM dbml_layouts
            WHERE project_id = $projectId AND rel_path = $relPath
            ORDER BY table_key
            """,
            reader => new DbmlTablePosition(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)),
            ("$projectId", projectId),
            ("$relPath", relPath));

    /// <summary>Stores positions, replacing any a table already had.</summary>
    /// <remarks>
    /// One multi-row <c>INSERT … ON CONFLICT</c> rather than a loop: a single statement is atomic by
    /// itself, so a save that fails part-way leaves the previous layout intact instead of half of the
    /// new one, without a transaction to open, commit and roll back. A key repeated within one save
    /// keeps its last position — the order a drag produced them in.
    /// </remarks>
    public static void Save(
        SqliteConnection connection, string projectId, string relPath, IReadOnlyList<DbmlTablePosition> positions)
    {
        if (positions.Count > MaxPositions)
            throw new ArgumentException($"too many positions in one save: {positions.Count} (the limit is {MaxPositions})");

        if (positions.Any(p => string.IsNullOrWhiteSpace(p.TableKey)))
            throw new ArgumentException("a position is missing its table_key");

        var latest = positions
            .GroupBy(p => p.TableKey, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        if (latest.Count == 0) return;

        var rows = new List<string>(latest.Count);
        var parameters = new List<(string Name, object? Value)>(latest.Count * 4 + 3)
        {
            ("$projectId", projectId),
            ("$relPath", relPath),
            ("$updatedAt", Clock.Now()),
        };

        for (var i = 0; i < latest.Count; i++)
        {
            rows.Add($"($id{i}, $projectId, $relPath, $key{i}, $x{i}, $y{i}, $updatedAt)");
            parameters.Add(($"$id{i}", Guid.NewGuid().ToString()));
            parameters.Add(($"$key{i}", latest[i].TableKey));
            parameters.Add(($"$x{i}", latest[i].X));
            parameters.Add(($"$y{i}", latest[i].Y));
        }

        // Only parameter *names* are interpolated; every value is bound.
        Sql.Execute(connection,
            $"""
            INSERT INTO dbml_layouts (id, project_id, rel_path, table_key, x, y, updated_at)
            VALUES {string.Join(", ", rows)}
            ON CONFLICT(project_id, rel_path, table_key) DO UPDATE SET
                x = excluded.x,
                y = excluded.y,
                updated_at = excluded.updated_at
            """,
            [.. parameters]);
    }

    /// <summary>Forgets every position of one document, so the auto-layout places all of it again.</summary>
    public static void Clear(SqliteConnection connection, string projectId, string relPath) =>
        Sql.Execute(connection,
            "DELETE FROM dbml_layouts WHERE project_id = $projectId AND rel_path = $relPath",
            ("$projectId", projectId),
            ("$relPath", relPath));
}
