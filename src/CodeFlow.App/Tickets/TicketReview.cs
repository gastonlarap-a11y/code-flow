using System.Text.Json;
using CodeFlow.Activity;
using CodeFlow.Ai;
using CodeFlow.Git;
using CodeFlow.Platform;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using CodeFlow.Review;
using CodeFlow.Security;
using CodeFlow.Storage;
using CodeFlow.Workspaces;

namespace CodeFlow.Tickets;

/// <summary>
/// The review that judges a branch's whole contribution against the ticket it was written for.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>ReviewRun.ForProjectAsync</c> but without a pull request: this runs before the work
/// is committed, which is the only moment at which "you have not done what the ticket asked" is still
/// cheap to act on.
/// </para>
/// <para>
/// Read-only against Azure, like the rest of this feature. It fetches the work item so the criteria
/// being judged are current and writes nothing back to the board.
/// </para>
/// </remarks>
internal static class TicketReview
{
    /// <summary>
    /// "This branch has no ticket", for a renderer that only ever sees the message string.
    /// </summary>
    /// <remarks>
    /// The same device as <c>NOTHING_TO_ANALYZE: </c>: a state rather than a failure, so the UI shows
    /// the "link a ticket" affordance instead of a red row in Activity. <c>XLANG-017</c>.
    /// </remarks>
    public const string NotLinkedPrefix = "TICKET_NOT_LINKED: ";

    /// <summary>
    /// "The ticket could not be fetched and nothing usable was cached."
    /// </summary>
    /// <remarks>
    /// Raised only when both are true. A fetch that fails over a cache that holds the work item runs
    /// anyway, saying how old the copy is — refusing to review because a PAT expired would withhold
    /// the finding half of the answer as well, which never needed the network. <c>XLANG-017</c>.
    /// </remarks>
    public const string SyncFailedPrefix = "TICKET_SYNC_FAILED: ";

    /// <summary>How much of the user's <c>notes/</c> reaches the prompt.</summary>
    private const int NotesBudgetChars = 20_000;

    private const string HistoryKind = "ticket-review";

