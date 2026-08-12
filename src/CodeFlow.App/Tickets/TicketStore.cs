using CodeFlow.Storage;
using Microsoft.Data.Sqlite;

namespace CodeFlow.Tickets;

/// <summary>
/// The cached tickets and the branches linked to them.
/// </summary>
/// <remarks>
/// The cache exists for three reasons, and only the first is speed: a ticket must still render when
/// the network is gone, the mirror must be rewritable without a fetch, and
/// <see cref="OthersOfType"/> needs a corpus to compare a field against when deciding whether it is
/// a filled-in requirement or an unanswered template.
/// </remarks>
internal static class TicketStore
{
    private const string Columns =
        "id, provider, org, project, external_id, title, state, work_item_type, "
        + "assigned_to, web_url, rev, mirror_path, synced_at";

    /// <summary>
    /// The identity a ticket is keyed by.
    /// </summary>
    /// <remarks>
    /// Composed here rather than left to callers so the primary key and the unique index can never
    /// disagree about what "the same ticket" means.
    /// </remarks>
    public static string IdFor(string provider, string org, string project, string externalId) =>
        $"{provider}:{org}:{project}:{externalId}";

    /// <summary>Writes a freshly fetched ticket over whatever was cached for it.</summary>
    /// <remarks>
    /// An upsert rather than delete-then-insert: <c>ticket_links</c> references this row, and
    /// deleting it would cascade the branch link away on every refresh.
    /// </remarks>
    public static Ticket Upsert(SqliteConnection connection, Ticket ticket, string rawJson)
    {
        Sql.Execute(connection,
            """
            INSERT INTO tickets
                (id, provider, org, project, external_id, title, state, work_item_type,
                 assigned_to, web_url, rev, raw_json, mirror_path, synced_at)
            VALUES
                ($id, $provider, $org, $project, $externalId, $title, $state, $workItemType,
                 $assignedTo, $webUrl, $rev, $rawJson, $mirrorPath, $syncedAt)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                state = excluded.state,
                work_item_type = excluded.work_item_type,
                assigned_to = excluded.assigned_to,
                web_url = excluded.web_url,
                rev = excluded.rev,
                raw_json = excluded.raw_json,
                mirror_path = excluded.mirror_path,
                synced_at = excluded.synced_at
            """,
            ("$id", ticket.Id),
            ("$provider", ticket.Provider),
            ("$org", ticket.Org),
            ("$project", ticket.Project),
            ("$externalId", ticket.ExternalId),
            ("$title", ticket.Title),
            ("$state", ticket.State),
            ("$workItemType", ticket.WorkItemType),
            ("$assignedTo", ticket.AssignedTo),
            ("$webUrl", ticket.WebUrl),
            ("$rev", ticket.Rev),
            ("$rawJson", rawJson),
            ("$mirrorPath", ticket.MirrorPath),
            ("$syncedAt", ticket.SyncedAt));

        return ticket;
    }

    public static Ticket? Get(SqliteConnection connection, string id) =>
        Sql.QuerySingle(connection, $"SELECT {Columns} FROM tickets WHERE id = $id", Read, ("$id", id));

    /// <summary>The raw payload a ticket was built from, for rewriting its mirror.</summary>
    public static string? RawJson(SqliteConnection connection, string id) =>
        Sql.QueryText(connection, "SELECT raw_json FROM tickets WHERE id = $id", ("$id", id));

