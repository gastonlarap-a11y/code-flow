using System.Text.Json;
using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tickets;

/// <summary>
/// One finished ticket review, as the renderer receives it.
/// </summary>
/// <param name="Criteria">
/// The parsed criteria table. Stored parsed rather than re-parsed on every read: what it came from is
/// a model's answer, and re-reading it later with a changed parser would silently rewrite history.
/// </param>
/// <param name="Coverage">
/// The coverage block, or <see langword="null"/> when the model never emitted one — which is a real
/// outcome and not an error, so it is representable.
/// </param>
public sealed record TicketReviewResult(
    string Id,
    string ProjectId,
    string TicketId,
    string Branch,
    string BaseRef,
    string HeadSha,
    string Level,
    string ReviewMd,
    IReadOnlyList<TicketCriterionVerdict> Criteria,
    TicketCoverage? Coverage,
    string CreatedAt);

/// <summary>What rides in the row's <c>meta</c> column.</summary>
/// <remarks>
/// The coverage block lives here rather than in <c>coverage_verdict</c> because that column holds the
/// single word the history list filters on; the sentence explaining it is not something to index.
/// <paramref name="Scope"/> joins it for a related reason: <c>base_ref</c> is empty for a
/// working-tree review, so the scope is the only thing that says what was judged.
/// </remarks>
internal sealed record TicketReviewMeta(
    TicketCoverage? Coverage,
    string Provider,
    string Model,
    string Scope);

/// <summary>
/// The finished ticket reviews.
/// </summary>
/// <remarks>
/// A table of its own rather than a row in <c>review_runs</c>, and the reason is structural:
/// <c>review_runs.pr_id</c> is <c>NOT NULL</c> and its index is <c>(project_id, pr_id, created_at)</c>.
/// A pre-commit review has no pull request, so it would need either a fake id — corrupting that index
/// and every reader that treats the column as a real PR — or a nullable column, which is a schema
/// change to the busiest table in the file. The <c>findings</c> column does hold <b>the same JSON
/// shape</b> as <c>review_runs.findings</c>, so <c>FindingCard</c> and <c>parseAnalysis.ts</c> need no
/// branch. See <c>03-storage.md</c> and <c>WI-013</c>.
/// </remarks>
internal static class TicketReviewStore
{
    private const string Columns =
        "id, project_id, ticket_id, branch, base_ref, head_sha, level, review_md, "
        + "criteria, coverage_verdict, meta, created_at";

    /// <summary>Records one finished review under the id the caller already gave it.</summary>
    /// <param name="diff">
    /// What the review actually judged. Written but never read back by any command here: it is what
    /// makes a stored verdict re-checkable months later, and it is the largest column in the table,
    /// which is why <see cref="ForBranch"/> does not select it.
    /// </param>
    public static void Add(
        SqliteConnection connection,
        TicketReviewResult review,
        string workspaceId,
        TicketReviewMeta meta,
        string findingsJson,
        string diff)
    {
        Sql.Execute(connection,
            """
            INSERT INTO ticket_review_runs
                (id, project_id, workspace_id, ticket_id, branch, base_ref, head_sha, level,
                 meta, review_md, diff, findings, criteria, coverage_verdict, created_at)
            VALUES
                ($id, $projectId, $workspaceId, $ticketId, $branch, $baseRef, $headSha, $level,
                 $meta, $reviewMd, $diff, $findings, $criteria, $coverageVerdict, $createdAt)
            """,
            ("$id", review.Id),
            ("$projectId", review.ProjectId),
            ("$workspaceId", workspaceId),
            ("$ticketId", review.TicketId),
            ("$branch", review.Branch),
            ("$baseRef", review.BaseRef),
            ("$headSha", review.HeadSha),
            ("$level", review.Level),
            ("$meta", JsonSerializer.Serialize(meta, TicketJsonContext.Default.TicketReviewMeta)),
            ("$reviewMd", review.ReviewMd),
            ("$diff", diff),
            ("$findings", findingsJson),
            ("$criteria", JsonSerializer.Serialize(
                review.Criteria, TicketJsonContext.Default.IReadOnlyListTicketCriterionVerdict)),
            ("$coverageVerdict", review.Coverage?.Coverage ?? string.Empty),
            ("$createdAt", review.CreatedAt));
    }

    /// <summary>A branch's reviews, newest first.</summary>
    public static List<TicketReviewResult> ForBranch(SqliteConnection connection, string projectId, string branch) =>
        Sql.Query(connection,
            $"""
            SELECT {Columns} FROM ticket_review_runs
            WHERE project_id = $projectId AND branch = $branch
            ORDER BY created_at DESC
            LIMIT 20
            """,
            Read,
            ("$projectId", projectId), ("$branch", branch));

    /// <summary>
    /// Reads a row back, tolerating a payload that will not parse.
    /// </summary>
    /// <remarks>
    /// A stored review whose criteria JSON is unreadable still renders its markdown, which is the
    /// part a person came to read. The alternative — throwing — would take the history list down
    /// with one bad row.
    /// </remarks>
    private static TicketReviewResult Read(SqliteDataReader reader)
    {
        var meta = Deserialize(reader.GetString(10), TicketJsonContext.Default.TicketReviewMeta);

        return new TicketReviewResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            Deserialize(reader.GetString(8), TicketJsonContext.Default.IReadOnlyListTicketCriterionVerdict) ?? [],
            meta?.Coverage,
            reader.GetString(11));
    }

    private static T? Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
