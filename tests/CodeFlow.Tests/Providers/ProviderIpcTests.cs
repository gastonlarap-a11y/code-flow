using System.Net;
using System.Text.Json;
using CodeFlow.Activity;
using CodeFlow.Ai;
using CodeFlow.Ipc;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using CodeFlow.Providers.GitHub;
using CodeFlow.Tests.Git;
using CodeFlow.Tests.Ipc;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The provider commands as the renderer reaches them: over a real socket, through the real registry,
/// with a fake transport behind the GitHub client.
/// </summary>
/// <remarks>
/// <para>
/// The store and client tests next door never serialise a command's reply, so nothing in the suite
/// would notice if a command were registered under the wrong name, if a tagged union lost its
/// discriminator, or if a parameter stopped being read. Each of those compiles, passes those tests, and
/// then shows the user an empty PR panel.
/// </para>
/// <para>
/// No network and no credentials: the commands that need a token are exercised by <em>not</em> having
/// one, which is a real path — it is what a fresh install does — and the ones that do reach the API get
/// a queued response instead.
/// </para>
/// </remarks>
[Collection(SerialKeychain.Name)]
public sealed class ProviderIpcTests : IAsyncLifetime
{
    /// <summary>The names <c>renderer/src/lib/ipc/commands.ts</c> invokes.</summary>
    /// <remarks>
    /// An exact set. <c>open_external_url</c> is deliberately absent — the renderer's wrapper calls the
    /// shell's own opener — and <c>repo_web_url</c> replaces <c>open_repo_in_browser</c> for the same
    /// reason. Both are recorded as deviations in the README.
    /// </remarks>
    private static readonly string[] Expected =
    [
        "link_project_github", "link_project_ado", "unlink_project",
        "ado_list_projects", "ado_list_repos",
        "github_authenticated_user", "auto_link_project",
        "repo_web_url", "list_pull_requests", "list_pr_comment_threads", "pr_review_decision",
        "create_pull_request", "act_on_pull_request", "generate_pr_description",
        "resolve_pr_link", "pr_link_pull_request", "pr_link_comment_threads", "pr_link_decision",
        "act_on_pr_link",
    ];

    private const string Token = "provider-ipc-token";

    /// <summary>
    /// The organisation the Azure tests file their PAT under.
    /// </summary>
    /// <remarks>
    /// Unique per test, so a real connection the developer has saved is never touched and a run that dies
    /// before cleanup cannot leave a credential that looks like one of theirs. Hex and a hyphen only,
    /// which the path encoder passes through unchanged — the URL assertions read it back verbatim.
    /// </remarks>
    private readonly string _org = $"cf-test-{Guid.NewGuid():N}";

    private TempDatabase _db = null!;
    private TempRepo _repo = null!;
    private FakeHttpHandler _handler = null!;
    private HttpClient _http = null!;
    private string _endpoint = null!;
    private IIpcListener _listener = null!;
    private IpcServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serving = null!;
    private string _projectId = null!;
    private long _nextId;

