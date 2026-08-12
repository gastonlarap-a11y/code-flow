namespace CodeFlow.Providers.Azure;

/// <summary>
/// <see cref="IPullRequestHost"/> over the Azure DevOps REST client.
/// </summary>
/// <remarks>
/// A thin binding of coordinates and a PAT onto <see cref="AzureClient"/>'s static functions. The only
/// behaviour that lives here rather than in the client is the mapping from the app's three action names
/// onto Azure's own model — a numeric reviewer vote, or abandonment — which is the half of
/// <c>DIVERGENCE-PROV-a</c> that must not be shared with GitHub's.
/// </remarks>
/// <param name="Project">
/// May be a name or a GUID: a pull request reached from a pasted link can carry either, and Azure's Git
/// REST API accepts both. Nothing here needs to know which it is.
/// </param>
internal sealed class AzureHost(HttpClient http, string org, string project, string repoId, string pat)
    : IPullRequestHost
{
    /// <summary>Azure's vote for "approved". Its scale also has 5, approve-with-suggestions, unused here.</summary>
    private const int ApproveVote = 10;

    /// <summary>Azure's vote for "rejected". Its scale also has -5, waiting-for-author, unused here.</summary>
    private const int RejectVote = -10;

    /// <summary>Azure's thread status for "fixed" — how a resolved finding's thread closes.</summary>
    /// <remarks>Its scale also has 3 wontFix, 4 closed, 5 byDesign and 6 pending, none used here.</remarks>
    private const int FixedStatus = 2;

    public Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(CancellationToken cancellationToken) =>
        AzureClient.ListPullRequestsAsync(http, org, project, repoId, pat, cancellationToken);

    public async Task<PullRequestSummary> GetPullRequestAsync(long prId, CancellationToken cancellationToken)
    {
        var detail = await AzureClient.GetPullRequestAsync(http, org, project, repoId, prId, pat, cancellationToken)
            .ConfigureAwait(false);

        // The canonical project and repository names the call also recovered matter only to the link
        // resolution, which asks the client directly. A command reading a pull request wants the summary.
        return detail.Summary;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The diff is fetched against the <em>canonical</em> project and repository names the pull-request
    /// read recovers, not against the coordinates this host was constructed with. A link can name both
    /// by GUID — Azure's own notification e-mails do — and the blobs endpoint the diff is assembled
    /// from wants names.
    /// </remarks>
    public async Task<(PullRequestSummary Pr, string Diff)> FetchPullRequestAndDiffAsync(
        long prId, CancellationToken cancellationToken)
    {
        var detail = await AzureClient.GetPullRequestAsync(http, org, project, repoId, prId, pat, cancellationToken)
            .ConfigureAwait(false);

        var diff = await AzureClient
            .PullRequestDiffAsync(http, org, detail.ProjectName, detail.RepoName, prId, pat, cancellationToken)
            .ConfigureAwait(false);

        return (detail.Summary, diff);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to check: this host has no SHA to compare, for the reason given on
    /// <see cref="PublishFindingsAsync"/>. Empty rather than absent, so the gap stays visible where
    /// GitHub's implementation is — <c>BUG-REVIEW-a</c> is open on this side and is not closed by a
    /// method that merely returns.
    /// </remarks>
    public Task EnsureUnchangedAsync(
        long prId, string? analysedHeadSha, bool anchored, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Nothing is prefetched for the batch: an anchored thread reads the latest iteration itself, per
    /// item, because that is the request 1.7.2 makes.
    /// </para>
    /// <para>
    /// <b><c>BUG-REVIEW-a</c> is still open here, and <paramref name="analysedHeadSha"/> is ignored on
    /// purpose rather than by omission.</b> The same defect exists — the iteration read is the current
    /// one, not the one the review analysed — but Azure anchors by iteration id and never sees a
    /// commit SHA, so the value the caller has is not comparable to anything on this side. Closing it
    /// here means recording the analysed <em>iteration</em> at review time, which is a change to what
    /// a run stores, not to what this method checks. GitHub's half is fixed; this half is named so
    /// nobody reads the two together and assumes both are.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PostOutcome>> PublishFindingsAsync(
        long prId, IReadOnlyList<PostItem> items, string? analysedHeadSha, CancellationToken cancellationToken)
    {
        var opened = new OpenedThreads();

        var outcomes = new List<PostOutcome>(items.Count);
        foreach (var item in items)
        {
            var outcome = await PublishAsync(prId, item, opened.For(item), cancellationToken)
                .ConfigureAwait(false);

            if (outcome is PostOutcome.Opened created)
            {
                opened.Record(item, created.ThreadId);
            }

            outcomes.Add(outcome);
        }

        return outcomes;
    }

    public Task PostSummaryAsync(long prId, string content, CancellationToken cancellationToken) =>
        AzureClient.PostCommentAsync(http, org, project, repoId, prId, content, pat, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Azure's pull-request overview puts the most recent thread at the top, so the summary is
    /// posted <em>after</em> the findings to end up above them. Measured on a real pull request:
    /// posted first, it sat at the bottom under every finding it summarised.
    /// </remarks>
    public bool DiscussionNewestFirst => true;

    public Task<IReadOnlyList<PrCommentThread>> ListCommentThreadsAsync(
        long prId, CancellationToken cancellationToken) =>
        AzureClient.ListCommentThreadsAsync(http, org, project, repoId, prId, pat, cancellationToken);

    public Task<string> ViewerDecisionAsync(long prId, CancellationToken cancellationToken) =>
        AzureClient.ViewerDecisionAsync(http, org, project, repoId, prId, pat, cancellationToken);

    public Task<PullRequestSummary> CreatePullRequestAsync(
        string title, string description, string sourceBranch, string targetBranch, bool draft,
        CancellationToken cancellationToken) =>
        AzureClient.CreatePullRequestAsync(
            http, org, project, repoId, title, description, sourceBranch, targetBranch, draft, pat,
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The comment is discarded.</b> An Azure decision is a number on the caller's reviewer entry, and
    /// there is nowhere in that request to put text — so a comment the user typed into the same form that
    /// GitHub would publish is silently dropped here. That is 1.7.2's behaviour, and the reason the
    /// blank-comment default is GitHub-only rather than shared.
    /// </remarks>
    public Task ActOnAsync(long prId, string action, string comment, CancellationToken cancellationToken) =>
        action switch
        {
            "approve" => AzureClient.SetReviewerVoteAsync(
                http, org, project, repoId, prId, ApproveVote, pat, cancellationToken),

            "request_changes" => AzureClient.SetReviewerVoteAsync(
                http, org, project, repoId, prId, RejectVote, pat, cancellationToken),

            "close" => AzureClient.AbandonPullRequestAsync(
                http, org, project, repoId, prId, pat, cancellationToken),

            _ => throw new ProviderException($"unknown PR action: {action}"),
        };

    /// <summary>Publishes one item: a new thread, or a follow-up on the one it already has.</summary>
    /// <remarks>
    /// Marking the thread fixed after a reply is best-effort and outside the failure this method
    /// reports: the reply already landed, and failing the item over the status would make the user
    /// re-post a comment that is already on the pull request.
    /// </remarks>
    private async Task<PostOutcome> PublishAsync(
        long prId, PostItem item, long? existingThread, CancellationToken cancellationToken)
    {
        try
        {
            if (existingThread is not { } threadId)
            {
                var opened = item.Location is { } location
                    ? await AzureClient.PostCommentAnchoredAsync(
                        http, org, project, repoId, prId, item.Content, location.File,
                        location.StartLine, location.EndLine, pat, cancellationToken).ConfigureAwait(false)
                    : await AzureClient.PostCommentAsync(
                        http, org, project, repoId, prId, item.Content, pat, cancellationToken).ConfigureAwait(false);

                return new PostOutcome.Opened(opened);
            }

            await AzureClient.ReplyThreadAsync(
                http, org, project, repoId, prId, threadId, FollowUp(item), pat, cancellationToken)
                .ConfigureAwait(false);

            if (item.Resolved)
            {
                try
                {
                    await AzureClient.SetThreadStatusAsync(
                        http, org, project, repoId, prId, threadId, FixedStatus, pat, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (AzureException)
                {
                    // The reply is already published; leaving the thread active is not worth failing over.
                }
            }

            return new PostOutcome.Replied();
        }
        catch (AzureException failure)
        {
            return new PostOutcome.Failed(failure.Message);
        }
    }

    /// <summary>
    /// What a follow-up on an existing thread says.
    /// </summary>
    /// <remarks>
    /// <c>VERBATIM</c>, Spanish, and <b>deliberately different from GitHub's</b>: italicised, and the
    /// resolved case names the status this host is about to set. Unifying the two would change what
    /// one of the hosts publishes.
    /// </remarks>
    private static string FollowUp(PostItem item) => item.Resolved
        ? FormattableString.Invariant($"✔️ _Resuelto en la iteración {item.Iter} — {item.Today}. Marcado como fixed._")
        : FormattableString.Invariant($"➡️ _Sigue presente en la iteración {item.Iter} — {item.Today}._");
}
