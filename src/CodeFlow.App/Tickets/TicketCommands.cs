using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using CodeFlow.Storage;
using CodeFlow.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tickets;

/// <summary>
/// The work-item commands: reading, caching and linking tickets.
/// </summary>
/// <remarks>
/// Every command here reads. None writes to Azure — commenting and state transitions are a later,
/// separately requested step, and until then a defect in this file cannot alter anybody's board.
/// </remarks>
internal static class TicketCommands
{
    public static CommandRegistry AddTicketCommands(
        this CommandRegistry registry, Database database, AiRunRegistry runs, HttpClient http) =>
        registry
            .Add("update_workspace_ticket_account", (p, ct) =>
                WriteUnit(database,
                    c => WorkspaceStore.UpdateTicketAccount(
                        c, Arg(p, "workspaceId"), OptionalArg(p, "org"), OptionalArg(p, "project")), ct))

            .Add("resolve_ticket_account", (p, ct) =>
                Read(database, c => TicketAccounts.Resolve(c, Arg(p, "projectId")),
                    TicketJsonContext.Default.TicketAccount, ct))

            // Pure parsing: no database, no network. It answers what a pasted URL or a typed number
            // addresses, and null for anything that is not a work item.
            .Add("resolve_ticket_link", (p, ct) =>
                ValueTask.FromResult(Json(
                    WorkItemLink.Parse(Arg(p, "text")) is { } reference
                        ? new TicketLinkRef(reference.Id, reference.Org, reference.Project)
                        : null,
                    TicketJsonContext.Default.TicketLinkRef!)))

            .Add("suggest_ticket_for_branch", (p, ct) =>
                ValueTask.FromResult(Json(
                    TicketBranchRef.Detect(Arg(p, "branch")) is { } suggestion
                        ? new TicketSuggestion(suggestion.Provider, suggestion.ExternalId)
                        : null,
                    TicketJsonContext.Default.TicketSuggestion!)))

            .Add("sync_ticket", async (p, ct) =>
                Json(
                    await TicketSync.RunAsync(
                        database, http, Arg(p, "org"), Arg(p, "project"), Arg(p, "externalId"), ct)
                        .ConfigureAwait(false),
                    TicketJsonContext.Default.Ticket))

            .Add("get_ticket", (p, ct) =>
                Read(database, c => TicketStore.Get(c, Arg(p, "ticketId")), TicketJsonContext.Default.Ticket!, ct))

            .Add("list_tickets", (p, ct) =>
                Read(database, c => TicketStore.List(c, Arg(p, "projectId")),
                    TicketJsonContext.Default.ListTicketWithLinks, ct))

            .Add("get_ticket_criteria", (p, ct) =>
                Read(database,
                    c => TicketStore.Get(c, Arg(p, "ticketId")) is { } ticket
                        ? TicketSync.CriteriaFor(c, ticket)
                        : new TicketCriteria(TicketCriteriaReader.ModeNone, null, string.Empty, []),
                    TicketJsonContext.Default.TicketCriteria, ct))

            .Add("link_branch_ticket", (p, ct) =>
                WriteUnit(database,
                    c => TicketStore.Link(c, Arg(p, "projectId"), Arg(p, "branch"), Arg(p, "ticketId")), ct))

            .Add("unlink_branch_ticket", (p, ct) =>
                WriteUnit(database, c => TicketStore.Unlink(c, Arg(p, "projectId"), Arg(p, "branch")), ct))

            .Add("ticket_for_branch", (p, ct) =>
                Read(database, c => TicketStore.ForBranch(c, Arg(p, "projectId"), Arg(p, "branch")),
                    TicketJsonContext.Default.Ticket!, ct))

            .Add("list_sprint_tickets", (p, ct) =>
                SprintAsync(http, Arg(p, "org"), Arg(p, "project"), OptionalArg(p, "team"), ct))

            .Add("list_my_tickets", (p, ct) =>
                QueryAsync(http, Arg(p, "org"), Arg(p, "project"), AzureWorkItemClient.AssignedToMe, ct))

            .Add("preview_ticket", (p, ct) =>
                PreviewAsync(http, Arg(p, "org"), Arg(p, "project"), Arg(p, "externalId"), ct))

            .Add("list_ticket_reviews", (p, ct) =>
                Read(database, c => TicketReview.ForBranch(c, Arg(p, "projectId"), Arg(p, "branch")),
                    TicketJsonContext.Default.ListTicketReviewResult, ct))

            .Add("review_changes", async (p, ct) =>
                Json(await ReviewAsync(database, runs, http, p, ct).ConfigureAwait(false),
                    TicketJsonContext.Default.String))

