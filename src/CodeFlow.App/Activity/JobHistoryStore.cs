using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Activity;

/// <summary>
/// Finished PR reviews and pre-commit analyses.
/// </summary>
internal static class JobHistoryStore
{
    private const string Columns =
        "id, project_id, kind, label, custom_label, status, result, error, meta, created_at";

    /// <summary>Records one finished run under the id the caller already gave it.</summary>
    public static JobHistoryEntry Add(
        SqliteConnection connection,
        string id,
        string projectId,
        string kind,
        string label,
        string status,
        string? result,
        string? error,
        string meta)
    {
        var entry = new JobHistoryEntry(
            id, projectId, kind, label, CustomLabel: null, status, result, error, meta, Clock.Now());

        Sql.Execute(connection,
            "INSERT INTO job_history (id, project_id, kind, label, status, result, error, meta, created_at) " +
            "VALUES ($id, $projectId, $kind, $label, $status, $result, $error, $meta, $createdAt)",
            ("$id", entry.Id),
            ("$projectId", entry.ProjectId),
            ("$kind", entry.Kind),
            ("$label", entry.Label),
            ("$status", entry.Status),
            ("$result", entry.Result),
            ("$error", entry.Error),
            ("$meta", entry.Meta),
            ("$createdAt", entry.CreatedAt));

        return entry;
    }

    /// <summary>A project's finished runs, newest first.</summary>
    public static List<JobHistoryEntry> List(SqliteConnection connection, string projectId) =>
        Sql.Query(connection,
            $"SELECT {Columns} FROM job_history WHERE project_id = $projectId ORDER BY created_at DESC",
            Read,
            ("$projectId", projectId));

    /// <summary>Sets the user-chosen label.</summary>
    /// <remarks>
    /// Writes <c>custom_label</c>, never <c>label</c>: the generated one stays, so clearing the
    /// override would restore it.
    /// </remarks>
    public static void Rename(SqliteConnection connection, string id, string label) =>
        Sql.Execute(connection,
            "UPDATE job_history SET custom_label = $label WHERE id = $id",
            ("$label", label),
            ("$id", id));

    /// <summary>Removes a run from history.</summary>
    /// <remarks>
    /// Best-effort by design: deleting a job that is still running has no row to hit yet and affects
    /// zero rows, which is fine — the frontend removes it from memory regardless.
    /// </remarks>
    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM job_history WHERE id = $id", ("$id", id));

    private static JobHistoryEntry Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.TextOrNull(4),
        reader.GetString(5),
        reader.TextOrNull(6),
        reader.TextOrNull(7),
        reader.GetString(8),
        reader.GetString(9));
}
