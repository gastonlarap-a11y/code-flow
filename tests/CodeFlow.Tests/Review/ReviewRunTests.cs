using System.Text.Json;
using CodeFlow.Ai;
using CodeFlow.Git;
using CodeFlow.Review;
using CodeFlow.Tests.Git;
using CodeFlow.Tests.Providers;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using LibGit2Sharp;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// A project-backed review, end to end over a real repository and a faked host and engine.
/// See <c>docs/business-rules/07-review-pipeline.md</c> §The review run, <c>REVIEW-018</c>–<c>REVIEW-023</c>.
/// </summary>
/// <remarks>
/// Azure rather than GitHub, deliberately: its head ref is always the source branch, so these tests
/// exercise the review itself rather than the pull-request head fetch GitHub adds on top. No network
/// and no model — the host answers from a <see cref="FakeHttpHandler"/> and the engine is a delegate.
/// </remarks>
[Collection(SerialKeychain.Name)]
public sealed class ReviewRunTests
{
    [Fact]
    public void The_coverage_line_cannot_be_read_as_saying_the_opposite()
    {
        // `diff: 34 de 52 archivos, 10 recortados` was read — reasonably — as "it only managed ten".
        // It meant it saw thirty-four and had to cut ten of those short. Whole and trimmed now add
        // up to what was seen, and each count says what it is.
        Assert.Equal(
            "diff: 52 archivos · vio 34 (24 enteros, 10 recortados) · 18 sin cambios desde la revisión anterior",
            ReviewRun.Seen(new DiffCoverage(Files: 52, Shown: 34, Excluded: 0, Omitted: 0, Truncated: 10, Carried: 18)));
    }

    [Fact]
    public void A_change_that_reached_the_model_whole_says_only_that()
    {
        Assert.Equal(
            "diff: 12 archivos · vio 12 enteros",
            ReviewRun.Seen(new DiffCoverage(Files: 12, Shown: 12, Excluded: 0, Omitted: 0, Truncated: 0, Carried: 0)));
    }

    [Fact]
    public void Everything_left_out_is_named_with_its_reason()
    {
        Assert.Equal(
            "diff: 20 archivos · vio 15 enteros · 3 excluidos por no aportar nada · 2 sin sitio en el prompt",
            ReviewRun.Seen(new DiffCoverage(Files: 20, Shown: 15, Excluded: 3, Omitted: 2, Truncated: 0, Carried: 0)));
    }

    private const string Org = "codeflow-review-tests";
    private const string ReviewBody =
        "### 🚨 [Blocker · Bug] Seguridad · F-001\nEl token viaja en la URL.\n📍 Ubicación: src/auth.ts:12\n";

    [Fact]
    public async Task A_first_review_saves_a_run_and_files_it_in_the_activity_list()
    {
        using var fixture = new Fixture();

        var text = await fixture.ReviewAsync();

        // A first review carries no banner and no history — there is nothing to reconcile against.
        // What it does carry is the stamped footer, which is the operation's own doing.
        Assert.StartsWith(ReviewBody, text, StringComparison.Ordinal);
        Assert.Contains("🤖", text, StringComparison.Ordinal);
        Assert.DoesNotContain("🔁", text, StringComparison.Ordinal);
        Assert.DoesNotContain("🕘", text, StringComparison.Ordinal);

        var run = fixture.Db.Use(c => ReviewRunStore.Get(c, Fixture.JobId));
        Assert.NotNull(run);
        Assert.Equal(1, run.Iter);
        // Stored and returned are the same single value.
        Assert.Equal(text, run.ReviewMd);

        var stored = Assert.Single(
            JsonSerializer.Deserialize(run.Findings, ReviewJsonContext.Default.ListMemoryFinding)!);
        // Forced to 1 rather than left on the parse sentinel.
        Assert.Equal(1, stored.IntroducidoEnIter);
        Assert.Equal(MemoryFinding.Open, stored.Estado);

        var meta = JsonSerializer.Deserialize(run.Meta, ReviewJsonContext.Default.ReviewMeta)!;
        Assert.Equal(fixture.HeadSha, meta.HeadSha);
        Assert.Equal("Add the thing", meta.PrTitle);
        Assert.Equal(1, meta.Iter);
    }

