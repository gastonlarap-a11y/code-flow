using System.Net;
using System.Text.Json;
using CodeFlow.Providers.Azure;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The Azure Boards client: the requests it builds, and what it does with the replies.
/// </summary>
/// <remarks>
/// Runs entirely against <see cref="FakeHttpHandler"/> — no network, no organisation, no PAT. Several
/// of these assert an exact literal (an api-version suffix, a WIQL clause) because those are the
/// values that fail silently rather than loudly when they drift.
/// </remarks>
public sealed class AzureWorkItemClientTests
{
    private const string Pat = "ado-test-pat";

    private const string Org = "contoso";

    private const string Project = "Web";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WorkItem(long id = 426647, string type = "Product Backlog Item") =>
        $$"""
        {
          "id": {{id}},
          "rev": 23,
          "fields": {
            "System.Title": "TRANSFORMACIONES - FEEDBACK FLUJO AVRO",
            "System.State": "Ready to Test",
            "System.WorkItemType": "{{type}}",
            "System.Description": "<div>real content</div>",
            "Microsoft.VSTS.Common.AcceptanceCriteria": "<div><b>-</b> </div>",
            "Custom.Funcionamiento": "¿Qué debe hacer el proceso?",
            "System.AssignedTo": { "displayName": "Ada Lovelace" },
            "System.CommentCount": 4
          },
          "relations": [
            { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.com/x/_apis/wit/workItems/1" },
            {
              "rel": "AttachedFile",
              "url": "https://dev.azure.com/x/_apis/wit/attachments/abc-123",
              "attributes": { "name": "captura.png" }
            }
          ]
        }
        """;

    // ---------- WIQL-001: the project clause is not optional ----------

