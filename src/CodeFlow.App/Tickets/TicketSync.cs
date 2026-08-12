using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using CodeFlow.Storage;

namespace CodeFlow.Tickets;

/// <summary>
/// Fetches a work item, caches it and writes its readable copy to disk.
/// </summary>
/// <remarks>
/// <para>
/// Read-only against Azure. Nothing here writes to a board.
/// </para>
/// <para>
/// Runs on three triggers and never on a timer: when a branch is linked, when the user asks for a
/// refresh, and best-effort immediately before a review so the criteria being judged are current. A
/// background poll would spend a PAT's rate budget on tickets nobody is looking at.
/// </para>
/// </remarks>
internal static class TicketSync
{
    /// <summary>
    /// How much attachment content one ticket may pull down.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a per-file limit: what matters is the total a single sync writes into
    /// the user's directory. Everything past it is named in <c>ticket.md</c> instead of being
    /// silently absent — a missing screenshot the model cannot see is a fact worth stating.
    /// </remarks>
    internal const long AttachmentBudgetBytes = 16 * 1024 * 1024;

    /// <summary>Azure's own relation type for a file attached to a work item.</summary>
    private const string AttachedFile = "AttachedFile";

    /// <summary>Reads one work item and mirrors it, returning what was cached.</summary>
    public static async Task<Ticket> RunAsync(
        Database database,
        HttpClient http,
        string org,
        string project,
        string externalId,
        CancellationToken cancellationToken)
    {
        var pat = PullRequestHosts.PatForOrg(org);

        var id = long.TryParse(externalId, out var numeric)
            ? numeric
            : throw new ArgumentException($"'{externalId}' is not an Azure DevOps work item id");

        var item = await AzureWorkItemClient
            .GetWorkItemAsync(http, org, project, id, pat, cancellationToken)
            .ConfigureAwait(false);

        var rawJson = JsonSerializer.Serialize(item, AzureWorkItemJsonContext.Default.RawWorkItem);
        var fields = JsonDocument.Parse(rawJson).RootElement.GetProperty("fields");

        var workItemType = Text(item, "System.WorkItemType") ?? "Work Item";
        var ticketId = TicketStore.IdFor("azure", org, project, externalId);

        var (order, others, root, cachedMirror) = await database.ReadAsync(
            connection => (
                TicketCriteriaReader.FieldsFor(connection, org, project),
                TicketStore.OthersOfType(connection, "azure", org, project, workItemType, ticketId),
                TicketPaths.RootFor(connection),
                TicketStore.Get(connection, ticketId)?.MirrorPath),
            cancellationToken).ConfigureAwait(false);

        var criteria = TicketCriteriaReader.Read(fields, order, others);

        var title = Text(item, "System.Title") ?? $"Work item {externalId}";

        // Where it already is, if it has been mirrored before. See `TicketPaths.MirrorFor`: a mirror
        // that moves takes the user's `notes/` out of reach.
        var directory = TicketPaths.MirrorFor(cachedMirror, root, org, project, externalId, title);

        var ticket = new Ticket(
            ticketId,
            "azure",
            org,
            project,
            externalId,
            title,
            Text(item, "System.State") ?? "",
            workItemType,
            Identity(item, "System.AssignedTo"),
            AzureWorkItemClient.WebUrl(org, project, id),
            item.Rev,
            directory,
            Clock.Now());

        var (attachments, skipped) = await DownloadAsync(http, item, pat, cancellationToken).ConfigureAwait(false);

        // Synchronous IO off the caller's thread, the shape SkillSync established. A mirror that
        // cannot be written must not lose the fetch: the cache below is what the app actually reads.
        await Task.Run(
            () => TryWrite(directory, ticket, criteria, rawJson, attachments, skipped),
            cancellationToken).ConfigureAwait(false);

        await database.WriteAsync(
            connection => TicketStore.Upsert(connection, ticket, rawJson),
            cancellationToken).ConfigureAwait(false);

        return ticket;
    }