    /// <summary>Runs one review and stores it.</summary>
    /// <param name="baseRef">
    /// What the branch is measured against, when <paramref name="scope"/> is the branch. Chosen by
    /// the caller — there is no default base branch anywhere in this app, and inventing one here
    /// would silently review against the wrong tree. Ignored for a working-tree scope, which has no
    /// base.
    /// </param>
    /// <param name="scope">
    /// Which diff to judge. <see cref="ReviewScope.Working"/> answers "is what I have not committed
    /// yet going in the right direction", and carries a caveat the model needs — see
    /// <see cref="ReviewScopes.CriteriaCaveat"/>.
    /// </param>
    /// <param name="jobId">The id the renderer minted, so the run streams and can be stopped.</param>
    public static async Task<string> RunAsync(
        Database database,
        HttpClient http,
        AiRunner runner,
        string projectId,
        string branch,
        string baseRef,
        ReviewScope scope,
        string level,
        string jobId,
        AgentOverride agent,
        CancellationToken cancellationToken)
    {
        var (setup, template, linked) = await database.ReadAsync(
            connection =>
            {
                var prepared = AiTurn.Prepare(connection, projectId, "ticket_review", agent);
                return (
                    prepared,
                    Settings.GetWorkspacePrompt(connection, prepared.Project.WorkspaceId, "ticket_review_standard"),
                    TicketStore.ForBranch(connection, projectId, branch));
            },
            cancellationToken).ConfigureAwait(false);

        if (linked is null)
        {
            throw new AiRunFailedException(
                NotLinkedPrefix + $"La rama '{branch}' no está vinculada a ningún ticket");
        }

        var (ticket, staleness) = await RefreshAsync(database, http, linked, cancellationToken).ConfigureAwait(false);

        var criteria = await database
            .ReadAsync(connection => TicketSync.CriteriaFor(connection, ticket), cancellationToken)
            .ConfigureAwait(false);

        // Off the pump thread: LibGit2Sharp is synchronous and a branch's whole contribution is not a
        // bounded amount of work. The notes ride along because they are file IO too.
        var (diffText, codeContext, headSha, noteText) = await Task.Run(
            () =>
            {
                var files = scope is ReviewScope.Branch
                    ? Diff.BranchContribution(setup.Project.LocalPath, baseRef)
                    : Diff.Working(setup.Project.LocalPath);

                return (
                    Diff.RenderForPrompt(files),
                    ChangeContext.Render(files),
                    Diff.HeadSha(setup.Project.LocalPath),
                    TicketMirror.ReadNotes(ticket.MirrorPath, NotesBudgetChars));
            },
            cancellationToken).ConfigureAwait(false);

        var mcpConfigPath = McpConfig.Write(
            setup.Mcps, AppPaths.WorkspaceMcpConfigFile(setup.Project.WorkspaceId));

        try
        {
            var run = await AiOperations.ReviewBranchAgainstTicketAsync(
                runner,
                setup.Config,
                Header(ticket, staleness),
                Body(ticket),
                criteria.Markdown,
                criteria.Mode,
                noteText,
                branch,
                baseRef,
                scope,
                setup.Contexts,
                diffText,
                codeContext,
                setup.Project.LocalPath,
                template,
                level,
                mcpConfigPath,
                new AiRunContext(jobId),
                cancellationToken).ConfigureAwait(false);

            var verdict = TicketVerdict.Parse(run.Text);
            var findings = ReviewMemory.ParseFindings(TicketVerdict.Split(run.Text).Findings);

            var stamped = AiText.StampFooter(
                run.Text, "revisión contra ticket", setup.Config.Engine.Label,
                run.Model ?? setup.Config.Model, DateTimeOffset.Now, run.Usage);

            var result = new TicketReviewResult(
                jobId,
                projectId,
                ticket.Id,
                branch,
                // Empty for a working-tree scope, which has no base: the column is NOT NULL and a
                // base branch recorded for a review that never compared against one would be a
                // claim about what was judged. The scope itself rides in `meta`.
                scope is ReviewScope.Branch ? baseRef : string.Empty,
                headSha,
                level,
                stamped,
                verdict?.Criteria ?? [],
                verdict?.Coverage,
                Clock.Now());

            var meta = new TicketReviewMeta(
                result.Coverage, setup.Config.Provider, run.Model ?? setup.Config.Model, scope.ToString());

            await database.WriteAsync(
                connection =>
                {
                    TicketReviewStore.Add(
                        connection,
                        result,
                        setup.Project.WorkspaceId,
                        meta,
                        JsonSerializer.Serialize(findings, ReviewJsonContext.Default.ListMemoryFinding),
                        diffText);

                    JobHistoryStore.Add(
                        connection, jobId, projectId, HistoryKind, Label(ticket), "done", stamped, null, "{}");
                },
                cancellationToken).ConfigureAwait(false);

            // The markdown, not the record: `jobsStore.run` stores a string, and a run's parsed
            // verdict is read back from `list_ticket_reviews` when the history needs it. Returning
            // the record meant the caller unwrapped it immediately.
            return stamped;
        }
        catch (AiRunFailedException failure)
        {
            // A run the user stopped, and "this branch changes nothing", are not history worth
            // keeping — the same two exclusions `AnalyzeWorkingChangesAsync` makes, for the same
            // reason: neither is a request that failed.
            var refused =
                failure.Message.StartsWith(AiRunRegistry.CancelledMarker, StringComparison.Ordinal)
                || failure.Message.StartsWith(AiOperations.NothingToAnalyzePrefix, StringComparison.Ordinal);

            if (!refused)
            {
                await database.WriteAsync(
                    connection => JobHistoryStore.Add(
                        connection, jobId, projectId, HistoryKind, Label(ticket), "error", null,
                        failure.Message, "{}"),
                    cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>A branch's stored reviews, newest first.</summary>
    public static List<TicketReviewResult> ForBranch(
        Microsoft.Data.Sqlite.SqliteConnection connection, string projectId, string branch) =>
        TicketReviewStore.ForBranch(connection, projectId, branch);

    /// <summary>
    /// Re-fetches the ticket, and says how stale the copy is when that could not be done.
    /// </summary>
    /// <remarks>
    /// Best-effort on purpose (<c>WI-009</c>): the sync is here so the criteria are current, not so
    /// the review can be held hostage to the network. What it cannot do is pretend — a review run
    /// against a copy from last week says so in the ticket header the model reads, because "the code
    /// does not meet AC-3" and "AC-3 changed yesterday" are different answers.
    /// </remarks>
    private static async Task<(Ticket Ticket, string Staleness)> RefreshAsync(
        Database database, HttpClient http, Ticket cached, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await TicketSync
                .RunAsync(database, http, cached.Org, cached.Project, cached.ExternalId, cancellationToken)
                .ConfigureAwait(false);

            return (fresh, string.Empty);
        }
        catch (Exception failure)
            when (failure is AzureException or ProviderException or CredentialStoreException
                      or HttpRequestException or TaskCanceledException
                  && !cancellationToken.IsCancellationRequested)
        {
            var raw = await database
                .ReadAsync(connection => TicketStore.RawJson(connection, cached.Id), cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(raw) || raw == "{}")
            {
                throw new AiRunFailedException(
                    SyncFailedPrefix
                    + $"No se pudo leer el ticket {cached.ExternalId} y no hay una copia local que usar: {failure.Message}");
            }

            return (cached, $" — copia local del {cached.SyncedAt}, no se pudo refrescar: {failure.Message}");
        }
    }

    /// <summary>The one line that tells the model what it is judging against.</summary>
    private static string Header(Ticket ticket, string staleness) =>
        $"{ticket.ExternalId} · {ticket.WorkItemType} · {ticket.Title} (estado: {ticket.State}){staleness}";

    /// <summary>The ticket's own prose, read from the mirror the sync just wrote.</summary>
    /// <remarks>
    /// From disk rather than from the cached payload because the mirror is already Markdown with its
    /// attachments relinked — the same text the user reads. When it cannot be read the review still
    /// runs on the criteria alone, which is the part it is actually judging.
    /// </remarks>
    private static string Body(Ticket ticket)
    {
        try
        {
            var path = Path.Combine(ticket.MirrorPath, TicketMirror.TicketFile);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string Label(Ticket ticket) => $"{ticket.ExternalId} · {ticket.Title}";
}
