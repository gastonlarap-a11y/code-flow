namespace CodeFlow.Providers.GitHub;

/// <summary>
/// <see cref="IPullRequestHost"/> over the GitHub REST client.
/// </summary>
/// <remarks>
/// A thin binding of coordinates and a token onto <see cref="GitHubClient"/>'s static functions. The only
/// behaviour that lives here rather than in the client is the mapping from the app's three action names
/// onto GitHub's own model — a review event, or a state change — which is the half of
/// <c>DIVERGENCE-PROV-a</c> that must not be shared with Azure's.
/// </remarks>
internal sealed class GitHubHost(HttpClient http, string host, string owner, string repo, string token)
    : IPullRequestHost
{
    /// <summary>The stand-in when a request-changes review carries no text. `VERBATIM`.</summary>
    /// <remarks>
    /// GitHub rejects a <c>REQUEST_CHANGES</c> review with an empty body, so something has to be sent. This
    /// is 1.7.2's Spanish wording, trailing period included, and it is published to the pull
    /// request where anyone can read it. Azure has no equivalent because a vote carries no message.
    /// </remarks>
    private const string BlankChangesRequested = "Cambios solicitados desde CodeFlow.";

    public Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(CancellationToken cancellationToken) =>
        GitHubClient.ListPullRequestsAsync(http, host, owner, repo, token, cancellationToken);

    public Task<PullRequestSummary> GetPullRequestAsync(long prId, CancellationToken cancellationToken) =>
        GitHubClient.GetPullRequestAsync(http, host, owner, repo, prId, token, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Two requests, in order: GitHub renders the diff itself, so nothing has to be recovered from the
    /// pull request first — unlike Azure, where the read is what supplies the diff's coordinates.
    /// </remarks>
    public async Task<(PullRequestSummary Pr, string Diff)> FetchPullRequestAndDiffAsync(
        long prId, CancellationToken cancellationToken)
    {
        var pr = await GitHubClient.GetPullRequestAsync(http, host, owner, repo, prId, token, cancellationToken)
            .ConfigureAwait(false);

        var diff = await GitHubClient.PullRequestDiffAsync(http, host, owner, repo, prId, token, cancellationToken)
            .ConfigureAwait(false);

        return (pr, diff);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The head SHA is fetched <b>once for the batch, and only when something in it is anchored</b> —
    /// an all-conversation post never touches that endpoint. A failed fetch degrades every anchored
    /// item to a plain comment, silently, which is 1.7.2's behaviour and not a fallback worth
    /// improving here.
    /// </para>
    /// <para>
    /// <b><c>BUG-REVIEW-a</c>, fixed after parity.</b> The SHA fetched here is the <em>current</em>
    /// head, while every anchored item carries line numbers computed from the diff as it stood when
    /// the review ran. If the pull request was pushed to in between, those findings land on whatever
    /// happens to be at those line numbers now — silently, with nothing marking them as misplaced. So
    /// when the caller knows which head the run analysed, the two are compared and a mismatch refuses
    /// the batch rather than posting it. Refusing beats warning: a comment on the wrong line is worse
    /// than no comment, it is read as a reviewer who did not understand the code, and it cannot be
    /// withdrawn without deleting it by hand.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PostOutcome>> PublishFindingsAsync(
        long prId, IReadOnlyList<PostItem> items, string? analysedHeadSha, CancellationToken cancellationToken)
    {
        var anchored = items.Any(item => item.Location is not null);
        var headSha = anchored
            ? await HeadShaOnceAsync(prId, cancellationToken).ConfigureAwait(false)
            : null;

        RefuseIfMoved(analysedHeadSha, headSha, anchored);

        var opened = new OpenedThreads();
        var outcomes = new List<PostOutcome>(items.Count);
        foreach (var item in items)
        {
            var outcome = await PublishAsync(prId, item, headSha, opened.For(item), cancellationToken)
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
        GitHubClient.PostCommentAsync(http, host, owner, repo, prId, content, token, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// GitHub's conversation runs oldest first, so the summary is posted before the findings and
    /// stays above them — the order this code always had, and which is correct here.
    /// </remarks>
    public bool DiscussionNewestFirst => false;

    public Task<IReadOnlyList<PrCommentThread>> ListCommentThreadsAsync(
        long prId, CancellationToken cancellationToken) =>
        GitHubClient.ListCommentThreadsAsync(http, host, owner, repo, prId, token, cancellationToken);

    public Task<string> ViewerDecisionAsync(long prId, CancellationToken cancellationToken) =>
        GitHubClient.ViewerDecisionAsync(http, host, owner, repo, prId, token, cancellationToken);

    public Task<PullRequestSummary> CreatePullRequestAsync(
        string title, string description, string sourceBranch, string targetBranch, bool draft,
        CancellationToken cancellationToken) =>
        GitHubClient.CreatePullRequestAsync(
            http, host, owner, repo, title, description, sourceBranch, targetBranch, draft, token,
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// A decision is a submitted review, except closing, which is a state change on the pull request
    /// itself. The comment is carried on the review, and a blank one is replaced rather than sent empty.
    /// </remarks>
    public Task ActOnAsync(long prId, string action, string comment, CancellationToken cancellationToken) =>
        action switch
        {
            "approve" => GitHubClient.SubmitReviewAsync(
                http, host, owner, repo, prId, "APPROVE", comment, token, cancellationToken),

            "request_changes" => GitHubClient.SubmitReviewAsync(
                http, host, owner, repo, prId, "REQUEST_CHANGES",
                string.IsNullOrWhiteSpace(comment) ? BlankChangesRequested : comment, token, cancellationToken),

            "close" => GitHubClient.CloseAsync(http, host, owner, repo, prId, token, cancellationToken),

            _ => throw new ProviderException($"unknown PR action: {action}"),
        };

    /// <summary>Publishes one item: a new comment, or a follow-up on the one it already has.</summary>
    /// <remarks>
    /// Resolving the thread after a reply is best-effort and deliberately outside the failure this
    /// method reports: a thread left open is a cosmetic loss, and failing the item over it would make
    /// the user re-post a comment that already landed.
    /// </remarks>
    private async Task<PostOutcome> PublishAsync(
        long prId, PostItem item, string? headSha, long? existingComment, CancellationToken cancellationToken)
    {
        try
        {
            if (existingComment is not { } commentId)
            {
                if (item.Location is not { } location || headSha is null)
                {
                    return new PostOutcome.Opened(await GitHubClient.PostCommentAsync(
                        http, host, owner, repo, prId, item.Content, token, cancellationToken)
                        .ConfigureAwait(false));
                }

                try
                {
                    return new PostOutcome.Opened(await GitHubClient.PostCommentAnchoredAsync(
                        http, host, owner, repo, prId, item.Content, location.File,
                        location.StartLine, location.EndLine, headSha, token, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (GitHubException failure) when (IsUnanchorable(failure))
                {
                    // The finding cited lines the diff does not fully contain, so GitHub refuses to
                    // anchor it. Observed live: a *critical* finding cited 68-73 of a file whose
                    // hunk starts at 70, and two lines cost the whole comment — it was reported as a
                    // failed post and simply lost. A model reads the code around a change and cites
                    // what it read; the diff is narrower than that by construction, so this is
                    // ordinary, not exceptional.
                    //
                    // Published unanchored instead, saying where it belonged. Worse than anchored,
                    // and far better than a critical finding nobody ever sees.
                    return new PostOutcome.Opened(await GitHubClient.PostCommentAsync(
                        http, host, owner, repo, prId, Unanchored(item, location), token, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            await GitHubClient.ReplyReviewCommentAsync(
                http, host, owner, repo, prId, commentId, FollowUp(item), token, cancellationToken)
                .ConfigureAwait(false);

            if (item.Resolved)
            {
                try
                {
                    await GitHubClient.ResolveReviewThreadAsync(
                        http, host, owner, repo, prId, commentId, token, cancellationToken).ConfigureAwait(false);
                }
                catch (GitHubException)
                {
                    // The reply is already published; leaving the thread open is not worth failing over.
                }
            }

            return new PostOutcome.Replied();
        }
        catch (GitHubException failure)
        {
            return new PostOutcome.Failed(failure.Message);
        }
    }

    /// <summary>
    /// What a follow-up on an existing thread says.
    /// </summary>
    /// <remarks>
    /// <c>VERBATIM</c>, Spanish, and <b>deliberately different from Azure's</b>: no italics, and no
    /// "Marcado como fixed" suffix, because GitHub has no thread status to mark. Unifying the two
    /// would change what one of the hosts publishes.
    /// </remarks>
    /// <summary>
    /// Whether GitHub refused the anchor rather than the comment.
    /// </summary>
    /// <remarks>
    /// A line outside the diff comes back as <c>422 Unprocessable Entity</c>, which is also what an
    /// otherwise malformed comment returns — so this is matched on the status alone and errs toward
    /// retrying unanchored. The retry posts the same text as a conversation comment: if that fails
    /// too, the item reports the second failure, which is the honest one.
    /// </remarks>
    private static bool IsUnanchorable(GitHubException failure) =>
        failure.Message.Contains("422", StringComparison.Ordinal)
        || failure.Message.Contains("Unprocessable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The finding's text with the location it could not be attached to, stated up front.
    /// </summary>
    /// <remarks>
    /// <c>VERBATIM</c>, Spanish, like every other string this host publishes. The reader has to be
    /// told where to look, because the comment is no longer sitting there.
    /// </remarks>
    private static string Unanchored(PostItem item, CommentLocation location) =>
        FormattableString.Invariant(
            $"📍 `{location.File}` líneas {location.StartLine}-{location.EndLine} — esas líneas no están dentro del diff de este pull request, así que el comentario no pudo anclarse ahí.")
        + "\n\n" + item.Content;

    private static string FollowUp(PostItem item) => item.Resolved
        ? FormattableString.Invariant($"✔️ Resuelto en la iteración {item.Iter} — {item.Today}.")
        : FormattableString.Invariant($"➡️ Sigue presente en la iteración {item.Iter} — {item.Today}.");

    /// <summary>
    /// Marks the refusal above for the renderer, which only ever sees the message string.
    /// </summary>
    /// <remarks>
    /// The same device as <c>CREDENTIAL_REFUSED: </c> and <c>SELF_APPROVAL: </c> — the transport
    /// carries a string and nothing else, and this is a state the UI should offer "review again" for
    /// rather than a plain retry, which would refuse identically. <c>XLANG-014</c>.
    /// </remarks>
    public const string StaleReviewPrefix = "STALE_REVIEW: ";

    /// <summary>The first seven characters, as git and both hosts abbreviate a commit.</summary>
    /// <inheritdoc/>
    public async Task EnsureUnchangedAsync(
        long prId, string? analysedHeadSha, bool anchored, CancellationToken cancellationToken)
    {
        if (!anchored || analysedHeadSha is not { Length: > 0 })
        {
            return;
        }

        RefuseIfMoved(
            analysedHeadSha,
            await HeadShaOnceAsync(prId, cancellationToken).ConfigureAwait(false),
            anchored: true);
    }

    /// <summary>
    /// The head SHA, fetched at most once for the life of this host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The freshness gate and the anchoring both need it, and asking twice was two requests where
    /// the batch used to make one — found by this application reviewing the change that split them
    /// (<c>F-005</c>). One host serves one publish, so the cache lives exactly as long as the
    /// operation does.
    /// </para>
    /// <para>
    /// Reusing the value is also more correct than re-reading it. A second read could return a
    /// different commit than the one the gate approved, which would mean checking one head and
    /// anchoring against another — the window the gate exists to close.
    /// </para>
    /// </remarks>
    private async Task<string?> HeadShaOnceAsync(long prId, CancellationToken cancellationToken)
    {
        if (_headShaFor == prId)
        {
            return _headSha;
        }

        _headSha = await HeadShaOrNullAsync(prId, cancellationToken).ConfigureAwait(false);
        _headShaFor = prId;
        return _headSha;
    }

    private long? _headShaFor;

    private string? _headSha;

    /// <summary>
    /// <c>BUG-REVIEW-a</c>'s refusal, shared by the early check and the batch itself.
    /// </summary>
    /// <remarks>
    /// Only anchored items can land on a wrong line, so an all-conversation post is unaffected. A run
    /// with no recorded SHA — one saved before that was tracked — cannot be checked and is let
    /// through: refusing it would strand old runs with no way to publish at all. The same holds when
    /// the current head could not be read, since a failed fetch already degrades anchoring to plain
    /// comments.
    /// </remarks>
    private static void RefuseIfMoved(string? analysedHeadSha, string? headSha, bool anchored)
    {
        if (!anchored
            || analysedHeadSha is not { Length: > 0 }
            || headSha is not { Length: > 0 }
            || headSha.Equals(analysedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ProviderException(
            $"{StaleReviewPrefix}this pull request has been pushed to since the review ran "
            + $"(analysed {Short(analysedHeadSha)}, now {Short(headSha)}). "
            + "Its findings point at line numbers that have moved, so nothing was posted. "
            + "Run the review again to publish against the current code.");
    }

    private static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];

    /// <summary>The pull request's current head, or null when it cannot be read.</summary>
    private async Task<string?> HeadShaOrNullAsync(long prId, CancellationToken cancellationToken)
    {
        try
        {
            return await GitHubClient.HeadShaForAsync(http, host, owner, repo, prId, token, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubException)
        {
            return null;
        }
    }
}