    /// <summary>
    /// Every ticket this repository has linked, most recently synced first, each with the branches
    /// it is work for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scoped to one repository, not to the workspace.</b> It was workspace-wide first, and using
    /// it settled the question: the module answers "what is this repository working on", and a list
    /// that mixes in another repository's tickets is answering a question nobody asked in this view.
    /// </para>
    /// <para>
    /// <b>A link outlives the branch.</b> Nothing here or anywhere else deletes a row from
    /// <c>ticket_links</c> when a git branch is deleted — the only <c>DELETE</c> is
    /// <see cref="Unlink"/>, which is the explicit button. That is deliberate: a merged branch is
    /// deleted as a matter of course, and the record of what it was work for is precisely what you
    /// want afterwards.
    /// </para>
    /// <para>
    /// One query and a grouping rather than <c>SELECT DISTINCT</c> plus a second read: the join
    /// already produces a row per link, and collapsing them was what threw the branch away.
    /// </para>
    /// </remarks>
    public static List<TicketWithLinks> List(SqliteConnection connection, string projectId)
    {
        var rows = Sql.Query(connection,
            $"""
            SELECT {Prefixed("t")}, p.id, p.name, l.branch
            FROM tickets t
            JOIN ticket_links l ON l.ticket_id = t.id
            JOIN projects p ON p.id = l.project_id
            WHERE l.project_id = $projectId
            ORDER BY t.synced_at DESC, l.branch
            """,
            reader => (
                Ticket: Read(reader),
                Link: new TicketLink(reader.GetString(13), reader.GetString(14), reader.GetString(15))),
            ("$projectId", projectId));

        // Grouped in order: the SQL already sorts, and a dictionary would lose that. `GroupBy`
        // preserves first-appearance order, which is the "most recently synced first" the caller
        // asked for.
        return [.. rows
            .GroupBy(row => row.Ticket.Id)
            .Select(group => new TicketWithLinks(
                group.First().Ticket,
                [.. group.Select(row => row.Link)]))];
    }

    /// <summary>
    /// Other cached tickets of the same type, for telling a requirement from a blank template.
    /// </summary>
    /// <remarks>
    /// Scoped to the same project and work item type because that is the scope a process template
    /// applies to: two Product Backlog Items on one board share a form, a Bug on another board does
    /// not. Returns raw payloads — the comparison is per field, and which fields matter is the
    /// caller's decision.
    /// </remarks>
    public static List<string> OthersOfType(
        SqliteConnection connection, string provider, string org, string project, string workItemType, string exceptId) =>
        Sql.Query(connection,
            """
            SELECT raw_json FROM tickets
            WHERE provider = $provider AND org = $org AND project = $project
              AND work_item_type = $workItemType AND id <> $exceptId
            ORDER BY synced_at DESC
            LIMIT 20
            """,
            reader => reader.GetString(0),
            ("$provider", provider),
            ("$org", org),
            ("$project", project),
            ("$workItemType", workItemType),
            ("$exceptId", exceptId));

    /// <summary>Points a branch at a ticket, replacing whatever it pointed at before.</summary>
    public static void Link(SqliteConnection connection, string projectId, string branch, string ticketId) =>
        Sql.Execute(connection,
            """
            INSERT INTO ticket_links (project_id, branch, ticket_id, linked_at)
            VALUES ($projectId, $branch, $ticketId, $linkedAt)
            ON CONFLICT(project_id, branch) DO UPDATE SET
                ticket_id = excluded.ticket_id,
                linked_at = excluded.linked_at
            """,
            ("$projectId", projectId),
            ("$branch", branch),
            ("$ticketId", ticketId),
            ("$linkedAt", Clock.Now()));

    public static void Unlink(SqliteConnection connection, string projectId, string branch) =>
        Sql.Execute(connection,
            "DELETE FROM ticket_links WHERE project_id = $projectId AND branch = $branch",
            ("$projectId", projectId), ("$branch", branch));

    /// <summary>The ticket a branch is linked to, or <see langword="null"/>.</summary>
    /// <remarks>
    /// The explicit link only. The branch-name heuristic is a suggestion offered before linking, and
    /// letting it answer here would mean a review silently judged against a ticket nobody chose.
    /// </remarks>
    public static Ticket? ForBranch(SqliteConnection connection, string projectId, string branch) =>
        Sql.QuerySingle(connection,
            $"""
            SELECT {Prefixed("t")}
            FROM tickets t JOIN ticket_links l ON l.ticket_id = t.id
            WHERE l.project_id = $projectId AND l.branch = $branch
            """,
            Read,
            ("$projectId", projectId), ("$branch", branch));

    /// <summary>The column list qualified by a table alias, for the two joined reads.</summary>
    private static string Prefixed(string alias) =>
        string.Join(", ", Columns.Split(", ").Select(column => $"{alias}.{column}"));

    private static Ticket Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.TextOrNull(8),
        reader.GetString(9),
        reader.GetInt64(10),
        reader.GetString(11),
        reader.GetString(12));
}
