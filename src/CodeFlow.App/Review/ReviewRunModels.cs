using CodeFlow.Ai;
using CodeFlow.Git;

namespace CodeFlow.Review;

/// <summary>
/// One saved run as the memory manager lists it — a slim projection with none of the heavy text.
/// </summary>
/// <remarks>
/// Not a stored shape: <c>project_name</c> comes from a join onto <c>projects</c>, <c>pr_title</c>
/// out of the run's own <c>meta</c> JSON, and <c>findings_count</c> from the length of its
/// <c>findings</c> array. A run whose project has since been deleted lists as <c>—</c>.
/// </remarks>
public sealed record ReviewRunSummary(
    string Id,
    string ProjectId,
    string ProjectName,
    long PrId,
    string PrTitle,
    long Iter,
    string Level,
    long FindingsCount,
    string CreatedAt);

/// <summary>The full content of one run, for the in-app viewer and the export.</summary>
/// <remarks>
/// <paramref name="Meta"/> and <paramref name="Findings"/> cross the wire as JSON <em>strings</em>,
/// not as objects — the renderer parses them itself, and that is 1.7.2's shape.
/// <c>workspace_id</c> is deliberately absent: it is on the row but not in this projection.
/// </remarks>
public sealed record ReviewRunDetail(
    string Id,
    string ProjectId,
    long PrId,
    long Iter,
    string Level,
    string Meta,
    string ReviewMd,
    string Diff,
    string Findings,
    string CreatedAt);

/// <summary>Run metadata, written verbatim into <c>review_runs.meta</c> as JSON.</summary>
/// <remarks>
/// <para>
/// Everything the viewer needs to describe a run long after the branch is gone, so none of it is
/// re-derived from a pull request that may no longer exist.
/// </para>
/// <para>
/// <paramref name="HeadSha"/> is what lets the next run detect "nothing changed" and work out which
/// files moved since. It is empty for a run recorded before that was tracked — and, per
/// <c>BUG-REVIEW-a</c>, nothing on the posting side ever reads it.
/// </para>
/// <para>
/// The last three are what a run cost, as numbers. They are also spelled out in the footer under the
/// review, but a sentence is for reading and these are for comparing: answering "did that get
/// cheaper?" used to mean opening the CLI's own session files by hand. All three are null on rows
/// written before they were recorded.
/// </para>
/// <para>
/// <paramref name="OperationMs"/> is the whole operation — fetch, diff, extract, model,
/// reconciliation — and is deliberately <em>not</em> called <c>DurationMs</c>: <c>Usage.DurationMs</c>
/// is the engine's own figure for its own run, one level of nesting away, and the two sat under the
/// same name reporting different things (249 570 against 245 424 on one measured review).
/// </para>
/// </remarks>
public sealed record ReviewMeta(
    long PrId,
    string PrTitle,
    string PrDescription,
    string Author,
    string SourceBranch,
    string TargetBranch,
    string Url,
    string Provider,
    string Level,
    string Engine,
    string Model,
    string ProjectId,
    string ProjectName,
    string WorkspaceId,
    string Timestamp,
    int Iter,
    string HeadSha,
    AiUsage? Usage = null,
    long? OperationMs = null,
    DiffCoverage? Coverage = null);
