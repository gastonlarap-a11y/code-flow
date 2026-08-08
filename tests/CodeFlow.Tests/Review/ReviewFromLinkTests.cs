using CodeFlow.Ai;
using CodeFlow.Review;
using CodeFlow.Tests.Providers;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// A review reached by a pasted link alone — no clone, no project, nothing remembered.
/// See <c>docs/business-rules/07-review-pipeline.md</c> §Review from a link, <c>REVIEW-009</c>/<c>REVIEW-010</c>.
/// </summary>
[Collection(SerialKeychain.Name)]
public sealed class ReviewFromLinkTests
{
    private const string Org = "codeflow-link-tests";
    private const string Url = "https://dev.azure.com/codeflow-link-tests/Web/_git/Widget/pullrequest/7";

    [Fact]
    public async Task The_no_clone_warning_is_the_first_context_the_model_sees()
    {
        using var fixture = new Fixture();

        await fixture.ReviewAsync();

        var payload = Assert.Single(fixture.Payloads);
        Assert.Contains("PROJECT REVIEW CONTEXT:", payload, StringComparison.Ordinal);
        Assert.Contains(
            "- Modo de revisión: Esta revisión corre SIN un clon local del repositorio.",
            payload,
            StringComparison.Ordinal);
        // Ahead of the workspace's own contexts, which the fixture also enables.
        Assert.True(
            payload.IndexOf("Modo de revisión", StringComparison.Ordinal)
            < payload.IndexOf("Convenciones", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agents_own_instructions_frame_the_review_ahead_of_the_warning()
    {
        using var fixture = new Fixture();

        await fixture.ReviewAsync(agent: new AgentOverride(null, null, "Eres el revisor de seguridad."));

        var payload = Assert.Single(fixture.Payloads);
        Assert.True(
            payload.IndexOf("- Agent: Eres el revisor de seguridad.", StringComparison.Ordinal)
            < payload.IndexOf("- Modo de revisión:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_working_directory_holds_the_pull_request_and_its_diff()
    {
        using var fixture = new Fixture();

        await fixture.ReviewAsync();

        var directory = Assert.Single(Directory.GetDirectories(fixture.ReviewsRoot));
        // Every character outside [A-Za-z0-9_-] becomes a dash, so the name is legal everywhere.
        Assert.Equal("azure-codeflow-link-tests-Web-Widget-7", Path.GetFileName(directory));

        var ct = TestContext.Current.CancellationToken;
        var overview = await File.ReadAllTextAsync(Path.Combine(directory, "PULL_REQUEST.md"), ct);
        Assert.Equal(
            "# #7 Add the thing\n\n- Autor: Ada Lovelace\n- Rama origen: `feature/thing`\n"
            + "- Rama destino: `main`\n- URL: https://dev.azure.com/codeflow-link-tests/Web/_git/Widget/pullrequest/7\n\n"
            + "## Descripción\n\nthe description\n",
            overview);

        var diff = await File.ReadAllTextAsync(Path.Combine(directory, "changes.diff"), ct);
        Assert.Contains("diff --git a/src/auth.ts b/src/auth.ts", diff, StringComparison.Ordinal);
        Assert.Contains("-const token = 1;", diff, StringComparison.Ordinal);
        Assert.Contains("+const token = 2;", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_with_no_description_says_so_in_spanish()
    {
        using var fixture = new Fixture(description: "   ");

        await fixture.ReviewAsync();

        var directory = Assert.Single(Directory.GetDirectories(fixture.ReviewsRoot));
        Assert.Contains(
            "## Descripción\n\n(sin descripción)\n",
            await File.ReadAllTextAsync(
                Path.Combine(directory, "PULL_REQUEST.md"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reviewing_the_same_link_twice_reuses_its_directory_and_remembers_nothing()
    {
        using var fixture = new Fixture();

        await fixture.ReviewAsync();
        await fixture.ReviewAsync();

        // Same slug, so no accumulation of directories...
        Assert.Single(Directory.GetDirectories(fixture.ReviewsRoot));
        // ...and the model ran both times: there is no head-SHA short-circuit on this path.
        Assert.Equal(2, fixture.Payloads.Count);
    }

    [Fact]
    public async Task A_link_nothing_recognises_fails_before_any_network_call()
    {
        using var fixture = new Fixture();

        var failure = await Assert.ThrowsAsync<CodeFlow.Providers.ProviderException>(
            () => fixture.ReviewAsync(url: "https://example.com/not-a-pull-request"));

        Assert.Equal("That isn't a pull-request link CodeFlow can read", failure.Message);
        Assert.Empty(fixture.Payloads);
    }

    /// <summary>An Azure link, a faked host, a scripted engine and a throwaway reviews directory.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempAdoPat _pat;
        private readonly FakeHttpHandler _handler = new();
        private readonly HttpClient _http;

        public Fixture(string description = "the description")
        {
            _pat = new TempAdoPat(Org);
            _http = _handler.Client();
            ReviewsRoot = Directory.CreateTempSubdirectory("codeflow-pr-link-").FullName;

            Db = new TempDatabase();
            var workspace = Db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
            WorkspaceId = workspace.Id;
            Db.Do(c => ReviewContextStore.Upsert(
                c, id: null, workspace.Id, "Convenciones", "usa camelCase", enabled: true));

            _handler.When("/pullRequests/7?", $$"""
                {
                  "pullRequestId": 7, "title": "Add the thing", "description": "{{description}}",
                  "status": "active", "isDraft": false,
                  "sourceRefName": "refs/heads/feature/thing", "targetRefName": "refs/heads/main",
                  "createdBy": { "displayName": "Ada Lovelace" }, "creationDate": "2026-07-29T10:00:00Z",
                  "repository": { "name": "Widget", "project": { "name": "Web" } }
                }
                """);

            // One edited file, so the diff the review runs on is real rather than empty — an empty
            // one is rejected by the operation before the engine is ever reached.
            _handler.When("/iterations?", """{ "value": [ { "id": 1 } ] }""");
            _handler.When("/changes?", """
                { "changeEntries": [ { "changeType": "edit", "item": {
                    "path": "/src/auth.ts", "originalObjectId": "old-sha", "objectId": "new-sha",
                    "isFolder": false } } ] }
                """);
            _handler.WhenBytes("/blobs/old-sha", System.Text.Encoding.UTF8.GetBytes("const token = 1;\n"));
            _handler.WhenBytes("/blobs/new-sha", System.Text.Encoding.UTF8.GetBytes("const token = 2;\n"));
        }

        public TempDatabase Db { get; }

        public string WorkspaceId { get; }

        public string ReviewsRoot { get; }

        /// <summary>Every stdin payload the engine was handed, in call order.</summary>
        public List<string> Payloads { get; } = [];

        public Task<string> ReviewAsync(string url = Url, AgentOverride? agent = null)
        {
            AiRunner runner = (_, invocation, _, _) =>
            {
                Payloads.Add(invocation.StdinContent);
                return Task.FromResult(new AiRun("Sin hallazgos.\n", SessionId: null, Model: null));
            };

            return ReviewRun.ForLinkAsync(
                Db.Handle, _http, runner, url, "link-job", "completo", WorkspaceId,
                agent ?? new AgentOverride(null, null, null), ReviewsRoot,
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Db.Dispose();
            _http.Dispose();
            _handler.Dispose();
            _pat.Dispose();
            Directory.Delete(ReviewsRoot, recursive: true);
        }
    }
}
