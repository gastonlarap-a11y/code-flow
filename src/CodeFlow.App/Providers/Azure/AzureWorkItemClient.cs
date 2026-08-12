using System.Globalization;

namespace CodeFlow.Providers.Azure;

/// <summary>
/// The Azure Boards (work item) REST client.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="AzureClient"/> rather than part of it: pull requests and work items are
/// separate features that happen to share a host. What they do share is the transport —
/// <see cref="AzureClient.GetAsync"/>, <see cref="AzureClient.SendJsonAsync"/> and
/// <see cref="AzureClient.OrgSegment"/> — so a refused PAT is reported here exactly as it is there
/// (<c>DIVERGENCE-PROV-b</c>), and organisation normalisation cannot drift between the two.
/// </para>
/// <para>
/// <b>Read-only.</b> Nothing in this file writes to Azure. Commenting and state transitions are a
/// later, separately requested step; until then a bug here cannot alter anybody's board.
/// </para>
/// <para>
/// Every function takes <c>org</c>, <c>project</c> and <c>pat</c> explicitly. There is no ambient
/// credential and the PAT appears only in the <c>Authorization</c> header — never in a body, a URL
/// or anything that reaches an AI process.
/// </para>
/// </remarks>
internal static class AzureWorkItemClient
{
    /// <summary>
    /// The comments endpoints never went GA.
    /// </summary>
    /// <remarks>
    /// A plain <c>7.1</c> is rejected with a 400 demanding the suffix — measured, not assumed. This
    /// is the same trap <see cref="AzureClient"/> documents for <c>connectionData</c>, and it is the
    /// literal in this file most likely to be "tidied" into <c>7.1</c> by someone making versions
    /// consistent.
    /// </remarks>
    internal const string CommentsApiVersion = "7.1-preview.4";

    /// <summary>A team's iteration contents are also preview-only, at a different suffix.</summary>
    internal const string IterationWorkItemsApiVersion = "7.1-preview.1";

    /// <summary>
    /// Azure's ceiling on one batch read.
    /// </summary>
    /// <remarks>
    /// Documented as 200. Exceeding it fails the whole request rather than truncating, so callers
    /// never pass a raw list through — <see cref="GetWorkItemsAsync"/> chunks.
    /// </remarks>
    internal const int MaxBatchSize = 200;

    /// <summary>The fields a list row needs, and no more.</summary>
    /// <remarks>
    /// A picker showing a hundred rows does not need every custom field on every work item. Naming
    /// them is also what keeps the batch read compatible with itself: <c>fields</c> and an expand are
    /// mutually exclusive on that endpoint.
    /// </remarks>
    internal static readonly string[] SummaryFields =
    [
        "System.Id",
        "System.Title",
        "System.State",
        "System.WorkItemType",
        "System.AssignedTo",
        "System.IterationPath",
        "System.ChangedDate",
    ];