    /// <summary>Recomputes a cached ticket's criteria without going to the network.</summary>
    /// <remarks>
    /// The payload the criteria are derived from is already stored, so answering "what does this
    /// ticket ask for" costs a read. That is what lets the picker show which field a ticket's
    /// requirements would come from before anything is linked.
    /// </remarks>
    public static TicketCriteria CriteriaFor(Microsoft.Data.Sqlite.SqliteConnection connection, Ticket ticket)
    {
        var rawJson = TicketStore.RawJson(connection, ticket.Id);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new TicketCriteria(TicketCriteriaReader.ModeNone, null, string.Empty, []);
        }

        using var document = JsonDocument.Parse(rawJson);
        if (!document.RootElement.TryGetProperty("fields", out var fields))
        {
            return new TicketCriteria(TicketCriteriaReader.ModeNone, null, string.Empty, []);
        }

        return TicketCriteriaReader.Read(
            fields,
            TicketCriteriaReader.FieldsFor(connection, ticket.Org, ticket.Project),
            TicketStore.OthersOfType(connection, ticket.Provider, ticket.Org, ticket.Project, ticket.WorkItemType, ticket.Id));
    }

    /// <summary>
    /// Downloads the attachments that fit the budget, and names the ones that did not.
    /// </summary>
    /// <remarks>
    /// One failed attachment does not fail the sync. A ticket that will not open because a
    /// screenshot 404s is worse than a ticket with a note saying the screenshot could not be read.
    /// </remarks>
    private static async Task<(List<TicketAttachment> Saved, List<string> Skipped)> DownloadAsync(
        HttpClient http, RawWorkItem item, string pat, CancellationToken cancellationToken)
    {
        var saved = new List<TicketAttachment>();
        var skipped = new List<string>();
        var budget = AttachmentBudgetBytes;

        foreach (var relation in item.Relations ?? [])
        {
            if (!string.Equals(relation.Rel, AttachedFile, StringComparison.Ordinal))
            {
                continue;
            }

            var name = relation.Attributes?.Name ?? "adjunto";
            if (budget <= 0)
            {
                skipped.Add($"`{name}` — se alcanzó el límite de descarga de esta sincronización");
                continue;
            }

            try
            {
                var content = await AzureWorkItemClient
                    .GetAttachmentAsync(http, relation.Url, name, pat, cancellationToken)
                    .ConfigureAwait(false);

                if (content.Length > budget)
                {
                    skipped.Add($"`{name}` — {content.Length / 1024} KB, no cabe en el presupuesto restante");
                    continue;
                }

                budget -= content.Length;
                saved.Add(new TicketAttachment(name, content, relation.Url));
            }
            catch (AzureException failure)
            {
                skipped.Add($"`{name}` — {failure.Message}");
            }
        }

        return (saved, skipped);
    }

    /// <summary>Writes the mirror, or gives up quietly.</summary>
    /// <remarks>
    /// Best-effort for the same reason <c>SkillSync.TryRun</c> is: a full disk or a directory the
    /// user made read-only must not turn a successful fetch into a failed command. What the app
    /// reads is the cache; the mirror is for the user and for the AI to read as files.
    /// </remarks>
    private static void TryWrite(
        string directory,
        Ticket ticket,
        TicketCriteria criteria,
        string rawJson,
        IReadOnlyList<TicketAttachment> attachments,
        IReadOnlyList<string> skipped)
    {
        try
        {
            TicketMirror.Write(directory, ticket, criteria, rawJson, attachments, skipped);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed; see the remarks.
        }
    }

    private static string? Text(RawWorkItem item, string field) =>
        item.Fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>An identity field's display name — the only part of it anybody reads.</summary>
    private static string? Identity(RawWorkItem item, string field) =>
        item.Fields.TryGetValue(field, out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("displayName", out var name)
            ? name.GetString()
            : null;
}
