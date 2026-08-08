using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Review;

/// <summary>
/// The <c>review_runs</c> table — durable memory of every completed pull-request review, from the
/// review-run tables.
/// </summary>
/// <remarks>
/// <para>
/// Rows are timestamped and never overwritten, so the code a finding referred to stays recoverable
/// after the branch is gone. <c>STORE-013</c>: <see cref="Add"/> is idempotent by id and only
/// <see cref="SetFindings"/> ever touches an existing row — <c>review_md</c>, <c>diff</c> and
/// <c>meta</c> are immutable once written.
/// </para>
/// <para>
/// <c>workspace_id</c> is a write-time copy with no foreign key, so it is only as true as its
/// upkeep: <c>move_project_to_workspace</c> updates it in the same transaction as the project row
/// (this closed <c>BUG-STORE-b</c> — 1.7.2 left it stale, and a moved project's history fell out
/// of <see cref="List"/> for its new workspace while staying deletable by <see cref="Purge"/>
/// scoped to the old one), and a migration step repairs databases that diverged before the fix.
/// </para>
/// </remarks>
internal static class ReviewRunStore
{
    /// <summary>How many runs this pull request already has, which is what numbers the next one.</summary>
    public static long Count(SqliteConnection connection, string projectId, long prId) =>
        Sql.Query(connection,
            "SELECT COUNT(*) FROM review_runs WHERE project_id = $projectId AND pr_id = $prId",
            reader => reader.GetInt64(0),
            ("$projectId", projectId),
            ("$prId", prId))[0];

    /// <summary>The newest run's <c>findings</c> JSON for this pull request, to reconcile against.</summary>
    public static string? LatestFindings(SqliteConnection connection, string projectId, long prId) =>
        Sql.QueryText(connection,
            "SELECT findings FROM review_runs WHERE project_id = $projectId AND pr_id = $prId "
            + "ORDER BY created_at DESC LIMIT 1",
            ("$projectId", projectId),
            ("$prId", prId));

    /// <summary>
    /// The head commit SHA the newest run was analysed against, out of its <c>meta</c> JSON.
    /// </summary>
    /// <remarks>
    /// This is what detects "nothing changed since the last review". An empty stored value answers
    /// <see langword="null"/>, the same as no run at all — a run recorded before the SHA was
    /// tracked must not short-circuit anything.
    /// </remarks>
    public static string? LatestHead(SqliteConnection connection, string projectId, long prId) =>
        Sql.QueryText(connection,
            "SELECT json_extract(meta, '$.head_sha') FROM review_runs "
            + "WHERE project_id = $projectId AND pr_id = $prId ORDER BY created_at DESC LIMIT 1",
            ("$projectId", projectId),
            ("$prId", prId)) is { Length: > 0 } head ? head : null;

    /// <summary>
    /// The head commit SHA <em>one specific run</em> was analysed against, out of its <c>meta</c> JSON.
    /// </summary>
    /// <remarks>
    /// <see cref="LatestHead"/> answers "has anything changed since we last reviewed this pull
    /// request"; this answers "is the code still what <em>this</em> run's line numbers were computed
    /// from", which is what the posting flow has to know (<c>BUG-REVIEW-a</c>). An empty stored value
    /// answers <see langword="null"/>, the same as no run at all — a run recorded before the SHA was
    /// tracked cannot be checked, and must not be blocked for it.
    /// </remarks>
    public static string? HeadFor(SqliteConnection connection, string runId) =>
        Sql.QueryText(connection,
            "SELECT json_extract(meta, '$.head_sha') FROM review_runs WHERE id = $id",
            ("$id", runId)) is { Length: > 0 } head ? head : null;

    /// <summary>Records one completed run.</summary>
    /// <remarks>
    /// <paramref name="id"/> reuses the job's own id, so the run and its <c>job_history</c> row
    /// share identity. A retry with that same id is a silent no-op, not a second row.
    /// </remarks>
    public static void Add(
        SqliteConnection connection,
        string id,
        string projectId,
        string workspaceId,
        long prId,
        int iter,
        string level,
        string meta,
        string reviewMarkdown,
        string diff,
        string findings) =>
        Sql.Execute(connection,
            "INSERT INTO review_runs "
            + "(id, project_id, workspace_id, pr_id, iter, level, meta, review_md, diff, findings, created_at) "
            + "VALUES ($id, $projectId, $workspaceId, $prId, $iter, $level, $meta, $reviewMd, $diff, $findings, $createdAt) "
            + "ON CONFLICT(id) DO NOTHING",
            ("$id", id),
            ("$projectId", projectId),
            ("$workspaceId", workspaceId),
            ("$prId", prId),
            ("$iter", iter),
            ("$level", level),
            ("$meta", meta),
            ("$reviewMd", reviewMarkdown),
            ("$diff", diff),
            ("$findings", findings),
            ("$createdAt", Clock.Now()));

    /// <summary>Every saved run in a workspace, across its projects, newest first.</summary>
    public static List<ReviewRunSummary> List(SqliteConnection connection, string workspaceId) =>
        Sql.Query(connection,
            "SELECT r.id, r.project_id, COALESCE(p.name, '—'), r.pr_id, "
            + "COALESCE(json_extract(r.meta, '$.pr_title'), ''), "
            + "r.iter, r.level, "
            + "COALESCE(json_array_length(r.findings), 0), "
            + "r.created_at "
            + "FROM review_runs r "
            + "LEFT JOIN projects p ON p.id = r.project_id "
            + "WHERE r.workspace_id = $workspaceId "
            + "ORDER BY r.created_at DESC",
            reader => new ReviewRunSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetString(8)),
            ("$workspaceId", workspaceId));

    /// <summary>The full content of one run, or <see langword="null"/> if there is no such row.</summary>
    public static ReviewRunDetail? Get(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection,
            "SELECT id, project_id, pr_id, iter, level, meta, review_md, diff, findings, created_at "
            + "FROM review_runs WHERE id = $id",
            reader => new ReviewRunDetail(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9)),
            ("$id", id));

    /// <summary>Overwrites a run's <c>findings</c> JSON. The only mutation an existing row allows.</summary>
    public static void SetFindings(SqliteConnection connection, string id, string findings) =>
        Sql.Execute(connection,
            "UPDATE review_runs SET findings = $findings WHERE id = $id",
            ("$id", id),
            ("$findings", findings));

    public static void Delete(SqliteConnection connection, string id) =>
        Sql.Execute(connection, "DELETE FROM review_runs WHERE id = $id", ("$id", id));

    /// <summary>Deletes every saved run of one pull request.</summary>
    public static void DeleteForPr(SqliteConnection connection, string projectId, long prId) =>
        Sql.Execute(connection,
            "DELETE FROM review_runs WHERE project_id = $projectId AND pr_id = $prId",
            ("$projectId", projectId),
            ("$prId", prId));

    /// <summary>Wipes all saved review memory for a workspace.</summary>
    public static void Purge(SqliteConnection connection, string workspaceId) =>
        Sql.Execute(connection,
            "DELETE FROM review_runs WHERE workspace_id = $workspaceId",
            ("$workspaceId", workspaceId));
}
