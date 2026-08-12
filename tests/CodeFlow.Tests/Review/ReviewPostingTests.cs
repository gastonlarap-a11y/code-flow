using System.Net;
using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Review;
using CodeFlow.Tests.Providers;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Review;

/// <summary>
/// Publishing a saved run's selected findings.
/// See <c>docs/business-rules/07-review-pipeline.md</c> <c>REVIEW-032</c>–<c>REVIEW-035</c>.
/// </summary>
/// <remarks>
/// Azure rather than GitHub for the flow itself: its posting needs no head-SHA prefetch, so these
/// cases exercise the reconciliation and the bookkeeping rather than one provider's preamble. The
/// per-provider requests are covered by <c>AzurePostingTests</c> and <c>GitHubPostingTests</c>.
/// Everything here is <c>UNVERIFIED</c> against a real API, and offline by construction.
/// </remarks>
[Collection(SerialKeychain.Name)]
public sealed class ReviewPostingTests
{
    private const string Org = "codeflow-posting-tests";

    [Fact]
    public async Task An_unposted_finding_opens_a_thread_and_is_recorded_as_posted()
    {
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(Item("src/auth.ts", "Seguridad", "el token viaja en la URL"));

        var stored = Assert.Single(fixture.Findings());
        Assert.Equal(501, stored.ThreadId);
        Assert.Equal(MemoryFinding.Posted, stored.Estado);

        // The full comment markdown, only ever used when opening a thread.
        var body = fixture.LastBody();
        Assert.Equal(
            "el token viaja en la URL",
            body.GetProperty("comments").EnumerateArray().Single().GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_finding_that_already_has_a_thread_gets_a_follow_up_instead()
    {
        using var fixture = new Fixture(
            Finding("F-001", "src/auth.ts", "Seguridad") with { ThreadId = 501, Estado = MemoryFinding.Posted });
        fixture.OnPost("");

        await fixture.PublishAsync(Item("src/auth.ts", "Seguridad", "el token viaja en la URL"));

        Assert.Contains("/threads/501/comments", fixture.LastUri, StringComparison.Ordinal);
        // VERBATIM, and Azure's own wording — italicised, unlike GitHub's.
        Assert.Equal(
            $"➡️ _Sigue presente en la iteración 3 — {Today}._",
            fixture.LastBody().GetProperty("content").GetString());

        // The thread it already had is unchanged, and so is its state.
        var stored = Assert.Single(fixture.Findings());
        Assert.Equal(501, stored.ThreadId);
        Assert.Equal(MemoryFinding.Posted, stored.Estado);
    }

    [Fact]
    public async Task A_resolved_finding_is_replied_to_and_its_thread_marked_fixed()
    {
        using var fixture = new Fixture(
            Finding("F-001", "src/auth.ts", "Seguridad") with { ThreadId = 501, Estado = MemoryFinding.Resolved });
        fixture.OnPost("");

        await fixture.PublishAsync(Item("src/auth.ts", "Seguridad", "ya no está"));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(
            $"✔️ _Resuelto en la iteración 3 — {Today}. Marcado como fixed._",
            JsonDocument.Parse(fixture.Requests[0].Body!).RootElement.GetProperty("content").GetString());

        var patch = fixture.Requests[1];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal(2, JsonDocument.Parse(patch.Body!).RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_finding_that_was_already_resolved_stays_resolved_when_it_is_first_posted()
    {
        using var fixture = new Fixture(
            Finding("F-001", "src/auth.ts", "Seguridad") with { Estado = MemoryFinding.Resolved });
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(Item("src/auth.ts", "Seguridad", "hallazgo"));

        var stored = Assert.Single(fixture.Findings());
        Assert.Equal(501, stored.ThreadId);
        // Only an open finding becomes posted. One resolved before anyone published it keeps that.
        Assert.Equal(MemoryFinding.Resolved, stored.Estado);
    }

    [Fact]
    public async Task A_finding_with_no_location_posts_as_a_conversation_comment()
    {
        using var fixture = new Fixture(Finding("F-001", archivo: null, "Estilo"));
        fixture.OnPost("""{"id":502}""");

        await fixture.PublishAsync(new PostFindingItem(null, "Estilo", "sin ubicación", Location: null));

        // No iteration lookup and no threadContext: there is nothing to anchor to.
        Assert.Single(fixture.Requests);
        Assert.False(fixture.LastBody().TryGetProperty("threadContext", out _));
    }

    [Fact]
    public async Task Two_findings_that_collide_on_identity_each_open_their_own_thread()
    {
        // BUG-REVIEW-b's posting half, fixed after parity. The identity key is not injective, so the
        // second item used to match the same stored finding as the first and — that finding having
        // just been given a thread — reply into it. Two unrelated findings ended up as one
        // conversation: a reviewer reading the thread saw a reply that answered nothing above it.
        //
        // One stored finding, two selected items: the first claims it, the second matches nothing
        // and opens its own thread, which is what an item with no stored counterpart already did.
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(
            Item("src/auth.ts", "Seguridad", "el token viaja en la URL"),
            Item("src/auth.ts", "Seguridad", "la cookie no es HttpOnly"));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.All(fixture.Requests, request =>
            Assert.EndsWith("/threads?api-version=7.1", request.Uri.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_matching_no_stored_finding_posts_and_leaves_no_record()
    {
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":900}""");

        await fixture.PublishAsync(Item("src/other.ts", "Rendimiento", "otro hallazgo"));

        // The comment went out, and the application has no record of the thread it opened: a later
        // post would open another. A documented edge case, not a marker.
        Assert.Single(fixture.Requests);
        Assert.Null(Assert.Single(fixture.Findings()).ThreadId);
    }

    [Fact]
    public async Task A_run_nobody_stored_posts_everything_as_new()
    {
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(runId: "no-such-run", Item("src/auth.ts", "Seguridad", "hallazgo"));

        // Not an error: no stored findings and iteration 1, so the item opens a thread.
        Assert.Single(fixture.Requests);
        // And the real run is untouched, because the write-back targets the id that was asked for.
        Assert.Null(Assert.Single(fixture.Findings()).ThreadId);
    }

    [Fact]
    public async Task On_azure_the_summary_is_posted_last_so_that_it_reads_first()
    {
        // Order is the assertion, and which order depends on the host. The goal never changes — the
        // summary is the first thing read, not a postscript to the findings it introduces — but
        // Azure's overview shows the newest thread at the top, so getting there means posting it
        // last. `DIVERGENCE-PROV-d`; `ReviewPostingFromLinkTests` covers the same for the link path.
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(
            postSummary: true, summary: "2 hallazgos, 1 bloqueante",
            Item("src/auth.ts", "Seguridad", "hallazgo"));

        Assert.Equal(2, fixture.Requests.Count);
        Assert.Contains("hallazgo", fixture.Requests[0].Body!, StringComparison.Ordinal);
        Assert.Equal(
            "2 hallazgos, 1 bloqueante",
            JsonDocument.Parse(fixture.Requests[1].Body!).RootElement
                .GetProperty("comments").EnumerateArray().Single().GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_summary_that_was_never_drafted_is_not_posted()
    {
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(
            postSummary: true, summary: null, Item("src/auth.ts", "Seguridad", "hallazgo"));

        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Whatever_did_post_is_remembered_even_when_the_batch_partly_failed()
    {
        using var fixture = new Fixture(
            Finding("F-001", "src/a.ts", "Seguridad"),
            Finding("F-002", "src/b.ts", "Rendimiento"));

        fixture.Iterations();
        fixture.Handler.Json("""{"id":501}""");
        fixture.Handler.Respond(HttpStatusCode.Forbidden, "no permission");

        var failure = await Assert.ThrowsAsync<ReviewException>(() => fixture.PublishAsync(
            Item("src/a.ts", "Seguridad", "uno"),
            Item("src/b.ts", "Rendimiento", "dos")));

        // 1-based, so the message points at an item the user can count to.
        Assert.Equal(
            "1 comment(s) failed to post — #2: Azure DevOps returned 403 Forbidden: no permission",
            failure.Message);

        // The write-back happened anyway: without it, a retry would open a second thread for the
        // finding that already succeeded.
        var findings = fixture.Findings();
        Assert.Equal(501, findings[0].ThreadId);
        Assert.Equal(MemoryFinding.Posted, findings[0].Estado);
        Assert.Null(findings[1].ThreadId);
    }

    [Fact]
    public async Task A_failed_summary_is_reported_under_its_own_label()
    {
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.Iterations();

        // On Azure the summary goes last, so the finding's response is queued first and the refusal
        // is what answers the summary.
        fixture.Handler.Json("""{"id":501}""");
        fixture.Handler.Respond(HttpStatusCode.Forbidden, "no permission");

        var failure = await Assert.ThrowsAsync<ReviewException>(() => fixture.PublishAsync(
            postSummary: true, summary: "resumen", Item("src/auth.ts", "Seguridad", "hallazgo")));

        Assert.Equal(
            "1 comment(s) failed to post — summary: Azure DevOps returned 403 Forbidden: no permission",
            failure.Message);
    }

    [Fact]
    public async Task The_runs_recorded_head_is_never_consulted_before_anchoring()
    {
        // BUG-REVIEW-a, pinned. The run knows which commit it analysed; nothing on this path reads it,
        // so a push between review and post silently anchors findings against the wrong lines.
        using var fixture = new Fixture(Finding("F-001", "src/auth.ts", "Seguridad"));
        fixture.OnPost("""{"id":501}""");

        await fixture.PublishAsync(Item("src/auth.ts", "Seguridad", "hallazgo"));

        Assert.All(fixture.Requests, request =>
            Assert.DoesNotContain(Fixture.AnalysedSha, request.Uri.ToString(), StringComparison.Ordinal));
        Assert.All(fixture.Requests, request =>
            Assert.DoesNotContain(Fixture.AnalysedSha, request.Body ?? "", StringComparison.Ordinal));
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static PostFindingItem Item(string? file, string category, string content) =>
        new(file, category, content, file is null ? null : new CommentLocation(file, 12, 12));

    private static MemoryFinding Finding(string id, string? archivo, string categoria) => new()
    {
        Id = id,
        Severity = "warning",
        Tipo = "Bug",
        Categoria = categoria,
        Subtitulo = "Algo",
        Archivo = archivo,
        IntroducidoEnIter = 1,
    };

    /// <summary>An Azure-linked project with one saved run, and a faked host.</summary>
    private sealed class Fixture : IDisposable
    {
        public const string AnalysedSha = "deadbeefcafe";

        private const string RunId = "run-1";

        private readonly TempAdoPat _pat;
        private readonly HttpClient _http;
        private readonly TempDatabase _db;
        private readonly string _projectId;

        public Fixture(params MemoryFinding[] findings)
        {
            _pat = new TempAdoPat(Org);
            Handler = new FakeHttpHandler();
            _http = Handler.Client();

            _db = new TempDatabase();
            var workspace = _db.Use(c => WorkspaceStore.Create(c, "First", "folder", "#6366f1"));
            var project = _db.Use(c => ProjectStore.Create(c, new NewProject(
                workspace.Id, "Repo", "/tmp/repo", RemoteUrl: null, "#6366f1", "folder",
                AdoOrg: Org, AdoProject: "Web", AdoRepoId: "Widget",
                GithubOwner: null, GithubRepo: null, GithubHost: null)));

            _projectId = project.Id;

            _db.Do(c => ReviewRunStore.Add(
                c, RunId, project.Id, workspace.Id, prId: 7, iter: 3, level: "completo",
                meta: $$"""{"pr_title":"Add the thing","head_sha":"{{AnalysedSha}}"}""",
                reviewMarkdown: "cuerpo",
                diff: "diff",
                findings: JsonSerializer.Serialize(
                    findings.ToList(), ReviewJsonContext.Default.ListMemoryFinding)));
        }

        public FakeHttpHandler Handler { get; }

        /// <summary>
        /// Only the writes, with the iteration lookups an anchored post makes filtered out.
        /// </summary>
        /// <remarks>
        /// Counting raw requests would make every assertion here depend on whether the item happened
        /// to be anchored, which is not what any of these cases is about.
        /// </remarks>
        public IReadOnlyList<FakeHttpHandler.Captured> Requests =>
            [.. Handler.Requests.Where(r => !r.Uri.ToString().Contains("/iterations?", StringComparison.Ordinal))];

        public string LastUri => Requests[^1].Uri.ToString();

        /// <summary>Answers the iteration lookup, and every write with the same body.</summary>
        public void OnPost(string body)
        {
            Iterations();
            Handler.When("/threads", body);
        }

        /// <summary>Answers the iteration lookup and leaves the writes to the queued responses.</summary>
        public void Iterations() => Handler.When("/iterations?", """{"value":[{"id":3}]}""");

        public Task PublishAsync(params PostFindingItem[] items) =>
            PublishAsync(RunId, postSummary: false, summary: null, items);

        public Task PublishAsync(string runId, params PostFindingItem[] items) =>
            PublishAsync(runId, postSummary: false, summary: null, items);

        public Task PublishAsync(bool postSummary, string? summary, params PostFindingItem[] items) =>
            PublishAsync(RunId, postSummary, summary, items);

        public Task PublishAsync(string runId, bool postSummary, string? summary, PostFindingItem[] items) =>
            ReviewPosting.PublishAsync(
                _db.Handle, _http, _projectId, prId: 7, runId, items, postSummary, summary,
                TestContext.Current.CancellationToken);

        public List<MemoryFinding> Findings()
        {
            var run = _db.Use(c => ReviewRunStore.Get(c, RunId));
            Assert.NotNull(run);
            return JsonSerializer.Deserialize(run.Findings, ReviewJsonContext.Default.ListMemoryFinding)!;
        }

        public JsonElement LastBody() => JsonDocument.Parse(Requests[^1].Body!).RootElement;

        public void Dispose()
        {
            _db.Dispose();
            _http.Dispose();
            Handler.Dispose();
            _pat.Dispose();
        }
    }
}
