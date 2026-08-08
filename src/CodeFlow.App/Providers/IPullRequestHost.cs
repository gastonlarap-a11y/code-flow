namespace CodeFlow.Providers;

/// <summary>
/// One pull-request host, already told which repository it is talking about and how to authenticate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface exists at all.</b> An interface earns its place only with two real
/// implementations. While there was one real provider and one arm that only threw, this would have
/// been the single-implementation interface the codebase rejects; with GitHub and Azure DevOps both
/// real, it pays for itself — the alternative is a two-armed <c>switch</c> repeated in every
/// command. <c>06-providers.md</c> records that the structural difference between the two clients
/// "has no behavioral consequence".
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not unify how the two hosts model a decision, nor
/// how they word a follow-up, nor what closing a thread means to them. <c>DIVERGENCE-PROV-a</c>
/// requires GitHub's review events and Azure's numeric reviewer votes to stay separate, and they do:
/// no shared vote, event or reply text crosses this interface, and each mapping lives inside its own
/// implementation where it cannot be averaged with the other. What is shared is only what the renderer
/// already treats as shared — a pull request, a comment thread, the three-way decision string, and a
/// finding to publish.
/// </para>
/// <para>
/// The coordinates and the credential are constructor state rather than parameters, which is the whole
/// point: it is what lets a command say "list this project's pull requests" once instead of resolving a
/// token, branching on the host and calling a different static function per arm.
/// </para>
/// <para>
/// Reading a pull request's diff arrived with the review pipeline, which is its only caller. It is one
/// member returning both the pull request and its diff rather than two, because that is what the two
/// hosts can actually do in the same number of requests: Azure has to read the pull request first
/// anyway, to recover the canonical project and repository names a link carrying GUIDs does not have.
/// </para>
/// </remarks>
internal interface IPullRequestHost
{
    /// <summary>Every pull request on the repository, in whatever order the host returns them.</summary>
    Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(CancellationToken cancellationToken);

    /// <summary>One pull request as the host reports it now.</summary>
    Task<PullRequestSummary> GetPullRequestAsync(long prId, CancellationToken cancellationToken);