    [Fact]
    public async Task Every_query_names_the_project_in_its_where_clause()
    {
        // WIQL-001. Measured against a real organisation: without this clause the project-scoped URL
        // still answers 200, with the right queryType and columns, and zero rows — on every project.
        // The failure is indistinguishable from "no work items", so the clause is built in here
        // rather than left to each caller to remember.
        using var handler = new FakeHttpHandler().Json("""{ "workItems": [ { "id": 7 } ] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.QueryIdsAsync(http, Org, Project, condition: null, top: 5, Pat, Ct);

        Assert.Contains("[System.TeamProject] = 'Web'", SentQuery(handler), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_extra_condition_is_anded_with_the_project_clause_not_substituted_for_it()
    {
        using var handler = new FakeHttpHandler().Json("""{ "workItems": [] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.QueryIdsAsync(
            http, Org, Project, AzureWorkItemClient.AssignedToMe, top: 50, Pat, Ct);

        var query = SentQuery(handler);
        Assert.Contains("[System.TeamProject] = 'Web'", query, StringComparison.Ordinal);
        Assert.Contains("AND ([System.AssignedTo] = @Me)", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_name_carrying_an_apostrophe_is_escaped_for_the_literal()
    {
        // WIQL quotes with ' and escapes by doubling, as SQL does. Unescaped, this is a syntax error
        // that surfaces as an opaque 400.
        using var handler = new FakeHttpHandler().Json("""{ "workItems": [] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.QueryIdsAsync(http, Org, "O'Brien", condition: null, top: 5, Pat, Ct);

        Assert.Contains("= 'O''Brien'", SentQuery(handler), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_returns_the_ids_whatever_the_select_asked_for()
    {
        using var handler = new FakeHttpHandler().Json("""{ "workItems": [ { "id": 11 }, { "id": 22 } ] }""");
        using var http = handler.Client();

        var ids = await AzureWorkItemClient.QueryIdsAsync(http, Org, Project, null, 5, Pat, Ct);

        Assert.Equal([11L, 22L], ids);
    }

    [Fact]
    public async Task A_query_that_matched_nothing_is_an_empty_list_not_a_failure()
    {
        using var handler = new FakeHttpHandler().Json("""{ "queryType": "flat", "columns": [] }""");
        using var http = handler.Client();

        Assert.Empty(await AzureWorkItemClient.QueryIdsAsync(http, Org, Project, null, 5, Pat, Ct));
    }

    // ---------- api-version literals that fail silently when they drift ----------

    [Fact]
    public async Task Comments_are_pinned_to_the_preview_contract()
    {
        // A plain 7.1 is rejected with a 400 demanding the suffix. This is the literal in the client
        // most likely to be "tidied" into consistency with its neighbours.
        using var handler = new FakeHttpHandler().Json("""{ "comments": [] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.ListCommentsAsync(http, Org, Project, 426647, Pat, Ct);

        Assert.Equal("7.1-preview.4", AzureWorkItemClient.CommentsApiVersion);
        Assert.Contains("api-version=7.1-preview.4", handler.Only.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_iterations_work_item_list_is_pinned_to_its_own_preview_contract()
    {
        using var handler = new FakeHttpHandler().Json("""{ "workItemRelations": [] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.IterationWorkItemIdsAsync(http, Org, Project, "Team", "iter-1", Pat, Ct);

        Assert.Equal("7.1-preview.1", AzureWorkItemClient.IterationWorkItemsApiVersion);
        Assert.Contains("api-version=7.1-preview.1", handler.Only.Uri.Query, StringComparison.Ordinal);
    }

    // ---------- the batch read ----------

    [Fact]
    public async Task A_batch_larger_than_azures_ceiling_is_split_rather_than_rejected()
    {
        // Azure fails the whole request past 200 instead of truncating, so exceeding it loses
        // everything rather than the tail.
        using var handler = new FakeHttpHandler()
            .Json("""{ "count": 0, "value": [] }""")
            .Json("""{ "count": 0, "value": [] }""");
        using var http = handler.Client();

        var ids = Enumerable.Range(1, 301).Select(i => (long)i).ToList();
        await AzureWorkItemClient.GetWorkItemsAsync(http, Org, Project, ids, null, Pat, Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(200, CountIds(handler.Requests[0].Body!));
        Assert.Equal(101, CountIds(handler.Requests[1].Body!));
    }

    [Fact]
    public async Task A_batch_omits_unreadable_ids_instead_of_failing_the_whole_list()
    {
        using var handler = new FakeHttpHandler().Json("""{ "count": 0, "value": [] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.GetWorkItemsAsync(http, Org, Project, [1L], null, Pat, Ct);

        Assert.Contains("\"errorPolicy\":\"omit\"", handler.Only.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_batch_asks_nothing_of_the_network()
    {
        using var handler = new FakeHttpHandler();
        using var http = handler.Client();

        Assert.Empty(await AzureWorkItemClient.GetWorkItemsAsync(http, Org, Project, [], null, Pat, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_summary_batch_asks_only_for_the_fields_a_row_shows()
    {
        using var handler = new FakeHttpHandler().Json("""{ "count": 0, "value": [] }""");
        using var http = handler.Client();

        await AzureWorkItemClient.GetWorkItemsAsync(
            http, Org, Project, [1L], AzureWorkItemClient.SummaryFields, Pat, Ct);

        Assert.Contains("System.Title", handler.Only.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("$expand", handler.Only.Body, StringComparison.Ordinal);
    }

    // ---------- reading a work item ----------

    [Fact]
    public async Task A_work_items_fields_survive_their_dots_and_their_custom_names()
    {
        // The reason `fields` is a dictionary: a customised process adds as many fields as it likes,
        // and none of these keys is a legal C# member name.
        using var handler = new FakeHttpHandler().Json(WorkItem());
        using var http = handler.Client();

        var item = await AzureWorkItemClient.GetWorkItemAsync(http, Org, Project, 426647, Pat, Ct);

        Assert.Equal(426647, item.Id);
        Assert.Equal(23, item.Rev);
        Assert.Equal("Ready to Test", item.Fields["System.State"].GetString());
        Assert.Equal("<div><b>-</b> </div>", item.Fields["Microsoft.VSTS.Common.AcceptanceCriteria"].GetString());
        Assert.True(item.Fields.ContainsKey("Custom.Funcionamiento"));

        // Not every value is a string, which is why they stay as JsonElement.
        Assert.Equal(4, item.Fields["System.CommentCount"].GetInt32());
        Assert.Equal("Ada Lovelace", item.Fields["System.AssignedTo"].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task The_parent_and_the_attachments_come_from_relations_not_from_fields()
    {
        using var handler = new FakeHttpHandler().Json(WorkItem());
        using var http = handler.Client();

        var item = await AzureWorkItemClient.GetWorkItemAsync(http, Org, Project, 426647, Pat, Ct);

        Assert.DoesNotContain("System.Parent", item.Fields.Keys);

        var relations = item.Relations!;
        Assert.Contains(relations, r => r.Rel == "System.LinkTypes.Hierarchy-Reverse");

        var attachment = Assert.Single(relations, r => r.Rel == "AttachedFile");
        Assert.Equal("captura.png", attachment.Attributes!.Name);
    }

    [Fact]
    public async Task Reading_one_work_item_asks_for_everything_it_has()
    {
        using var handler = new FakeHttpHandler().Json(WorkItem());
        using var http = handler.Client();

        await AzureWorkItemClient.GetWorkItemAsync(http, Org, Project, 426647, Pat, Ct);

        Assert.Contains("$expand=all", handler.Only.Uri.Query, StringComparison.Ordinal);
    }

    // ---------- the taskboard route ----------

    [Fact]
    public async Task An_iterations_work_items_are_read_out_of_its_relations()
    {
        using var handler = new FakeHttpHandler().Json(
            """
            {
              "workItemRelations": [
                { "target": { "id": 426647 } },
                { "target": { "id": 428194 } },
                { "rel": "System.LinkTypes.Hierarchy-Forward" }
              ]
            }
            """);
        using var http = handler.Client();

        var ids = await AzureWorkItemClient.IterationWorkItemIdsAsync(
            http, Org, Project, "Team", "iter-1", Pat, Ct);

        // The entry with no target is the iteration's own root row, and it is not a work item.
        Assert.Equal([426647L, 428194L], ids);
    }

    // ---------- shared transport behaviour ----------

    [Fact]
    public async Task A_refused_credential_is_marked_the_same_way_the_pull_request_client_marks_it()
    {
        // The reason this client goes through AzureClient's transport instead of its own: the
        // 401/403 marking of DIVERGENCE-PROV-b is what the settings screen branches on.
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, "nope");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureWorkItemClient.GetWorkItemAsync(http, Org, Project, 1, Pat, Ct));

        Assert.True(failure.Unauthorized);
    }

    [Fact]
    public async Task A_project_name_with_spaces_is_percent_encoded_into_the_path()
    {
        // "Ficha Clinica - Coding Discovery" is a real project name. Sent raw, the request 404s and
        // the result reads as "that project has no work items".
        using var handler = new FakeHttpHandler().Json(WorkItem());
        using var http = handler.Client();

        await AzureWorkItemClient.GetWorkItemAsync(http, Org, "Ficha Clinica - Coding Discovery", 1, Pat, Ct);

        Assert.Contains("Ficha%20Clinica", handler.Only.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", handler.Only.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attachment_is_downloaded_from_the_url_its_relation_carries()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        using var handler = new FakeHttpHandler()
            .WhenBytes("_apis/wit/attachments/abc-123", bytes);
        using var http = handler.Client();

        var downloaded = await AzureWorkItemClient.GetAttachmentAsync(
            http, "https://dev.azure.com/x/_apis/wit/attachments/abc-123", "captura.png", Pat, Ct);

        Assert.Equal(bytes, downloaded);
        Assert.Contains("download=true", handler.Only.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_url_is_the_modern_form_a_person_would_recognise()
    {
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_workitems/edit/426647",
            AzureWorkItemClient.WebUrl(Org, Project, 426647));
    }

    [Fact]
    public void An_organisation_saved_as_a_url_still_produces_a_usable_path()
    {
        // NormalizeOrg is shared with the pull-request client, so a connection saved as a browser URL
        // works here too. Azure's server rejects a literal ':' anywhere in a request path.
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_workitems/edit/1",
            AzureWorkItemClient.WebUrl("https://dev.azure.com/contoso", Project, 1));
    }

    private static int CountIds(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("ids").GetArrayLength();

    /// <summary>
    /// The WIQL query as Azure will read it, not as it sits on the wire.
    /// </summary>
    /// <remarks>
    /// <c>System.Text.Json</c>'s default encoder escapes <c>'</c> to <c>'</c>, so asserting
    /// against the raw body would fail on a query that is perfectly correct — the server decodes it
    /// before the query parser ever sees it. Parsing here asserts what actually gets executed.
    /// </remarks>
    private static string SentQuery(FakeHttpHandler handler) =>
        JsonDocument.Parse(handler.Only.Body!).RootElement.GetProperty("query").GetString()!;
}