            // The one verb here that writes to a board. It carries the body rather than a run id on
            // purpose: the button publishes the text the user just read, and deriving it again here
            // would let the two drift (`WI-022`).
            .Add("comment_ticket", async (p, ct) =>
                Json(
                    await TicketComment
                        .PostAsync(database, http, Arg(p, "ticketId"), Arg(p, "body"), ct)
                        .ConfigureAwait(false),
                    TicketJsonContext.Default.String));

    /// <summary>
    /// The work items on a team's current sprint.
    /// </summary>
    /// <remarks>
    /// The default list for a picker, because it is the one a person recognises: it is what their
    /// taskboard shows. Measured on a real board it is 46 rows where the project holds thousands.
    /// With no team named, the first one that has a current iteration wins — a project's teams are
    /// few, and asking the user to pick a team before they can pick a ticket is a step too many.
    /// </remarks>
    private static async ValueTask<ReadOnlyMemory<byte>> SprintAsync(
        HttpClient http, string org, string project, string? team, CancellationToken cancellationToken)
    {
        var pat = PullRequestHosts.PatForOrg(org);

        var teams = team is { Length: > 0 }
            ? [team]
            : (await AzureWorkItemClient.ListTeamsAsync(http, org, project, pat, cancellationToken)
                .ConfigureAwait(false))
                .Select(candidate => candidate.Name)
                .ToList();

        foreach (var candidate in teams)
        {
            var iterations = await AzureWorkItemClient
                .ListIterationsAsync(http, org, project, candidate, pat, cancellationToken)
                .ConfigureAwait(false);

            // The one Azure itself marks current, falling back to the latest that has started —
            // a board with no current iteration still has a most recent one worth showing.
            var iteration = iterations.FirstOrDefault(i => i.Attributes?.TimeFrame == "current")
                ?? iterations.OrderByDescending(i => i.Attributes?.StartDate ?? DateTimeOffset.MinValue).FirstOrDefault();

            if (iteration is null)
            {
                continue;
            }

            var ids = await AzureWorkItemClient
                .IterationWorkItemIdsAsync(http, org, project, candidate, iteration.Id, pat, cancellationToken)
                .ConfigureAwait(false);

            if (ids.Count > 0)
            {
                return Json(await SummariesAsync(http, org, project, ids, pat, cancellationToken)
                    .ConfigureAwait(false), TicketJsonContext.Default.IReadOnlyListTicketSummary);
            }
        }

        return Json((IReadOnlyList<TicketSummary>)[], TicketJsonContext.Default.IReadOnlyListTicketSummary);
    }

