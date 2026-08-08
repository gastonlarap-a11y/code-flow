using System.Globalization;
using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using CodeFlow.Providers.GitHub;
using CodeFlow.Storage;
using CodeFlow.Workspaces;

namespace CodeFlow.Review;

/// <summary>
/// Publishing a review's selected findings back onto the pull request, from
/// </summary>
/// <remarks>
/// <para>
/// One finding keeps one thread for the pull request's whole life: never posted → open one; posted and
/// still present → a follow-up on it; now resolved → a follow-up plus the thread closed. New thread ids
/// are written back onto the run so a later post continues the same conversations instead of
/// duplicating them.
/// </para>
/// <para>
/// <b>Every write here is <c>UNVERIFIED</c></b> — <c>docs/business-rules/90-ambiguities.md</c> records that none of these
/// paths has ever run against a real Azure DevOps or GitHub API, in this port or in 1.7.2. They
/// compile and are covered against a fake transport; that is all that can honestly be claimed.
/// </para>
/// </remarks>
internal static class ReviewPosting
{
    /// <summary>Publishes selected findings from a saved run, reconciling against what it already posted.</summary>
    /// <remarks>
    /// A <paramref name="runId"/> nothing matches is <b>not</b> an error: it resolves to no stored
    /// findings and iteration 1, so every item posts as a brand-new thread. CodeFlow 1.7.2 chose that
    /// over failing, and it is what makes a post survive a deleted run.
    /// </remarks>
    public static async Task PublishAsync(
        Database database,
        HttpClient http,
        string projectId,
        long prId,
        string runId,
        IReadOnlyList<PostFindingItem> items,
        bool postSummary,
        string? summary,
        CancellationToken cancellationToken)
    {
        var (host, findings, iter, analysedHead) = await database.ReadAsync(
            c =>
            {
                var project = ProjectStore.Get(c, projectId) ?? throw new ProviderException("Project not found");
                var run = ReviewRunStore.Get(c, runId);

                return (
                    PullRequestHosts.For(http, LinkedRepo.Resolve(project)),
                    run is null ? [] : Stored(run.Findings),
                    run?.Iter ?? 1,
                    // BUG-REVIEW-a: the commit these findings' line numbers were computed from. Null
                    // for a run saved before that was tracked, which the host reads as "cannot check".
                    ReviewRunStore.HeadFor(c, runId));
            },
            cancellationToken).ConfigureAwait(false);

        var today = Today();
        var failures = new List<string>();

        // Each item carries its identity as well as its thread, so the host can link an item to a
        // thread opened earlier in the same batch. Since BUG-REVIEW-b was fixed that linking only
        // happens between items that genuinely refer to the same stored finding: two *different*
        // findings that merely collide on the identity key now claim separate rows, and the one left
        // over opens its own thread rather than replying into a conversation about something else.
        var prepared = new List<PostItem>(items.Count);
        var matches = new List<int?>(items.Count);

        // BUG-REVIEW-b's posting half: which stored findings are already spoken for. Without it two
        // selected findings sharing a file and a category both resolved to the same stored row, and
        // the second replied into the first's thread — two unrelated findings in one conversation.
        var claimed = new HashSet<int>();

        foreach (var item in items)
        {
            var index = IndexOf(findings, item, claimed);
            if (index is { } taken)
            {
                claimed.Add(taken);
            }

            matches.Add(index);
            prepared.Add(new PostItem(
                item.Content,
                item.Location,
                index is { } k ? findings[k].ThreadId : null,
                index is { } r && findings[r].Estado == MemoryFinding.Resolved,
                iter,
                today,
                // Null when nothing matched: an unmatched item is never linked to another, because the
                // reference does not record its thread either.
                index is null ? null : ReviewMemory.FindingIdentity(item.File, item.Category)));
        }

        // Summary first, and the refusal before it.
        //
        // A review's summary is the thing anyone opens the pull request to read, and posting it last
        // put it under the findings it summarises — a postscript to its own conclusions. Posting it
        // first only works if the batch is known to be publishable: a summary announcing findings
        // that a stale-head refusal then blocks would describe comments nobody can see. So the
        // freshness check moves ahead of both.
        //
        // `PublishFindingsAsync` checks again, and that second check is **not** a re-validation: the
        // host reads the head SHA once per pull request and reuses it, so both checks see the same
        // snapshot. That is deliberate — a second read could disagree with the one the gate
        // approved, and then the batch would be judged against a commit nobody decided on. The
        // second check is there for a caller that skips this one, not to catch a push that lands in
        // between. An earlier version of this comment claimed otherwise, and the review that read it
        // was right to call it out.
        await host.EnsureUnchangedAsync(
            prId, analysedHead, prepared.Any(item => item.Location is not null), cancellationToken)
            .ConfigureAwait(false);

        await SummaryAsync(host, prId, postSummary, summary, failures, cancellationToken).ConfigureAwait(false);

        var outcomes = await host.PublishFindingsAsync(prId, prepared, analysedHead, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < outcomes.Count; i++)
        {
            Apply(findings, matches[i], outcomes[i], i, failures);
        }

        // Unconditional, and before the aggregate error: whatever did post has to be remembered, or a
        // retry would open a second thread for every finding that already succeeded.
        await database.WriteAsync(
            c => ReviewRunStore.SetFindings(
                c, runId, JsonSerializer.Serialize(findings, ReviewJsonContext.Default.ListMemoryFinding)),
            cancellationToken).ConfigureAwait(false);

        Report(failures);
    }

