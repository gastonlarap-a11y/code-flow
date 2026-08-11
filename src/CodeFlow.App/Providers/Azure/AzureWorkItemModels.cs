using System.Text.Json;

namespace CodeFlow.Providers.Azure;

/// <summary>
/// One work item as Azure Boards reports it.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Fields"/> is a dictionary and cannot be anything else.</b> Azure keys every field by
/// its reference name — <c>System.Title</c>, <c>Microsoft.VSTS.Common.AcceptanceCriteria</c> — and a
/// process can add as many of its own as it likes. One real organisation this was measured against
/// carries sixteen <c>Custom.*</c> fields on a Product Backlog Item, four of them named by GUID. A
/// record with properties would drop every one of them, and the dots are not valid in a C# member
/// name anyway.
/// </para>
/// <para>
/// <see cref="JsonElement"/> rather than <c>string</c> because the values are not all text:
/// <c>System.AssignedTo</c> is an identity object, <c>System.Rev</c> a number,
/// <c>System.CommentCount</c> an integer. Reading one as the wrong type is then a caller's decision
/// at the point it matters, not a deserialisation failure that loses the whole work item.
/// </para>
/// </remarks>
internal sealed record RawWorkItem(
    long Id,
    int Rev,
    IReadOnlyDictionary<string, JsonElement> Fields,
    IReadOnlyList<RawWorkItemRelation>? Relations);

/// <summary>
/// A link from a work item to something else.
/// </summary>
/// <remarks>
/// The parent is here rather than in <see cref="RawWorkItem.Fields"/>: Azure models hierarchy as a
/// relation with <c>rel = "System.LinkTypes.Hierarchy-Reverse"</c>, and there is no
/// <c>System.Parent</c> field to read despite the name appearing in the field list. Attachments
/// arrive the same way, as <c>rel = "AttachedFile"</c>.
/// </remarks>
internal sealed record RawWorkItemRelation(string Rel, string Url, RelationAttributes? Attributes);

/// <summary>A relation's own metadata — the attachment's file name, mostly.</summary>
internal sealed record RelationAttributes(string? Name, string? Comment);

/// <summary>The body of a WIQL query.</summary>
internal sealed record WiqlBody(string Query);

/// <summary>
/// What WIQL answers with: identifiers, and nothing else.
/// </summary>
/// <remarks>
/// The <c>SELECT</c> clause is ignored — whatever columns it names, the response carries ids — so
/// every query is followed by a batch read. That is Azure's documented behaviour, not a limitation of
/// this client.
/// </remarks>
internal sealed record WiqlResult(IReadOnlyList<WiqlWorkItemRef>? WorkItems);

/// <summary>One id from a WIQL result.</summary>
internal sealed record WiqlWorkItemRef(long Id);

/// <summary>The body of a batch read.</summary>
/// <param name="Fields">
/// The reference names to return. Mutually exclusive with an expand, which is why the batch path
/// never asks for relations: a picker list wants a title and a state, not every link.
/// </param>
/// <param name="ErrorPolicy">
/// <c>Omit</c> drops an id the caller cannot see instead of failing the whole batch — the right
/// choice for a list assembled from a query the user did not write.
/// </param>
internal sealed record WorkItemsBatchBody(
    IReadOnlyList<long> Ids,
    IReadOnlyList<string>? Fields,
    string? ErrorPolicy);

/// <summary>A team inside a project.</summary>
internal sealed record AdoTeam(string Id, string Name);

/// <summary>
/// One of a team's iterations.
/// </summary>
/// <remarks>
/// Named <c>Ado</c>-something rather than <c>RawIteration</c>, which already means a pull request's
/// iteration in <c>AzureModels.cs</c>. Two unrelated concepts share the word in Azure's own
/// vocabulary; they must not share a type name here.
/// </remarks>
internal sealed record AdoIteration(string Id, string Name, string? Path, AdoIterationAttributes? Attributes);

/// <summary>An iteration's dates. <c>TimeFrame</c> is <c>past</c>, <c>current</c> or <c>future</c>.</summary>
internal sealed record AdoIterationAttributes(
    DateTimeOffset? StartDate,
    DateTimeOffset? FinishDate,
    string? TimeFrame);

/// <summary>The work items on a team's iteration — the taskboard's own contents.</summary>
internal sealed record IterationWorkItems(IReadOnlyList<WorkItemRelationRef>? WorkItemRelations);

/// <summary>One entry of an iteration's work-item list.</summary>
internal sealed record WorkItemRelationRef(WorkItemTarget? Target);

/// <summary>The work item an iteration entry points at.</summary>
internal sealed record WorkItemTarget(long Id);

/// <summary>A work item type declared by the project's process.</summary>
internal sealed record AdoWorkItemType(string Name, string ReferenceName);

/// <summary>A field declared on a work item type.</summary>
internal sealed record AdoTypeField(string ReferenceName, string? Name);

/// <summary>One comment on a work item.</summary>
/// <remarks>
/// Read-only here. The comments endpoints are the one part of this client pinned to
/// <c>7.1-preview.4</c> rather than <c>7.1</c>.
/// </remarks>
internal sealed record RawWorkItemComment(
    long Id,
    string? Text,
    RawIdentity? CreatedBy,
    DateTimeOffset? CreatedDate);

/// <summary>A page of work-item comments.</summary>
internal sealed record WorkItemComments(IReadOnlyList<RawWorkItemComment>? Comments);