    /// <summary>
    /// Reviews local changes, with or without the branch's work item in the question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A dispatcher, not a third implementation.</b> Two axes reach it — which diff
    /// (<c>scope</c>) and whether the ticket is judged too (<c>withTicket</c>) — and the four
    /// combinations they make used to be two, because the axes were welded together: the pre-commit
    /// analysis was always the working tree and never the ticket, the ticket review always the whole
    /// branch. The one that was missing and wanted is a whole-branch review with no ticket at all.
    /// </para>
    /// <para>
    /// <b>Why it lives here and not in <c>Ai/</c>.</b> The ticket half needs
    /// <see cref="TicketReview"/>, and <c>Tickets/</c> already depends on <c>Ai/</c>. Registering it
    /// beside the analysis would have made <c>Ai/</c> depend on <c>Tickets/</c> in return, closing a
    /// cycle between two features. The body of each half stays in the feature that owns it — the
    /// analysis in <c>AiTurn</c>, where <c>AI-024</c>'s refusal rules have always lived.
    /// </para>
    /// <para>
    /// It replaces <c>analyze_working_changes</c> and <c>review_branch_ticket</c>, each of which had
    /// exactly one caller.
    /// </para>
    /// </remarks>
    private static async Task<string> ReviewAsync(
        Database database, AiRunRegistry runs, HttpClient http, JsonElement p, CancellationToken cancellationToken)
    {
        var scope = ReviewScopes.Parse(OptionalArg(p, "scope"));
        var agent = new AgentOverride(
            OptionalArg(p, "agentProvider"), OptionalArg(p, "agentModel"), OptionalArg(p, "agentPrompt"));
        var runner = AiEngineRunner.Bind(runs, http);

        return Flag(p, "withTicket")
            ? await TicketReview.RunAsync(
                database, http, runner, Arg(p, "projectId"), Arg(p, "branch"),
                OptionalArg(p, "baseRef") ?? string.Empty, scope, Arg(p, "level"), Arg(p, "jobId"), agent,
                cancellationToken).ConfigureAwait(false)
            : await AiTurn.AnalyzeChangesAsync(
                database, runner, Arg(p, "projectId"), Arg(p, "jobId"), scope,
                OptionalArg(p, "baseRef") ?? string.Empty, agent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One work item's row, without caching or mirroring it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the link dialog shows the moment a pasted address parses: the title, type and state of
    /// the thing you are about to link. It exists rather than reusing <c>sync_ticket</c> because
    /// that one is the full fetch — it writes the cache, rewrites the mirror and downloads up to
    /// 16 MB of attachments — and this runs while somebody is still typing.
    /// </para>
    /// <para>
    /// A work item that does not exist comes back as <see langword="null"/> rather than as an error:
    /// a half-typed id addresses nothing, and that is the ordinary state of the field, not a
    /// failure to report.
    /// </para>
    /// </remarks>
    private static async ValueTask<ReadOnlyMemory<byte>> PreviewAsync(
        HttpClient http, string org, string project, string externalId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(externalId, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return Json<TicketSummary?>(null, TicketJsonContext.Default.TicketSummary!);
        }

        var rows = await SummariesAsync(http, org, project, [id], PullRequestHosts.PatForOrg(org), cancellationToken)
            .ConfigureAwait(false);

        return Json(rows.Count > 0 ? rows[0] : null, TicketJsonContext.Default.TicketSummary!);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> QueryAsync(
        HttpClient http, string org, string project, string condition, CancellationToken cancellationToken)
    {
        var pat = PullRequestHosts.PatForOrg(org);

        var ids = await AzureWorkItemClient
            .QueryIdsAsync(http, org, project, condition, top: 100, pat, cancellationToken)
            .ConfigureAwait(false);

        return Json(await SummariesAsync(http, org, project, ids, pat, cancellationToken).ConfigureAwait(false),
            TicketJsonContext.Default.IReadOnlyListTicketSummary);
    }

    /// <summary>Turns a list of ids into the rows a picker renders.</summary>
    private static async Task<IReadOnlyList<TicketSummary>> SummariesAsync(
        HttpClient http, string org, string project, IReadOnlyList<long> ids, string pat,
        CancellationToken cancellationToken)
    {
        var items = await AzureWorkItemClient
            .GetWorkItemsAsync(http, org, project, ids, AzureWorkItemClient.SummaryFields, pat, cancellationToken)
            .ConfigureAwait(false);

        return [.. items.Select(item => new TicketSummary(
            item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Field(item, "System.Title") ?? "",
            Field(item, "System.State") ?? "",
            Field(item, "System.WorkItemType") ?? "",
            DisplayName(item, "System.AssignedTo")))];
    }

    private static string? Field(RawWorkItem item, string name) =>
        item.Fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? DisplayName(RawWorkItem item, string name) =>
        item.Fields.TryGetValue(name, out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("displayName", out var display)
            ? display.GetString()
            : null;

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>A boolean argument, absent counting as false.</summary>
    private static bool Flag(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? OptionalArg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async ValueTask<ReadOnlyMemory<byte>> Read<T>(
        Database database, Func<SqliteConnection, T> work, JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        var result = await database.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        return Json(result, type);
    }

    private static async ValueTask<ReadOnlyMemory<byte>> WriteUnit(
        Database database, Action<SqliteConnection> work, CancellationToken cancellationToken)
    {
        await database.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return "null"u8.ToArray();
    }

    private static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToUtf8Bytes(value, type);
}

/// <summary>Every shape the work-item commands return.</summary>
/// <remarks>
/// Snake_case, because returned payloads are read verbatim by <c>renderer/src/types/domain.ts</c>.
/// Each list type is listed explicitly: the generator does not infer a collection from its element.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Ticket))]
[JsonSerializable(typeof(List<Ticket>))]
[JsonSerializable(typeof(TicketLink))]
[JsonSerializable(typeof(IReadOnlyList<TicketLink>))]
[JsonSerializable(typeof(TicketWithLinks))]
[JsonSerializable(typeof(List<TicketWithLinks>))]
[JsonSerializable(typeof(TicketSummary))]
[JsonSerializable(typeof(IReadOnlyList<TicketSummary>))]
[JsonSerializable(typeof(TicketAccount))]
[JsonSerializable(typeof(TicketCriteria))]
[JsonSerializable(typeof(TicketSuggestion))]
[JsonSerializable(typeof(TicketLinkRef))]
[JsonSerializable(typeof(TicketCriterionVerdict))]
[JsonSerializable(typeof(IReadOnlyList<TicketCriterionVerdict>))]
[JsonSerializable(typeof(TicketCoverage))]
[JsonSerializable(typeof(TicketReviewMeta))]
[JsonSerializable(typeof(TicketReviewResult))]
[JsonSerializable(typeof(List<TicketReviewResult>))]
[JsonSerializable(typeof(string))]
internal sealed partial class TicketJsonContext : JsonSerializerContext;