    [Fact]
    public async Task Reviewing_the_same_commit_again_returns_without_calling_the_model()
    {
        using var fixture = new Fixture();
        await fixture.ReviewAsync();

        var calls = fixture.EngineCalls;
        var text = await fixture.ReviewAsync(jobId: "second-job");

        Assert.Equal(
            $"🔁 Sin cambios desde la última revisión (mismo commit `{fixture.HeadSha[..8]}`). No se volvió a analizar.",
            text);

        // The whole point of the short-circuit: no model call...
        Assert.Equal(calls, fixture.EngineCalls);
        // ...no second run, and no job-history row for it either.
        Assert.Equal(1L, fixture.Db.Use(c => ReviewRunStore.Count(c, fixture.ProjectId, 7)));
        Assert.Null(fixture.Db.Use(c => ReviewRunStore.Get(c, "second-job")));
    }

    [Fact]
    public async Task A_re_review_of_a_new_commit_prepends_the_delta_banner()
    {
        using var fixture = new Fixture();
        await fixture.ReviewAsync();

        fixture.CommitMore();

        // The same finding comes back, so it persists rather than being new.
        var text = await fixture.ReviewAsync(jobId: "second-job");

        Assert.StartsWith(
            "🔁 Re-revisión (iter 1 → 2): 0 nuevos · 1 persisten · 0 resueltos\n\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(ReviewBody, text, StringComparison.Ordinal);

        var run = fixture.Db.Use(c => ReviewRunStore.Get(c, "second-job"));
        Assert.NotNull(run);
        Assert.Equal(2, run.Iter);
        // What is stored is exactly what was returned.
        Assert.Equal(text, run.ReviewMd);
    }

    [Fact]
    public async Task A_finding_that_stops_being_reported_is_resolved_and_rendered_in_the_history()
    {
        using var fixture = new Fixture();
        await fixture.ReviewAsync();

        fixture.CommitMore();
        var text = await fixture.ReviewAsync(jobId: "second-job", review: "Sin hallazgos.\n");

        Assert.StartsWith(
            "🔁 Re-revisión (iter 1 → 2): 0 nuevos · 0 persisten · 1 resueltos\n\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "### 🕘 Historial de hallazgos resueltos (trazabilidad)", text, StringComparison.Ordinal);
        Assert.Contains(
            "- `Seguridad` · src/auth.ts — introducido iter 1 · resuelto iter 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_the_user_stopped_leaves_nothing_behind()
    {
        using var fixture = new Fixture();

        var failure = await Assert.ThrowsAsync<AiRunFailedException>(
            () => fixture.ReviewAsync(fail: new AiRunFailedException($"{AiRunRegistry.CancelledMarker}stopped")));

        Assert.StartsWith(AiRunRegistry.CancelledMarker, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0L, fixture.Db.Use(c => ReviewRunStore.Count(c, fixture.ProjectId, 7)));
        Assert.Empty(fixture.Db.Use(c => JobHistory(c, fixture.ProjectId)));
    }

    [Fact]
    public async Task A_failed_run_is_filed_as_an_error_but_saves_no_memory()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<AiRunFailedException>(
            () => fixture.ReviewAsync(fail: new AiRunFailedException("the CLI exited with 1")));

        Assert.Equal(0L, fixture.Db.Use(c => ReviewRunStore.Count(c, fixture.ProjectId, 7)));

        var filed = Assert.Single(fixture.Db.Use(c => JobHistory(c, fixture.ProjectId)));
        Assert.Equal("error", filed.Status);
        Assert.Equal("the CLI exited with 1", filed.Error);
    }

    [Fact]
    public async Task A_pull_request_the_host_does_not_list_is_reported_as_missing()
    {
        using var fixture = new Fixture();

        var failure = await Assert.ThrowsAsync<ReviewException>(() => fixture.ReviewAsync(prId: 999));

        Assert.Equal("Pull request not found", failure.Message);
    }

    private static List<CodeFlow.Activity.JobHistoryEntry> JobHistory(
        Microsoft.Data.Sqlite.SqliteConnection connection, string projectId) =>
        CodeFlow.Activity.JobHistoryStore.List(connection, projectId);

    /// <summary>A linked project over a real repository, a faked Azure host and a scripted engine.</summary>
    private sealed class Fixture : IDisposable
    {
        public const string JobId = "review-job";

        private readonly TempAdoPat _pat;
        private readonly FakeHttpHandler _handler = new();
        private readonly HttpClient _http;
        private readonly GitNetwork _git = new((_, _, _) => ValueTask.CompletedTask);

        public Fixture()
        {
            _pat = new TempAdoPat(Org);
            _http = _handler.Client();

            Repo = new TempRepo();
            Repo.Write("src/auth.ts", "export const token = 1;\n");
            Repo.Commit("base", "src/auth.ts");

            using (var repository = Repo.Open())
            {
                // A branch to diff against, so the review has a real diff rather than an empty one.
                repository.Branches.Add("main", repository.Head.Tip);
                Commands.Checkout(repository, repository.Branches.Add("feature/thing", repository.Head.Tip));
            }

            Repo.Write("src/auth.ts", "export const token = 2;\n");
            Repo.Commit("change", "src/auth.ts");

            Db = new TempDatabase();
            var workspace = Db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
            var project = Db.Use(c => ProjectStore.Create(c, new NewProject(
                workspace.Id, "Repo", Repo.Path, RemoteUrl: null, "#6366f1", "folder",
                AdoOrg: Org, AdoProject: "Web", AdoRepoId: "Widget",
                GithubOwner: null, GithubRepo: null, GithubHost: null)));

            ProjectId = project.Id;

            // Every list call answers the same one pull request, however many reviews a test runs.
            _handler.When("/pullrequests", $$"""
                { "value": [ {
                  "pullRequestId": 7, "title": "Add the thing", "description": "the description",
                  "status": "active", "isDraft": false,
                  "sourceRefName": "refs/heads/feature/thing", "targetRefName": "refs/heads/main",
                  "createdBy": { "displayName": "Ada Lovelace" }, "creationDate": "2026-07-29T10:00:00Z",
                  "repository": { "name": "Widget", "project": { "name": "Web" } }
                } ] }
                """);
        }

        public TempDatabase Db { get; }

        public TempRepo Repo { get; }

        public string ProjectId { get; }

        public int EngineCalls { get; private set; }

        /// <summary>The commit the review will resolve, which is what the short-circuit compares.</summary>
        public string HeadSha
        {
            get
            {
                using var repository = Repo.Open();
                return repository.Head.Tip.Sha;
            }
        }

        public void CommitMore()
        {
            Repo.Write("src/auth.ts", "export const token = 3;\n");
            Repo.Commit("more", "src/auth.ts");
        }

        public Task<string> ReviewAsync(
            string jobId = JobId,
            long prId = 7,
            string review = ReviewBody,
            AiRunFailedException? fail = null)
        {
            AiRunner runner = (_, _, _, _) =>
            {
                EngineCalls++;
                return fail is not null
                    ? Task.FromException<AiRun>(fail)
                    // Raw model text. The operation stamps its own footer onto this before the
                    // review pipeline ever sees it, which is why the assertions below match on
                    // prefixes rather than on the whole string.
                    : Task.FromResult(new AiRun(review, SessionId: null, Model: null));
            };

            return ReviewRun.ForProjectAsync(
                Db.Handle, _http, _git, runner, ProjectId, prId, jobId, "completo",
                new AgentOverride(null, null, null), TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Db.Dispose();
            Repo.Dispose();
            _http.Dispose();
            _handler.Dispose();
            _pat.Dispose();
        }
    }
}