    public ValueTask InitializeAsync()
    {
        _db = new TempDatabase();
        _repo = new TempRepo();

        var workspace = _db.Use(c => WorkspaceStore.Create(c, "Workspace", "folder", "#6366f1"));
        _projectId = _db.Use(c => ProjectStore.Create(
            c, WorkspaceStoreTests.NewProjectIn(workspace.Id) with { LocalPath = _repo.Path })).Id;

        _handler = new FakeHttpHandler();
        _http = _handler.Client();

        _endpoint = Ipc.TestEndpoint.Create();
        _cts = new CancellationTokenSource();
        _listener = IpcListener.Create(_endpoint);

        var registry = new CommandRegistry();
        _server = new IpcServer(registry, Token);
        registry.AddProviderCommands(_db.Handle, new AiRunRegistry(_server.PublishAsync), _http).Seal();

        _serving = _server.RunAsync(_listener, _cts.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await _server.DisposeAsync();
        await _listener.DisposeAsync();
        _cts.Dispose();
        _http.Dispose();
        _handler.Dispose();
        _repo.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void The_commands_this_slice_owns_are_registered_under_their_contract_names()
    {
        var registry = new CommandRegistry()
            .AddProviderCommands(_db.Handle, new AiRunRegistry((_, _, _) => ValueTask.CompletedTask), _http);

        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Linking_a_project_by_hand_and_unlinking_it_round_trip_through_the_wire()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "link_project_github",
            $$"""{"id":"{{_projectId}}","githubOwner":"acme","githubRepo":"widget","githubHost":"github.com"}""");

        var project = _db.Use(c => ProjectStore.Get(c, _projectId))!;
        Assert.Equal("acme", project.GithubOwner);
        Assert.Equal("github.com", project.GithubHost);

        await CallAsync(client, "unlink_project", $$"""{"id":"{{_projectId}}"}""");

        Assert.Null(_db.Use(c => ProjectStore.Get(c, _projectId))!.GithubOwner);
    }

    [Fact]
    public async Task Auto_linking_a_repo_with_no_recognisable_remote_reports_that_rather_than_failing()
    {
        // A brand-new TempRepo has no remotes at all, which is the "nothing to detect" path — and it must
        // answer, not throw, because the sidebar calls it on every project it shows.
        await using var client = await ConnectAsync();

        var result = await CallAsync(client, "auto_link_project", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal("NotDetected", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Auto_linking_a_github_remote_with_no_saved_token_asks_for_one()
    {
        // An Enterprise host rather than github.com, and not for variety: this asserts that *no*
        // token is saved, and `github-token:github.com` is exactly the key a developer's own token
        // lives under. Whoever ran the built app once made this test fail on their machine while it
        // still passed on a clean CI runner — the worst shape a test failure can have.
        var host = ConnectedEnterpriseHost();
        _repo.SetRemote("origin", $"https://{host}/acme/widget.git");

        await using var client = await ConnectAsync();

        var result = await CallAsync(client, "auto_link_project", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal("NeedsToken", result.GetProperty("status").GetString());
        Assert.Equal("github", result.GetProperty("provider").GetString());
        // The owner here — resolve_pr_link puts the host in this same field. CodeFlow 1.7.2 disagrees with
        // itself and both are reproduced.
        Assert.Equal("acme", result.GetProperty("identifier").GetString());

        // And nothing was written: a project is only linked once its token exists.
        Assert.Null(_db.Use(c => ProjectStore.Get(c, _projectId))!.GithubOwner);
    }

    [Fact]
    public async Task The_repository_web_url_is_rebuilt_from_the_remote_not_from_the_stored_link()
    {
        // The stored columns can hold an Azure repo GUID from the manual picker, so the remote is the
        // source of truth. Here they say one thing and the remote says another; the remote wins.
        _repo.SetRemote("origin", "https://github.com/acme/widget.git");
        _db.Do(c => ProjectStore.LinkGithub(c, _projectId, "stale", "stale-repo", "github.com"));

        await using var client = await ConnectAsync();

        var url = await CallAsync(client, "repo_web_url", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal("https://github.com/acme/widget", url.GetString());
    }

    [Fact]
    public async Task A_repository_whose_remote_is_not_a_known_host_says_so()
    {
        _repo.SetRemote("origin", "https://gitlab.com/acme/widget.git");

        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "repo_web_url", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal(
            "Couldn't determine this repository's web address from its remote",
            response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unlinked_project_is_refused_in_the_words_the_frontend_shows()
    {
        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "list_pull_requests", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal(
            "This project isn't linked to a pull-request host yet",
            response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_azure_linked_project_with_no_saved_pat_names_the_organisation_to_connect()
    {
        // Where slice 6 answered "Azure isn't wired up yet", the dispatch now reaches the real host and
        // stops at the missing credential — which is the same shape a GitHub-linked project gives.
        _db.Do(c => ProjectStore.LinkAdo(c, _projectId, "contoso", "Web", "api"));

        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "list_pull_requests", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal(
            "No Azure DevOps token saved for organization \"contoso\" — connect it in Settings first",
            response.GetProperty("error").GetString());

        // And nothing was attempted: the credential is checked before the client is built.
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Linking_a_project_to_azure_by_hand_writes_only_its_own_three_columns()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "link_project_ado",
            $$"""{"id":"{{_projectId}}","adoOrg":"contoso","adoProject":"Web","adoRepoId":"repo-guid"}""");

        var project = _db.Use(c => ProjectStore.Get(c, _projectId))!;
        Assert.Equal("contoso", project.AdoOrg);
        Assert.Equal("Web", project.AdoProject);
        Assert.Equal("repo-guid", project.AdoRepoId);

        // One-sided on purpose: only unlink clears both sets, which is why re-linking unlinks first.
        Assert.Null(project.GithubOwner);
    }

    [Fact]
    public async Task The_manual_dialogs_project_lookup_needs_a_pat_and_says_so()
    {
        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "ado_list_projects", """{"org":"contoso"}""");

        Assert.Equal(
            "No Azure DevOps token saved for organization \"contoso\" — connect it in Settings first",
            response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_azure_pull_request_reaches_the_panel_over_the_wire()
    {
        // The whole slice, end to end on the transport the renderer uses: an Azure-linked project, a PAT in
        // the store, and a reply the PR panel can bind to.
        _db.Do(c => ProjectStore.LinkAdo(c, _projectId, _org, "Web", "Widget"));
        using var pat = new TempAdoPat(_org);

        _handler.Json(
            """
            {"value":[{
              "pullRequestId": 7,
              "title": "Add the thing",
              "description": "the description",
              "status": "active",
              "isDraft": false,
              "sourceRefName": "refs/heads/feature/thing",
              "targetRefName": "refs/heads/main",
              "createdBy": { "displayName": "Ada Lovelace" },
              "creationDate": "2026-07-29T10:00:00Z",
              "repository": { "name": "Widget", "project": { "name": "Web" } }
            }]}
            """);

        await using var client = await ConnectAsync();
        var result = await CallAsync(client, "list_pull_requests", $$"""{"projectId":"{{_projectId}}"}""");

        var pr = Assert.Single(result.EnumerateArray().ToArray());
        Assert.Equal(7, pr.GetProperty("id").GetInt64());

        // snake_case on the wire and "azure" in the provider field, which is what drives the panel's
        // "view on Azure DevOps" link.
        Assert.Equal("feature/thing", pr.GetProperty("source_branch").GetString());
        Assert.Equal("azure", pr.GetProperty("provider").GetString());
        Assert.Equal("open", pr.GetProperty("status").GetString());
        Assert.Equal(
            $"https://dev.azure.com/{_org}/Web/_git/Widget/pullrequest/7",
            pr.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Approving_an_azure_pull_request_votes_and_files_the_decision_in_activity()
    {
        _db.Do(c => ProjectStore.LinkAdo(c, _projectId, _org, "Web", "Widget"));
        using var pat = new TempAdoPat(_org);

        _handler
            .Json("""{"authenticatedUser":{"id":"user-guid"}}""")
            .Json("{}")
            .Json(
                """
                {
                  "pullRequestId": 7, "title": "Fix login bug", "description": "", "status": "active",
                  "sourceRefName": "refs/heads/fix", "targetRefName": "refs/heads/main",
                  "createdBy": { "displayName": "Ada" }, "creationDate": "now",
                  "repository": { "name": "Widget", "project": { "name": "Web" } }
                }
                """);

        await using var client = await ConnectAsync();
        var result = await CallAsync(client, "act_on_pull_request",
            $$"""{"projectId":"{{_projectId}}","prId":7,"action":"approve","body":"looks good"}""");

        // The vote is a number on the reviewer resource, and the comment the form collected is dropped —
        // an Azure vote has nowhere to carry text. That is 1.7.2's behaviour.
        var vote = _handler.Requests[1];
        Assert.Equal(HttpMethod.Put, vote.Method);
        Assert.Equal("""{"vote":10}""", vote.Body);
        Assert.DoesNotContain("looks good", vote.Body!, StringComparison.Ordinal);

        var activity = result.GetProperty("activity");
        Assert.Equal("pr-action", activity.GetProperty("kind").GetString());
        Assert.Equal("#7 Fix login bug", activity.GetProperty("label").GetString());
        Assert.Equal("done", activity.GetProperty("status").GetString());

        // camelCase inside meta, the one shape on the wire that is not snake_case.
        Assert.Contains("\"prId\":7", activity.GetProperty("meta").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_azure_link_with_no_saved_pat_asks_for_the_organisation_rather_than_the_host()
    {
        await using var client = await ConnectAsync();

        var result = await CallAsync(client, "resolve_pr_link",
            """{"url":"https://dev.azure.com/contoso/Web/_git/Widget/pullrequest/7"}""");

        Assert.Equal("NeedsToken", result.GetProperty("status").GetString());
        Assert.Equal("azure", result.GetProperty("provider").GetString());
        Assert.Equal("contoso", result.GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task An_azure_link_whose_saved_pat_is_refused_says_so_rather_than_asking_to_connect()
    {
        // DIVERGENCE-PROV-b. The difference that matters: there *is* a PAT saved, so telling the user
        // to connect the organisation would be telling them to redo what they already did. The one
        // thing separating this from the test above is that Azure answered 401 instead of nothing
        // being stored — and before this, both landed in the same generic error.
        using var pat = new TempAdoPat(_org);
        _handler.Respond(HttpStatusCode.Unauthorized, """{"message":"TF400813"}""");

        await using var client = await ConnectAsync();
        var result = await CallAsync(client, "resolve_pr_link",
            $$"""{"url":"https://dev.azure.com/{{_org}}/Web/_git/Widget/pullrequest/7"}""");

        Assert.Equal("Expired", result.GetProperty("status").GetString());
        Assert.Equal("azure", result.GetProperty("provider").GetString());
        Assert.Equal(_org, result.GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task Listing_pull_requests_marks_a_refused_credential_for_the_sidebar()
    {
        // XLANG-012. The PR list has no structured result to carry a state in — it answers an array
        // or it rejects — so the one thing the sidebar can read is the string. The prefix is what
        // lets it offer "replace the token" instead of a Retry that will be refused identically.
        _db.Do(c => ProjectStore.LinkAdo(c, _projectId, _org, "Web", "Widget"));
        using var pat = new TempAdoPat(_org);
        _handler.Respond(HttpStatusCode.Unauthorized, """{"message":"TF400813"}""");

        await using var client = await ConnectAsync();

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await CallAsync(client, "list_pull_requests", $$"""{"projectId":"{{_projectId}}"}"""));

        Assert.Contains(AzureException.RefusedPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_your_own_pull_request_is_marked_for_the_toast()
    {
        // XLANG-013, the boundary half of DIVERGENCE-PROV-c. `act_on_pull_request` answers an outcome
        // or it rejects, so — exactly as with the PR list and XLANG-012 — the only thing the renderer
        // can read is the string. Without the prefix the toast shows GitHub's raw JSON, which is what
        // the operator met and reported.
        var host = TempGitHubToken.UniqueHost();
        _db.Do(c => ProjectStore.LinkGithub(c, _projectId, "acme", "widget", host));
        using var token = new TempGitHubToken(host);

        _handler.Respond(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Unprocessable Entity","errors":["Review Can not approve your own pull request"]}""");

        await using var client = await ConnectAsync();

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await CallAsync(client, "act_on_pull_request",
                $$"""{"projectId":"{{_projectId}}","prId":42,"action":"approve"}"""));

        Assert.Contains(GitHubException.SelfApprovalPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_action_that_fails_for_any_other_reason_carries_no_prefix()
    {
        // The guard on the test above. A prefix that appeared on every failure would be worse than
        // none: the renderer would tell a user whose token just expired that they cannot approve their
        // own pull request.
        var host = TempGitHubToken.UniqueHost();
        _db.Do(c => ProjectStore.LinkGithub(c, _projectId, "acme", "widget", host));
        using var token = new TempGitHubToken(host);

        _handler.Respond(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        await using var client = await ConnectAsync();

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await CallAsync(client, "act_on_pull_request",
                $$"""{"projectId":"{{_projectId}}","prId":42,"action":"approve"}"""));

        Assert.DoesNotContain(GitHubException.SelfApprovalPrefix, failure.Message, StringComparison.Ordinal);
        Assert.Contains("401 Unauthorized", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_azure_link_that_fails_for_any_other_reason_still_fails_as_an_error()
    {
        // The boundary of the divergence: only the credential case became a resolution state. A 404
        // keeps travelling as a rejected call, exactly as 1.7.2 leaves it.
        using var pat = new TempAdoPat(_org);
        _handler.Respond(HttpStatusCode.NotFound, """{"message":"no such repo"}""");

        await using var client = await ConnectAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () => await CallAsync(client, "resolve_pr_link",
            $$"""{"url":"https://dev.azure.com/{{_org}}/Web/_git/Widget/pullrequest/7"}"""));
    }

    [Fact]
    public async Task An_azure_link_with_no_matching_local_repo_offers_a_clone_url_without_a_git_suffix()
    {
        using var pat = new TempAdoPat(_org);

        _handler.Json(
            """
            {
              "pullRequestId": 7, "title": "t", "description": "", "status": "active",
              "sourceRefName": "refs/heads/f", "targetRefName": "refs/heads/main",
              "createdBy": { "displayName": "Ada" }, "creationDate": "now",
              "repository": { "name": "Widget", "project": { "name": "Web" } }
            }
            """);

        await using var client = await ConnectAsync();
        var result = await CallAsync(client, "resolve_pr_link",
            $$"""{"url":"https://dev.azure.com/{{_org}}/Web/_git/Widget/pullrequest/7"}""");

        Assert.Equal("NoLocalRepo", result.GetProperty("status").GetString());

        // The project and repository, not the organisation — and no ".git", unlike GitHub's.
        Assert.Equal("Web/Widget", result.GetProperty("repo_label").GetString());
        Assert.Equal(
            $"https://dev.azure.com/{_org}/Web/_git/Widget",
            result.GetProperty("clone_url").GetString());
    }

    [Fact]
    public async Task An_azure_link_binds_the_local_project_whose_remote_points_at_it()
    {
        // The second pass: nothing is linked yet, so the remotes are read and the row is mutated on the
        // first match. The link columns then carry the canonical names, not whatever the URL spelled.
        _repo.SetRemote("origin", $"https://dev.azure.com/{_org}/Web/_git/Widget");
        using var pat = new TempAdoPat(_org);

        _handler.Json(
            """
            {
              "pullRequestId": 7, "title": "t", "description": "", "status": "active",
              "sourceRefName": "refs/heads/f", "targetRefName": "refs/heads/main",
              "createdBy": { "displayName": "Ada" }, "creationDate": "now",
              "repository": { "name": "Widget", "project": { "name": "Web" } }
            }
            """);

        await using var client = await ConnectAsync();
        var result = await CallAsync(client, "resolve_pr_link",
            $$"""{"url":"https://dev.azure.com/{{_org}}/Web/_git/Widget/pullrequest/7"}""");

        Assert.Equal("Ready", result.GetProperty("status").GetString());
        Assert.Equal(_projectId, result.GetProperty("project_id").GetString());

        var project = _db.Use(c => ProjectStore.Get(c, _projectId))!;
        Assert.Equal(_org, project.AdoOrg);
        Assert.Equal("Widget", project.AdoRepoId);
    }

    [Fact]
    public async Task A_pasted_link_that_is_not_a_pull_request_resolves_rather_than_erroring()
    {
        await using var client = await ConnectAsync();

        var result = await CallAsync(client, "resolve_pr_link", """{"url":"https://example.invalid/whatever"}""");

        // A state the UI renders, not a failure — unlike the pr_link_* commands, which throw.
        Assert.Equal("Unrecognized", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_pasted_link_for_a_host_with_no_token_asks_for_one_and_names_the_host()
    {
        // Enterprise rather than github.com, for the reason given on the auto-link test above.
        var host = ConnectedEnterpriseHost();

        await using var client = await ConnectAsync();

        var result = await CallAsync(client, "resolve_pr_link", $$"""{"url":"https://{{host}}/acme/widget/pull/42"}""");

        Assert.Equal("NeedsToken", result.GetProperty("status").GetString());
        // The host, where auto_link_project puts the owner.
        Assert.Equal(host, result.GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task The_pr_link_commands_throw_on_an_unreadable_link_instead_of_reporting_a_state()
    {
        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "pr_link_decision", """{"url":"not a link"}""");

        Assert.Equal(
            "That isn't a pull-request link CodeFlow can read",
            response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_linked_project_with_no_saved_token_names_the_host_and_points_at_settings()
    {
        // A different message from github_authenticated_user's for the same condition. CodeFlow 1.7.2 has
        // both, and the settings screen and the PR panel each show their own.
        _db.Do(c => ProjectStore.LinkGithub(c, _projectId, "acme", "widget", "ghe.example.invalid"));

        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "list_pull_requests", $$"""{"projectId":"{{_projectId}}"}""");

        Assert.Equal(
            "No GitHub token saved for \"ghe.example.invalid\" — connect it in Settings first",
            response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Asking_who_a_token_belongs_to_without_one_uses_the_other_wording()
    {
        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "github_authenticated_user",
            """{"host":"ghe.example.invalid"}""");

        // The second of the two messages, verbatim — terser, no host, no pointer to Settings.
        Assert.Equal("No GitHub token saved for this host", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_missing_parameter_is_named_rather_than_crashing_the_dispatch()
    {
        await using var client = await ConnectAsync();
        var response = await SendAsync(client, "list_pull_requests", """{}""");

        Assert.Equal("missing required parameter 'projectId'", response.GetProperty("error").GetString());
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// A GitHub Enterprise host this app recognises, that no real token can be filed under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers the host in <c>github_connections</c>, which is the allow-list
    /// <c>KnownHosts.ForGitHub</c> reads — without it a remote on an unknown host is not recognised as
    /// GitHub at all, and the test would pass for the wrong reason.
    /// </para>
    /// <para>
    /// <c>.invalid</c> is reserved by RFC 2606 precisely so it can never resolve, and the label is
    /// unique per call, so nothing here can collide with a credential a developer actually has.
    /// </para>
    /// </remarks>
    private string ConnectedEnterpriseHost()
    {
        var host = $"ghe-{Guid.NewGuid():N}.invalid";
        _db.Do(c => Settings.SetSetting(c, "github_connections", $$"""[{"host":"{{host}}"}]"""));
        return host;
    }

    private Task<IpcTestClient> ConnectAsync() => IpcTestClient.ConnectAsync(_endpoint, "rpc", Token);

    private async Task<JsonElement> CallAsync(IpcTestClient client, string method, string parameters)
    {
        var response = await SendAsync(client, method, parameters);

        Assert.False(response.TryGetProperty("error", out var error),
            $"{method} failed: {(error.ValueKind == JsonValueKind.Undefined ? "" : error.GetString())}");

        return response.GetProperty("result");
    }

    private async Task<JsonElement> SendAsync(IpcTestClient client, string method, string parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        await client.SendAsync($$"""{"id":{{id}},"method":"{{method}}","params":{{parameters}}}""");

        var response = await client.ReceiveAsync();
        Assert.Equal(id, response.GetProperty("id").GetInt64());
        return response;
    }
}