    /// <summary>One work item with every field, relation and link Azure has for it.</summary>
    /// <remarks>
    /// <c>$expand=all</c> is what returns the relations, and the relations are the only place the
    /// parent link and the attachments live — neither is a field, despite <c>System.Parent</c>
    /// appearing in the field list.
    /// </remarks>
    public static Task<RawWorkItem> GetWorkItemAsync(
        HttpClient http, string org, string project, long id, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/workitems/{id}?$expand=all&api-version={AzureClient.ApiVersion}";

        return AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.RawWorkItem, cancellationToken);
    }

    /// <summary>
    /// Many work items at once, in chunks Azure will accept.
    /// </summary>
    /// <remarks>
    /// <c>errorPolicy: "omit"</c> drops an id the credential cannot see instead of failing the batch.
    /// The ids come from a query the user did not write — a sprint's contents, "assigned to me" — and
    /// one unreadable item should cost that row, not the whole list.
    /// </remarks>
    /// <param name="fields">
    /// Reference names to return. <see langword="null"/> means every field, which is only worth
    /// asking for when the caller genuinely needs them all.
    /// </param>
    public static async Task<IReadOnlyList<RawWorkItem>> GetWorkItemsAsync(
        HttpClient http, string org, string project, IReadOnlyList<long> ids, IReadOnlyList<string>? fields,
        string pat, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/workitemsbatch?api-version={AzureClient.ApiVersion}";

        var collected = new List<RawWorkItem>(ids.Count);

        foreach (var chunk in ids.Chunk(MaxBatchSize))
        {
            var page = await AzureClient.SendJsonAsync(
                http, HttpMethod.Post, url, pat,
                new WorkItemsBatchBody(chunk, fields, "omit"),
                AzureWorkItemJsonContext.Default.WorkItemsBatchBody,
                AzureWorkItemJsonContext.Default.AzureListRawWorkItem,
                cancellationToken).ConfigureAwait(false);

            collected.AddRange(page.Value);
        }

        return collected;
    }

    /// <summary>
    /// Runs a WIQL query scoped to one project and returns the ids it matched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>WIQL-001</c>: the project clause is not optional, and this signature is why.</b> A query
    /// is assembled here from a <paramref name="condition"/> rather than accepted whole, so no caller
    /// can send one without <c>WHERE [System.TeamProject] = …</c>.
    /// <para>
    /// <b>The project segment in the URL does not reliably filter the query, and what it does
    /// instead varies by organisation.</b> Measured on 2026-08-10 against two real ones: in the
    /// first, the clause-less query posted to the project-scoped URL returned <c>HTTP 200</c> with a
    /// well-formed body, the right <c>queryType</c> and columns — and <em>zero</em> rows, on all five
    /// of its projects. In the second, the same request returned every work item the project had.
    /// Neither answer is an error; the point is that neither is dependable, and the first is
    /// indistinguishable from "this board is empty". A rule nobody can forget beats a rule that only
    /// bites on somebody else's organisation.
    /// </para>
    /// </para>
    /// <para>
    /// The response carries ids whatever the <c>SELECT</c> names, so callers follow this with
    /// <see cref="GetWorkItemsAsync"/>. That is Azure's behaviour, not a shortcut taken here.
    /// </para>
    /// </remarks>
    /// <param name="condition">
    /// An extra WIQL predicate ANDed with the project clause, or <see langword="null"/> for all of
    /// the project's work items. Caller-supplied text: it is a query language, so anything reaching
    /// it must come from this codebase, never from a user's free typing.
    /// </param>
    /// <param name="top">How many ids to ask for. Azure applies it server-side.</param>
    public static async Task<IReadOnlyList<long>> QueryIdsAsync(
        HttpClient http, string org, string project, string? condition, int top,
        string pat, CancellationToken cancellationToken)
    {
        var where = $"[System.TeamProject] = '{EscapeLiteral(project)}'";
        if (!string.IsNullOrWhiteSpace(condition))
        {
            where += $" AND ({condition})";
        }

        var query = $"SELECT [System.Id] FROM WorkItems WHERE {where} ORDER BY [System.ChangedDate] DESC";

        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/wiql?$top={top.ToString(CultureInfo.InvariantCulture)}"
            + $"&api-version={AzureClient.ApiVersion}";

        var result = await AzureClient.SendJsonAsync(
            http, HttpMethod.Post, url, pat,
            new WiqlBody(query),
            AzureWorkItemJsonContext.Default.WiqlBody,
            AzureWorkItemJsonContext.Default.WiqlResult,
            cancellationToken).ConfigureAwait(false);

        return result.WorkItems?.Select(reference => reference.Id).ToList() ?? [];
    }

    /// <summary>The WIQL predicate for "assigned to whoever this PAT belongs to".</summary>
    /// <remarks><c>@Me</c> resolves server-side against the token's identity, so nothing here has to
    /// look up a user id first.</remarks>
    public const string AssignedToMe = "[System.AssignedTo] = @Me";

    /// <summary>Every team in a project.</summary>
    /// <remarks>The teams endpoint is organisation-scoped with the project as a path parameter, not
    /// project-scoped like the rest of this file — hence the different URL shape.</remarks>
    public static async Task<IReadOnlyList<AdoTeam>> ListTeamsAsync(
        HttpClient http, string org, string project, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}"
            + $"/_apis/projects/{AzureClient.Encode(project)}/teams?api-version={AzureClient.ApiVersion}";

        var response = await AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.AzureListAdoTeam, cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    /// <summary>A team's iterations, as its board is configured.</summary>
    public static async Task<IReadOnlyList<AdoIteration>> ListIterationsAsync(
        HttpClient http, string org, string project, string team, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/{AzureClient.Encode(team)}/_apis/work/teamsettings/iterations"
            + $"?api-version={AzureClient.ApiVersion}";

        var response = await AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.AzureListAdoIteration, cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    /// <summary>
    /// The work items on one of a team's iterations — what the taskboard shows.
    /// </summary>
    /// <remarks>
    /// The route that makes the ticket picker usable. A real sprint measured against this held 46
    /// items where the project as a whole held thousands, and it is also the list the user is already
    /// looking at in the browser when they go to link a branch.
    /// </remarks>
    public static async Task<IReadOnlyList<long>> IterationWorkItemIdsAsync(
        HttpClient http, string org, string project, string team, string iterationId,
        string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/{AzureClient.Encode(team)}/_apis/work/teamsettings/iterations/{AzureClient.Encode(iterationId)}"
            + $"/workitems?api-version={IterationWorkItemsApiVersion}";

        var response = await AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.IterationWorkItems, cancellationToken)
            .ConfigureAwait(false);

        return response.WorkItemRelations?
            .Select(relation => relation.Target?.Id)
            .OfType<long>()
            .ToList() ?? [];
    }

    /// <summary>Every work item type the project's process declares.</summary>
    public static async Task<IReadOnlyList<AdoWorkItemType>> ListWorkItemTypesAsync(
        HttpClient http, string org, string project, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/workitemtypes?api-version={AzureClient.ApiVersion}";

        var response = await AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.AzureListAdoWorkItemType, cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    /// <summary>
    /// The fields one work item type declares.
    /// </summary>
    /// <remarks>
    /// This is how the app answers "does this type even have acceptance criteria" without guessing.
    /// Measured against a real customised process: the field exists on 8 of 33 types, and
    /// <c>Technical Story</c> — a type in daily use there — is not one of them.
    /// </remarks>
    public static async Task<IReadOnlyList<AdoTypeField>> ListTypeFieldsAsync(
        HttpClient http, string org, string project, string workItemType, string pat,
        CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/workitemtypes/{AzureClient.Encode(workItemType)}/fields"
            + $"?api-version={AzureClient.ApiVersion}";

        var response = await AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.AzureListAdoTypeField, cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    /// <summary>A work item's comments, oldest first as Azure returns them.</summary>
    public static async Task<IReadOnlyList<RawWorkItemComment>> ListCommentsAsync(
        HttpClient http, string org, string project, long id, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/workItems/{id}/comments?api-version={CommentsApiVersion}";

        var response = await AzureClient.GetAsync(
            http, url, pat, AzureWorkItemJsonContext.Default.WorkItemComments, cancellationToken)
            .ConfigureAwait(false);

        return response.Comments ?? [];
    }

    /// <summary>
    /// One attachment's bytes, from the URL its relation carries.
    /// </summary>
    /// <remarks>
    /// The relation's <c>url</c> is used as given rather than rebuilt from the attachment id: it
    /// already names the right organisation, and an image embedded in a description can point at an
    /// attachment created on another work item. <c>download=true</c> asks for the content rather than
    /// the metadata.
    /// </remarks>
    public static Task<byte[]> GetAttachmentAsync(
        HttpClient http, string attachmentUrl, string fileName, string pat, CancellationToken cancellationToken)
    {
        var separator = attachmentUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{attachmentUrl}{separator}fileName={AzureClient.Encode(fileName)}"
            + $"&download=true&api-version={AzureClient.ApiVersion}";

        return AzureClient.GetBytesAsync(http, url, pat, cancellationToken);
    }

    /// <summary>Where a person opens this work item in a browser.</summary>
    /// <remarks>
    /// The modern form. A work item's own <c>_links.html</c> answers with the older
    /// <c>wi.aspx?…&amp;id=</c> shape, which still redirects but is not what anyone would recognise
    /// if they saw it in a link.
    /// </remarks>
    public static string WebUrl(string org, string project, long id) =>
        $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}/_workitems/edit/{id}";

    /// <summary>
    /// Escapes a value going inside a WIQL string literal.
    /// </summary>
    /// <remarks>
    /// WIQL quotes with <c>'</c> and escapes it by doubling, as SQL does. Project names carrying an
    /// apostrophe are rare and would otherwise produce a syntax error that reads as an API fault.
    /// </remarks>
    private static string EscapeLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