    /// <summary>
    /// The pull request and its unified diff, read from the host's API alone — no clone, no
    /// <c>projects</c> row.
    /// </summary>
    /// <remarks>
    /// This is what a review reached by link alone runs on. A project-backed review never calls it:
    /// it has a working copy, and diffs the target branch against the head ref locally instead.
    /// </remarks>
    Task<(PullRequestSummary Pr, string Diff)> FetchPullRequestAndDiffAsync(
        long prId, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes one batch of selected findings and reports, per item, what it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A batch rather than one call per operation, on purpose.</b> The two hosts diverge in three
    /// places at once — the wording of a follow-up reply, what closing a thread means, and what has to
    /// be fetched before anything is posted — and all three belong to the provider. Splitting them into
    /// <c>OpenComment</c>/<c>Reply</c>/<c>PrepareBatch</c> members would put the reply text and the
    /// prefetch back outside the host, which is exactly what <c>DIVERGENCE-PROV-a</c> says must not
    /// happen, and would cost GitHub one head-SHA request per item instead of one per batch.
    /// </para>
    /// <para>
    /// Every item is attempted regardless of what the ones before it did, so a batch is never left
    /// half-abandoned; a per-item failure comes back as <see cref="PostOutcome.Failed"/> rather than as
    /// an exception. The result is positional: one outcome per item, in order.
    /// </para>
    /// <para>
    /// <c>UNVERIFIED</c> on both implementations — see <c>docs/business-rules/90-ambiguities.md</c>.
    /// </para>
    /// </remarks>
    /// <param name="analysedHeadSha">
    /// The commit the findings' line numbers were computed from, or <see langword="null"/> when the
    /// caller does not know it. <c>BUG-REVIEW-a</c>: a host that anchors against the pull request's
    /// current state compares the two and refuses a batch whose anchors have moved. Azure DevOps
    /// ignores it — it anchors by iteration, not by commit, and has no SHA to compare against.
    /// </param>
    Task<IReadOnlyList<PostOutcome>> PublishFindingsAsync(
        long prId,
        IReadOnlyList<PostItem> items,
        string? analysedHeadSha,
        CancellationToken cancellationToken);

    /// <summary>
    /// Throws if the pull request has moved since the review ran, before anything is posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same refusal <see cref="PublishFindingsAsync"/> makes, asked for early so the summary can
    /// be posted <em>before</em> the findings it summarises — a summary that arrived last read as a
    /// postscript to its own findings, which is the wrong way round for the first thing anyone
    /// opens. Publishing keeps its own check as well, for a caller that skips this one — but it is
    /// the same check against the same cached SHA, so it re-validates nothing.
    /// </para>
    /// <para>
    /// <b>The accepted cost of that ordering</b>: the summary is already posted when the findings are
    /// attempted, so a batch that fails for a reason this check cannot see — a revoked token, a
    /// network drop — leaves a summary describing comments that never appeared. A stale head is not
    /// one of those reasons, since both checks read the same snapshot. Retrying re-posts the summary
    /// rather than replacing it. Narrow, and preferred over a summary that reads as a postscript to
    /// its own conclusions every single time.
    /// </para>
    /// <para>
    /// It costs GitHub <b>nothing extra</b>: the host reads a pull request's head SHA once and reuses
    /// it, so this check and the one inside <see cref="PublishFindingsAsync"/> share a single request
    /// and a single snapshot. Sharing the snapshot is the point, not an optimisation — two reads
    /// could disagree, and then the batch would be judged against a commit the early check never
    /// approved. Azure does nothing here: it anchors by iteration and has no SHA to compare, exactly
    /// as <c>BUG-REVIEW-a</c> records.
    /// </para>
    /// </remarks>
    /// <param name="anchored">
    /// Whether any item in the batch is anchored to a line. Only those can land in the wrong place,
    /// so an all-conversation post is never refused.
    /// </param>
    Task EnsureUnchangedAsync(
        long prId, string? analysedHeadSha, bool anchored, CancellationToken cancellationToken);

    /// <summary>Posts a review's summary as one conversation-level comment.</summary>
    /// <remarks>
    /// Separate from <see cref="PublishFindingsAsync"/> because it genuinely is the same operation on
    /// both hosts — an unanchored comment, with nothing to reconcile and no thread to remember.
    /// <c>UNVERIFIED</c>.
    /// </remarks>
    Task PostSummaryAsync(long prId, string content, CancellationToken cancellationToken);

    /// <summary>The pull request's open comment threads.</summary>
    Task<IReadOnlyList<PrCommentThread>> ListCommentThreadsAsync(long prId, CancellationToken cancellationToken);

    /// <summary>The signed-in user's own decision: <c>approved</c>, <c>changes_requested</c> or <c>none</c>.</summary>
    Task<string> ViewerDecisionAsync(long prId, CancellationToken cancellationToken);

    /// <summary>Opens a pull request.</summary>
    Task<PullRequestSummary> CreatePullRequestAsync(
        string title, string description, string sourceBranch, string targetBranch, bool draft,
        CancellationToken cancellationToken);

    /// <summary>
    /// Approves, requests changes on, or closes the pull request.
    /// </summary>
    /// <param name="action">
    /// <c>approve</c>, <c>request_changes</c> or <c>close</c>. Anything else is an error naming what was
    /// asked for, which is 1.7.2's behaviour and reaches the user verbatim.
    /// </param>
    /// <param name="comment">
    /// <b>Honoured by GitHub and discarded by Azure DevOps.</b> A GitHub review carries a body, and the
    /// API rejects a <c>REQUEST_CHANGES</c> review without one, so a blank comment is replaced with a
    /// default. An Azure decision is a numeric reviewer vote, which has nowhere to put text at all — so
    /// on that host this argument is dropped, exactly as 1.7.2 drops it. That asymmetry is why
    /// the parameter is documented here rather than left to be discovered.
    /// </param>
    Task ActOnAsync(long prId, string action, string comment, CancellationToken cancellationToken);
}