    /// <summary>Publishes selected findings from a review reached by link alone.</summary>
    /// <remarks>
    /// There is no saved run to reconcile against — a review with no project has nowhere to keep its
    /// memory — so <b>every finding opens a fresh thread every time</b>, however often the same link is
    /// posted. Nothing is written back, because there is nothing to write it to.
    /// </remarks>
    public static async Task PublishFromLinkAsync(
        Database database,
        HttpClient http,
        string url,
        IReadOnlyList<PostFindingItem> items,
        bool postSummary,
        string? summary,
        CancellationToken cancellationToken)
    {
        var target = await database.ReadAsync(c => PrLink.Parse(url, KnownHosts.ForGitHub(c)), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProviderException("That isn't a pull-request link CodeFlow can read");

        var (host, number) = PullRequestHosts.For(http, target);
        var today = Today();
        var failures = new List<string>();

        // Summary first here too, for the reason given in `PublishAsync`. No freshness check to run
        // ahead of it: a link review has no saved run, so there is no analysed head to compare
        // against and BUG-REVIEW-a's refusal cannot fire.
        await SummaryAsync(host, number, postSummary, summary, failures, cancellationToken).ConfigureAwait(false);

        // No thread and nothing resolved on any of them, which is what reduces the same interface
        // member to "open one comment per finding".
        var outcomes = await host.PublishFindingsAsync(
            number,
            [.. items.Select(item => new PostItem(
                item.Content, item.Location, ExistingThreadId: null, Resolved: false, Iter: 1, today))],
            // The window is also far smaller: this posts findings from a review that just ran in
            // this session, not from one stored days ago.
            analysedHeadSha: null,
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i] is PostOutcome.Failed failed)
            {
                failures.Add(Numbered(i, failed.Message));
            }
        }

        Report(failures);
    }

    /// <summary>
    /// The stored finding an item refers to, by file and category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same identity <c>ReviewMemory.Reconcile</c> keys on, and <c>BUG-REVIEW-b</c> applied here
    /// too until it was fixed after parity: the key is not injective, so two selected findings
    /// sharing a file and a category both resolved to the first stored row, and the second replied on
    /// the first's thread instead of opening its own.
    /// </para>
    /// <para>
    /// <paramref name="claimed"/> holds the rows earlier items in this pass already took, which makes
    /// the matching one-to-one. An item that finds only claimed rows matches nothing and posts a
    /// fresh thread — the same answer an item with no stored counterpart at all gets.
    /// </para>
    /// </remarks>
    private static int? IndexOf(List<MemoryFinding> findings, PostFindingItem item, HashSet<int> claimed)
    {
        var key = ReviewMemory.FindingIdentity(item.File, item.Category);
        var index = findings.FindIndex(f => ReviewMemory.FindingIdentity(f.Archivo, f.Categoria) == key);

        while (index >= 0 && claimed.Contains(index))
        {
            index = findings.FindIndex(
                index + 1, f => ReviewMemory.FindingIdentity(f.Archivo, f.Categoria) == key);
        }

        return index < 0 ? null : index;
    }

    /// <summary>Records what one item did on the finding it belongs to.</summary>
    /// <remarks>
    /// An <b>unmatched</b> item whose post succeeded silently loses its thread id — the host now has a
    /// comment this application has no record of, and a later post would open another. It takes an item
    /// whose file and category match nothing in the run, which the panel's own findings list cannot
    /// produce, so 1.7.2 leaves it as an edge case rather than a marker.
    /// </remarks>
    private static void Apply(
        List<MemoryFinding> findings, int? index, PostOutcome outcome, int position, List<string> failures)
    {
        switch (outcome)
        {
            case PostOutcome.Opened opened when index is { } k:
                findings[k] = findings[k] with
                {
                    ThreadId = opened.ThreadId,
                    // Only an open finding becomes posted. One that was already resolved when someone
                    // got around to publishing it stays resolved, which is correct.
                    Estado = findings[k].Estado == MemoryFinding.Open ? MemoryFinding.Posted : findings[k].Estado,
                };
                break;

            case PostOutcome.Failed failed:
                failures.Add(Numbered(position, failed.Message));
                break;

            default:
                // Opened with nothing to record, or a reply, which changes nothing.
                break;
        }
    }

    private static async Task SummaryAsync(
        IPullRequestHost host,
        long prId,
        bool postSummary,
        string? summary,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        if (!postSummary || summary is null)
        {
            return;
        }

        try
        {
            await host.PostSummaryAsync(prId, summary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is GitHubException or AzureException)
        {
            failures.Add($"summary: {failure.Message}");
        }
    }

    /// <summary>The posting machine's local date — not UTC, and not the repository's timezone.</summary>
    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An item's failure, prefixed with its 1-based position in the batch.</summary>
    private static string Numbered(int position, string message) =>
        string.Create(CultureInfo.InvariantCulture, $"#{position + 1}: {message}");

    /// <summary>
    /// Turns whatever failed into the one error the caller gets.
    /// </summary>
    /// <remarks>
    /// Nothing in it says which items succeeded, so a caller retrying the whole batch re-posts the ones
    /// that already worked. The <c>#n</c> prefixes are what the frontend has to read to avoid that.
    /// </remarks>
    private static void Report(List<string> failures)
    {
        if (failures.Count > 0)
        {
            throw new ReviewException(
                string.Create(CultureInfo.InvariantCulture,
                    $"{failures.Count} comment(s) failed to post — {string.Join("; ", failures)}"));
        }
    }

    /// <summary>A run's stored findings, or none when the column will not parse.</summary>
    private static List<MemoryFinding> Stored(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ReviewJsonContext.Default.ListMemoryFinding) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
