using System.Text.Json;
using CodeFlow.Providers.Azure;
using CodeFlow.Security;
using CodeFlow.Tickets;
using Xunit;

namespace CodeFlow.Tests.Tickets;

/// <summary>
/// The Azure Boards client and the ticket sync, against a real organisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here writes to a board</b>, and nothing here carries a secret. The organisation is
/// named by <c>CODEFLOW_E2E_ADO_ORG</c> — an organisation name is not a credential — and the PAT is
/// read from the OS keychain, where the installed app put it. No token is committed, embedded in a
/// build, or passed through an environment variable, which is the standing rule for this repository.
/// </para>
/// <para>
/// Skipped by default so <c>dotnet test</c> behaves as it always has. Run them deliberately:
/// <code>
/// CODEFLOW_E2E_ADO_ORG=your-org dotnet test CodeFlow.slnx --configuration Release \
///   --no-build --filter "Category=E2E"
/// </code>
/// </para>
/// <para>
/// They exist because the fake transport cannot catch the two failures that actually happened while
/// building this feature: a WIQL query that returns <c>200</c> with zero rows because it lacks a
/// project clause, and an api-version suffix the server rejects. Both look like correct code.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class AzureBoardsEndToEndTests
{
    /// <summary>The organisation to run against. A name, deliberately not a secret.</summary>
    private const string OrgVariable = "CODEFLOW_E2E_ADO_ORG";

    /// <summary>An optional board project, when the first one with work items is not the one wanted.</summary>
    private const string ProjectVariable = "CODEFLOW_E2E_ADO_PROJECT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The organisation and its PAT, or a skip explaining exactly what to do.
    /// </summary>
    /// <remarks>
    /// The credential comes from <see cref="CredentialStore"/> rather than from the environment on
    /// purpose: it is the one the user already entered in the app, so these tests need no separate
    /// secret to exist anywhere, and a machine that has never connected the account simply skips.
    /// </remarks>
    private static (string Org, string Pat) Account()
    {
        var org = Environment.GetEnvironmentVariable(OrgVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(org),
            $"Needs a real Azure DevOps organisation. Set {OrgVariable} to run this.");

        Assert.SkipUnless(
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "The PAT is read from the OS credential store, which exists only on macOS and Windows.");

        var pat = CredentialStore.Get(CredentialStore.AdoPatKey(org!));
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(pat),
            // The key is named because the failure it catches in practice is a typo in the saved
            // organisation: the credential is there, under a name one character away from this one.
            $"No PAT under '{CredentialStore.AdoPatKey(org!)}' in the OS credential store. Connect "
            + $"'{org}' in CodeFlow's Settings first — these tests deliberately hold no credential "
            + "of their own, and the organisation name must match the one the token was saved under.");

        return (org!, pat!);
    }

    private static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The board project to use: the configured one, or the first that has work items.</summary>
    private static async Task<string> ProjectAsync(HttpClient http, string org, string pat)
    {
        if (Environment.GetEnvironmentVariable(ProjectVariable) is { Length: > 0 } configured)
        {
            return configured;
        }

        var projects = await AzureClient.ListProjectsAsync(http, org, pat, Ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            var ids = await AzureWorkItemClient
                .QueryIdsAsync(http, org, project.Name, condition: null, top: 1, pat, Ct)
                .ConfigureAwait(false);

            if (ids.Count > 0)
            {
                return project.Name;
            }
        }

        Assert.Skip($"No project in '{org}' returned any work item. Set {ProjectVariable} to name one.");
        return string.Empty;
    }

    [Fact]
    public async Task The_saved_pat_can_list_the_organisations_projects()
    {
        var (org, pat) = Account();
        using var http = Client();

        var projects = await AzureClient.ListProjectsAsync(http, org, pat, Ct);

        Assert.NotEmpty(projects);
    }

    [Fact]
    public async Task A_query_carrying_the_project_clause_returns_that_projects_work_items()
    {
        // `WIQL-001` from the side that is true everywhere: with the clause, a project that has
        // work items returns them. That is what the client guarantees, and what every caller
        // depends on.
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var ids = await AzureWorkItemClient.QueryIdsAsync(http, org, project, condition: null, top: 20, pat, Ct);

        Assert.NotEmpty(ids);

        // Every id really belongs to the project that was asked about.
        var items = await AzureWorkItemClient.GetWorkItemsAsync(
            http, org, project, ids, ["System.TeamProject"], pat, Ct);

        Assert.All(items, item =>
            Assert.Equal(project, item.Fields["System.TeamProject"].GetString()));
    }

    [Fact]
    public async Task Whether_an_unfiltered_query_returns_anything_is_organisation_dependent()
    {
        // <b>The measurement behind `WIQL-001`, and the reason it is a rule rather than advice.</b>
        // The project segment in the URL does not reliably filter a WIQL query, and what happens
        // without the clause differs by organisation: measured 2026-08-10, one organisation
        // returned HTTP 200 and *zero* rows on all five of its projects — indistinguishable from an
        // empty board — while another returned every work item it had. Neither answer is wrong; the
        // point is that neither can be relied on, which is why `QueryIdsAsync` cannot express the
        // clause-less form at all.
        //
        // So this asserts only what is universally true: the request succeeds and says nothing
        // dependable. A caller reading its row count is reading a coin flip.
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var url = $"https://dev.azure.com/{AzureClient.OrgSegment(org)}/{AzureClient.Encode(project)}"
            + $"/_apis/wit/wiql?$top=5&api-version={AzureClient.ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{pat}")));
        request.Content = JsonContent(
            """{"query":"SELECT [System.Id] FROM WorkItems ORDER BY [System.ChangedDate] DESC"}""");

        using var response = await http.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        // A 200 either way is the trap: the failure never announces itself.
        Assert.True(
            response.IsSuccessStatusCode,
            $"the unfiltered query is expected to succeed and merely be unreliable, got {response.StatusCode}");

        using var document = JsonDocument.Parse(body);
        Assert.Equal("flat", document.RootElement.GetProperty("queryType").GetString());
    }

    [Fact]
    public async Task A_sprints_work_items_come_back_through_the_taskboard_route()
    {
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var teams = await AzureWorkItemClient.ListTeamsAsync(http, org, project, pat, Ct);
        Assert.NotEmpty(teams);

        foreach (var team in teams)
        {
            var iterations = await AzureWorkItemClient
                .ListIterationsAsync(http, org, project, team.Name, pat, Ct);

            var iteration = iterations.FirstOrDefault(i => i.Attributes?.TimeFrame == "current")
                ?? iterations.OrderByDescending(i => i.Attributes?.StartDate ?? DateTimeOffset.MinValue).FirstOrDefault();

            if (iteration is null)
            {
                continue;
            }

            var ids = await AzureWorkItemClient
                .IterationWorkItemIdsAsync(http, org, project, team.Name, iteration.Id, pat, Ct);

            if (ids.Count > 0)
            {
                var summaries = await AzureWorkItemClient.GetWorkItemsAsync(
                    http, org, project, ids, AzureWorkItemClient.SummaryFields, pat, Ct);

                Assert.NotEmpty(summaries);
                Assert.All(summaries, item => Assert.True(item.Id > 0));
                return;
            }
        }

        Assert.Skip("No team in this project has an iteration with work items on it.");
    }

    [Fact]
    public async Task A_real_work_item_reads_back_with_its_fields_and_relations()
    {
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var ids = await AzureWorkItemClient.QueryIdsAsync(http, org, project, null, top: 1, pat, Ct);
        var item = await AzureWorkItemClient.GetWorkItemAsync(http, org, project, ids[0], pat, Ct);

        Assert.Equal(ids[0], item.Id);
        Assert.True(item.Rev > 0);
        Assert.True(item.Fields.ContainsKey("System.Title"), "every work item has a title");
        Assert.True(item.Fields.ContainsKey("System.WorkItemType"));
    }

    [Fact]
    public async Task Comments_are_readable_only_on_the_preview_contract()
    {
        // PROV-046. A plain 7.1 is rejected here; the test proves the preview suffix is still the
        // one that works rather than a stale constant nobody has re-checked.
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var ids = await AzureWorkItemClient.QueryIdsAsync(http, org, project, null, top: 1, pat, Ct);

        // Does not assert there ARE comments — most work items have none. It asserts the call is
        // accepted, which is the part that breaks when the version drifts.
        var comments = await AzureWorkItemClient.ListCommentsAsync(http, org, project, ids[0], pat, Ct);
        Assert.NotNull(comments);
    }

    [Fact]
    public async Task A_ticket_synced_from_the_real_board_produces_a_readable_mirror()
    {
        // The whole feature, end to end: fetch, extract criteria, download attachments, write the
        // mirror. Into a throwaway root, so it never touches the user's real tickets directory.
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var ids = await AzureWorkItemClient.QueryIdsAsync(http, org, project, null, top: 1, pat, Ct);
        var item = await AzureWorkItemClient.GetWorkItemAsync(http, org, project, ids[0], pat, Ct);

        var rawJson = JsonSerializer.Serialize(item, AzureWorkItemJsonContext.Default.RawWorkItem);
        using var document = JsonDocument.Parse(rawJson);
        var fields = document.RootElement.GetProperty("fields");

        var criteria = TicketCriteriaReader.Read(fields, TicketCriteriaReader.DefaultFields, []);

        var root = Path.Combine(Path.GetTempPath(), $"codeflow-e2e-{Guid.NewGuid():N}");
        try
        {
            var title = fields.TryGetProperty("System.Title", out var t) ? t.GetString()! : "sin título";
            var directory = TicketPaths.DirectoryFor(root, org, project, ids[0].ToString(), title);

            var ticket = new Ticket(
                TicketStore.IdFor("azure", org, project, ids[0].ToString()),
                "azure", org, project, ids[0].ToString(), title,
                fields.TryGetProperty("System.State", out var s) ? s.GetString()! : "",
                fields.TryGetProperty("System.WorkItemType", out var w) ? w.GetString()! : "",
                null,
                AzureWorkItemClient.WebUrl(org, project, ids[0]),
                item.Rev, directory, "2026-08-11T00:00:00.0000000+00:00");

            TicketMirror.Write(directory, ticket, criteria, rawJson, [], []);

            var page = await File.ReadAllTextAsync(Path.Combine(directory, "ticket.md"), Ct);

            Assert.Contains(title, page, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(directory, "notes")));
            // No markup survives into the copy a person and the model read.
            Assert.DoesNotContain("<div", page, StringComparison.Ordinal);
            Assert.DoesNotContain("&nbsp;", page, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The lookup the link dialog runs while somebody is still typing, and how long it takes.
    /// </summary>
    /// <remarks>
    /// The picker debounces this by 350 ms and then shows what it found. The debounce is only the
    /// right number if the call itself is fast — a batch of one id over five fields — so the elapsed
    /// time is asserted rather than assumed. A second is generous for a single row and still well
    /// under the point at which resolving-as-you-type stops feeling immediate; if this ever fails,
    /// the answer is to resolve on Enter instead of on keystroke, not to raise the bound.
    /// </remarks>
    [Fact]
    public async Task A_single_work_item_previews_fast_enough_to_resolve_while_typing()
    {
        var (org, pat) = Account();
        using var http = Client();
        var project = await ProjectAsync(http, org, pat);

        var ids = await AzureWorkItemClient.QueryIdsAsync(
            http, org, project, condition: null, top: 1, pat, Ct);

        Assert.SkipWhen(ids.Count == 0, $"'{org}/{project}' holds no work items to preview.");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var summaries = await AzureWorkItemClient.GetWorkItemsAsync(
            http, org, project, [ids[0]], AzureWorkItemClient.SummaryFields, pat, Ct);
        started.Stop();

        var only = Assert.Single(summaries);
        Assert.Equal(ids[0], only.Id);
        Assert.True(
            started.ElapsedMilliseconds < 1000,
            $"previewing one work item took {started.ElapsedMilliseconds} ms — too slow to run on a keystroke");
    }

    // -----------------------------------------------------------------------
    // Write coverage — written, and deliberately not enabled.
    //
    // Commenting on a work item and transitioning its state are specified in 14-work-items.md but
    // not built: the standing instruction is that this feature stays read-only until that is
    // revisited. The organisation these run against is a personal one where writing is allowed, so
    // when the time comes this is the file they belong in — posting a comment through
    // `POST .../comments?format=markdown&api-version=7.1-preview.4` and reading it back, then
    // patching `System.State` with a JSON Patch carrying a `test` op on `/rev` and asserting that a
    // stale rev is refused rather than silently overwriting somebody's edit.
    // -----------------------------------------------------------------------

    private static StringContent JsonContent(string body) =>
        new(body, System.Text.Encoding.UTF8, "application/json");
}
