namespace CodeFlow.Tickets;

/// <summary>
/// A cached work item, as the renderer receives it.
/// </summary>
/// <remarks>
/// Snake_case on the wire, matching <c>renderer/src/types/domain.ts</c>. The raw payload is
/// deliberately absent: it is stored so the mirror can be rewritten without a network call, and it
/// carries every custom field a process defines — several kilobytes the UI has no use for.
/// </remarks>
public sealed record Ticket(
    string Id,
    string Provider,
    string Org,
    string Project,
    string ExternalId,
    string Title,
    string State,
    string WorkItemType,
    string? AssignedTo,
    string WebUrl,
    long Rev,
    string MirrorPath,
    string SyncedAt);

/// <summary>Where a cached ticket is linked: one per branch it is work for.</summary>
/// <param name="ProjectName">
/// The repository's own name, not only its id. It is what the list prints, and an id on screen
/// answers nothing.
/// </param>
public sealed record TicketLink(string ProjectId, string ProjectName, string Branch);

/// <summary>
/// A cached ticket and the branches it is work for.
/// </summary>
/// <remarks>
/// A wrapper rather than a list inside <see cref="Ticket"/>, because <c>get_ticket</c>,
/// <c>sync_ticket</c> and <c>ticket_for_branch</c> return that record and know nothing about links:
/// a field that arrived empty on all three paths would read as "linked to nothing" rather than as
/// "not asked". The list is genuinely plural — <c>ticket_links</c> is keyed
/// <c>(project_id, branch)</c>, so one ticket can be the work of several branches, and of several
/// repositories.
/// </remarks>
public sealed record TicketWithLinks(Ticket Ticket, IReadOnlyList<TicketLink> Links);

/// <summary>One row of a ticket picker: what a list shows, and nothing more.</summary>
/// <remarks>
/// Separate from <see cref="Ticket"/> because these are not cached — they come straight from a
/// sprint or a query, and asking for every field of a hundred work items to render a hundred rows
/// is the request that makes a picker feel slow.
/// </remarks>
public sealed record TicketSummary(
    string ExternalId,
    string Title,
    string State,
    string WorkItemType,
    string? AssignedTo);

/// <summary>
/// Which Azure DevOps account the tickets of a project come from, and how that was decided.
/// </summary>
/// <param name="Source">
/// <c>workspace</c>, <c>project</c>, <c>only_connection</c> or <c>none</c>. Returned so the UI can
/// say <em>why</em> it is about to use an organisation — and, when it is <c>none</c>, ask instead of
/// guessing.
/// </param>
public sealed record TicketAccount(string? Org, string? Project, string Source);

/// <summary>
/// What a ticket asks for, and where that was found.
/// </summary>
/// <param name="Mode">
/// <c>list</c> when the source held an enumerable list and the items are numbered here;
/// <c>prose</c> when it is narrative and the model has to enumerate it itself; <c>none</c> when no
/// configured source carried enough content to be a requirement at all.
/// </param>
/// <param name="Field">The reference name the content came from, or <see langword="null"/>.</param>
/// <param name="Items">
/// The criteria as <c>AC-1…AC-N</c>, in <c>list</c> mode only. Empty in the other two: numbering
/// prose with a regex produces criteria cut in half, which reads as a defect in the work rather than
/// in the extraction.
/// </param>
public sealed record TicketCriteria(
    string Mode,
    string? Field,
    string Markdown,
    IReadOnlyList<string> Items);

/// <summary>A ticket a branch name appears to be about, as the renderer receives it.</summary>
public sealed record TicketSuggestion(string Provider, string ExternalId);

/// <summary>A work item address parsed out of pasted text.</summary>
/// <remarks>
/// <see cref="Org"/> and <see cref="Project"/> are null when the text was a bare id, which is the
/// fastest thing to type for a ticket you already know. The caller fills them from the workspace.
/// </remarks>
public sealed record TicketLinkRef(long Id, string? Org, string? Project);
