using System.Net;
using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Providers.GitHub;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The four comment-writing calls, and the requests they build.
/// See <c>docs/business-rules/06-providers.md</c> <c>PROV-011</c>–<c>PROV-014</c>.
/// </summary>
/// <remarks>
/// All four are <c>UNVERIFIED</c> — <c>docs/business-rules/90-ambiguities.md</c> records that none has ever run against
/// a real API, here or in 1.7.2. So what can be verified is the request, and it is
/// verified exactly: method, URL and every field of the body.
/// </remarks>
public sealed class GitHubPostingTests
{
    private const string Token = "gho-test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- anchored inline comments ----------

    [Fact]
    public async Task An_anchored_comment_posts_to_the_review_comments_endpoint()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":9001}""");
        using var http = handler.Client();

        var id = await GitHubClient.PostCommentAnchoredAsync(
            http, "github.com", "acme", "widget", 42, "el token viaja en la URL",
            "src/auth.ts", 12, 12, "abc123", Token, Ct);

        Assert.Equal(9001, id);
        Assert.Equal(HttpMethod.Post, handler.Only.Method);
        Assert.Equal(
            "https://api.github.com/repos/acme/widget/pulls/42/comments",
            handler.Only.Uri.ToString());
    }

    [Fact]
    public async Task A_single_line_comment_omits_the_range_start()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":1}""");
        using var http = handler.Client();

        await GitHubClient.PostCommentAnchoredAsync(
            http, "github.com", "acme", "widget", 42, "hallazgo", "src/auth.ts", 12, 12, "abc123", Token, Ct);

        var body = Body(handler);
        Assert.Equal("hallazgo", body.GetProperty("body").GetString());
        Assert.Equal("abc123", body.GetProperty("commit_id").GetString());
        Assert.Equal("src/auth.ts", body.GetProperty("path").GetString());
        Assert.Equal(12, body.GetProperty("line").GetInt64());
        Assert.Equal("RIGHT", body.GetProperty("side").GetString());

        // Load-bearing: GitHub answers 422 when start_line equals line, so sending it always would
        // break the common case rather than an edge one.
        Assert.False(body.TryGetProperty("start_line", out _));
        Assert.False(body.TryGetProperty("start_side", out _));
    }

    [Fact]
    public async Task A_multi_line_comment_carries_the_range_start()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":1}""");
        using var http = handler.Client();

        await GitHubClient.PostCommentAnchoredAsync(
            http, "github.com", "acme", "widget", 42, "hallazgo", "src/auth.ts", 12, 14, "abc123", Token, Ct);

        var body = Body(handler);
        // GitHub anchors to the last line; start_line marks where the highlight begins.
        Assert.Equal(14, body.GetProperty("line").GetInt64());
        Assert.Equal(12, body.GetProperty("start_line").GetInt64());
        Assert.Equal("RIGHT", body.GetProperty("start_side").GetString());
    }

    [Fact]
    public async Task An_inverted_range_anchors_to_whichever_line_is_higher()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":1}""");
        using var http = handler.Client();

        await GitHubClient.PostCommentAnchoredAsync(
            http, "github.com", "acme", "widget", 42, "hallazgo", "src/auth.ts", 20, 5, "abc123", Token, Ct);

        var body = Body(handler);
        Assert.Equal(20, body.GetProperty("line").GetInt64());
        // start_line is not below line, so it is omitted rather than sent inverted.
        Assert.False(body.TryGetProperty("start_line", out _));
    }

    [Fact]
    public async Task The_path_loses_its_leading_slash()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":1}""");
        using var http = handler.Client();

        await GitHubClient.PostCommentAnchoredAsync(
            http, "github.com", "acme", "widget", 42, "hallazgo", "/src/auth.ts", 3, 3, "abc123", Token, Ct);

        // The opposite of Azure DevOps, which adds one. Both are 1.7.2's.
        Assert.Equal("src/auth.ts", Body(handler).GetProperty("path").GetString());
    }

    // ---------- general comments and replies ----------

    [Fact]
    public async Task A_general_comment_goes_to_the_issues_endpoint()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":77}""");
        using var http = handler.Client();

        var id = await GitHubClient.PostCommentAsync(
            http, "github.com", "acme", "widget", 42, "resumen de la revisión", Token, Ct);

        Assert.Equal(77, id);
        // A pull request is an issue on GitHub, and a conversation comment is an issue comment.
        Assert.Equal(
            "https://api.github.com/repos/acme/widget/issues/42/comments",
            handler.Only.Uri.ToString());
        Assert.Equal("resumen de la revisión", Body(handler).GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_reply_threads_off_the_root_comments_id()
    {
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.Created);
        using var http = handler.Client();

        await GitHubClient.ReplyReviewCommentAsync(
            http, "github.com", "acme", "widget", 42, 9001, "sigue presente", Token, Ct);

        Assert.Equal(
            "https://api.github.com/repos/acme/widget/pulls/42/comments/9001/replies",
            handler.Only.Uri.ToString());
        Assert.Equal("sigue presente", Body(handler).GetProperty("body").GetString());
    }

    [Fact]
    public async Task An_enterprise_host_writes_through_its_own_api_root()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":1}""");
        using var http = handler.Client();

        await GitHubClient.PostCommentAsync(http, "ghe.contoso.com", "team", "app", 7, "hola", Token, Ct);

        Assert.Equal(
            "https://ghe.contoso.com/api/v3/repos/team/app/issues/7/comments",
            handler.Only.Uri.ToString());
    }

    // ---------- resolving a thread, over GraphQL ----------

    [Fact]
    public async Task Resolving_a_thread_finds_it_by_database_id_and_then_mutates_it()
    {
        using var handler = new FakeHttpHandler()
            .Json(Threads(("T_other", 111), ("T_ours", 9001)))
            .Json("""{"data":{"resolveReviewThread":{"thread":{"isResolved":true}}}}""");
        using var http = handler.Client();

        await GitHubClient.ResolveReviewThreadAsync(http, "github.com", "acme", "widget", 42, 9001, Token, Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.Equal("https://api.github.com/graphql", request.Uri.ToString()));

        var query = Query(handler, 0);
        Assert.Contains("""repository(owner: "acme", name: "widget")""", query, StringComparison.Ordinal);
        Assert.Contains("pullRequest(number: 42)", query, StringComparison.Ordinal);
        // Both caps are real limits, not defensive numbers: a pull request past either finds nothing.
        Assert.Contains("reviewThreads(first: 100)", query, StringComparison.Ordinal);
        Assert.Contains("comments(first: 100)", query, StringComparison.Ordinal);

        Assert.Contains(
            """resolveReviewThread(input: { threadId: "T_ours" })""", Query(handler, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_graphql_calls_drop_the_two_rest_only_headers()
    {
        using var handler = new FakeHttpHandler()
            .Json(Threads(("T_ours", 9001)))
            .Json("""{"data":{}}""");
        using var http = handler.Client();

        await GitHubClient.ResolveReviewThreadAsync(http, "github.com", "acme", "widget", 42, 9001, Token, Ct);

        var request = handler.Requests[0];
        Assert.Equal($"Bearer {Token}", request.Header("Authorization"));
        Assert.Equal("CodeFlow", request.Header("User-Agent"));
        // Accept and X-GitHub-Api-Version are REST-only; 1.7.2 does not send them here.
        Assert.Null(request.Header("Accept"));
        Assert.Null(request.Header("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task A_thread_that_holds_no_matching_comment_is_reported_as_not_found()
    {
        using var handler = new FakeHttpHandler().Json(Threads(("T_other", 111)));
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(() =>
            GitHubClient.ResolveReviewThreadAsync(http, "github.com", "acme", "widget", 42, 9001, Token, Ct));

        Assert.Equal("couldn't find the review thread for this comment", failure.Message);
        // The mutation is never attempted.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_response_with_no_threads_node_says_so()
    {
        using var handler = new FakeHttpHandler().Json("""{"data":{"repository":null}}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(() =>
            GitHubClient.ResolveReviewThreadAsync(http, "github.com", "acme", "widget", 42, 9001, Token, Ct));

        Assert.Equal("no review threads in GraphQL response", failure.Message);
    }

    [Theory]
    [InlineData(0, "GitHub GraphQL returned 401 Unauthorized")]
    [InlineData(1, "GitHub GraphQL resolve returned 401 Unauthorized")]
    public async Task A_graphql_failure_names_which_call_failed_and_omits_the_body(int failingCall, string expected)
    {
        var handler = new FakeHttpHandler();
        if (failingCall == 1)
        {
            handler.Json(Threads(("T_ours", 9001)));
        }

        handler.Respond(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        using var _ = handler;
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(() =>
            GitHubClient.ResolveReviewThreadAsync(http, "github.com", "acme", "widget", 42, 9001, Token, Ct));

        // Every REST error in this client includes the response body; these two do not. The asymmetry
        // is 1.7.2's, reproduced rather than tidied.
        Assert.Equal(expected, failure.Message);
        Assert.DoesNotContain("Bad credentials", failure.Message, StringComparison.Ordinal);
    }

    // ---------- BUG-REVIEW-a: findings must not land on lines that have moved ----------

    /// <summary>An anchored item, which is the only kind whose line numbers can go stale.</summary>
    private static PostItem Anchored() =>
        new("el token viaja en la URL", new CommentLocation("src/auth.ts", 12, 12),
            ExistingThreadId: null, Resolved: false, Iter: 2, Today: "2026-07-31");

    private static GitHubHost Host(HttpClient http) => new(http, "github.com", "acme", "widget", Token);

    [Fact]
    public async Task A_batch_whose_review_analysed_an_older_head_is_refused()
    {
        // BUG-REVIEW-a. The line numbers were computed from the diff at review time; the anchor is
        // written against whatever head is current. A push in between silently moves every finding
        // onto whatever now sits at those line numbers — a reviewer reads a comment that does not
        // match the code above it, and nothing marks it as misplaced.
        using var handler = new FakeHttpHandler().Json("""{"head":{"sha":"def4567"}}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<ProviderException>(
            () => Host(http).PublishFindingsAsync(42, [Anchored()], analysedHeadSha: "abc1234", Ct));

        Assert.StartsWith(GitHubHost.StaleReviewPrefix, failure.Message, StringComparison.Ordinal);
        Assert.Contains("abc1234", failure.Message, StringComparison.Ordinal);
        Assert.Contains("def4567", failure.Message, StringComparison.Ordinal);

        // Refused, not partially posted: the head lookup is the only request that ran. A comment on
        // the wrong line cannot be withdrawn without deleting it by hand.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_early_check_refuses_before_a_summary_could_be_posted()
    {
        // The refusal has to be askable *before* publishing, because the summary is now posted
        // first: a summary announcing findings that a stale head then blocks would describe
        // comments nobody can see.
        using var handler = new FakeHttpHandler().Json("""{"head":{"sha":"def4567"}}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<ProviderException>(
            () => Host(http).EnsureUnchangedAsync(42, "abc1234", anchored: true, Ct));

        Assert.StartsWith(GitHubHost.StaleReviewPrefix, failure.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_early_check_asks_nothing_when_no_finding_is_anchored()
    {
        // Only an anchored comment can land on a line that moved, so an all-conversation post must
        // not pay for a head lookup it cannot use.
        using var handler = new FakeHttpHandler();
        using var http = handler.Client();

        await Host(http).EnsureUnchangedAsync(42, "abc1234", anchored: false, Ct);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_early_check_asks_nothing_when_the_run_recorded_no_head()
    {
        using var handler = new FakeHttpHandler();
        using var http = handler.Client();

        await Host(http).EnsureUnchangedAsync(42, analysedHeadSha: null, anchored: true, Ct);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_early_check_lets_a_matching_head_through()
    {
        using var handler = new FakeHttpHandler().Json("""{"head":{"sha":"abc1234"}}""");
        using var http = handler.Client();

        await Host(http).EnsureUnchangedAsync(42, "abc1234", anchored: true, Ct);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_finding_the_diff_cannot_hold_is_posted_unanchored_rather_than_lost()
    {
        // Observed live: a *critical* finding cited lines 68-73 of a file whose hunk starts at 70,
        // GitHub refused the anchor with 422, and the finding was reported as a failed post and
        // simply never published. A model reads the code around a change and cites what it read;
        // the diff is narrower than that by construction.
        using var handler = new FakeHttpHandler()
            .Json("""{"head":{"sha":"abc1234"}}""")
            .Respond(HttpStatusCode.UnprocessableEntity, "line must be part of the diff")
            .Json("""{"id":9002}""");

        using var http = handler.Client();

        var outcomes = await Host(http).PublishFindingsAsync(42, [Anchored()], analysedHeadSha: "abc1234", Ct);

        Assert.Equal(9002, Assert.IsType<PostOutcome.Opened>(Assert.Single(outcomes)).ThreadId);

        // The retry says where it belonged, since the comment is no longer sitting there. Asserted
        // on the unaccented run of the sentence: the body is JSON, and `á` arrives as `á`.
        var retry = handler.Requests[^1].Body!;
        Assert.Contains("src/auth.ts", retry, StringComparison.Ordinal);
        Assert.Contains("dentro del diff de este pull request", retry, StringComparison.Ordinal);
        Assert.Contains("el token viaja en la URL", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_the_anchor_still_fails()
    {
        // 403 is a permission problem: retrying the same text unanchored would fail the same way and
        // only make the user wait twice for the same answer.
        using var handler = new FakeHttpHandler()
            .Json("""{"head":{"sha":"abc1234"}}""")
            .Respond(HttpStatusCode.Forbidden, "no permission");

        using var http = handler.Client();

        var outcomes = await Host(http).PublishFindingsAsync(42, [Anchored()], analysedHeadSha: "abc1234", Ct);

        Assert.IsType<PostOutcome.Failed>(Assert.Single(outcomes));
    }

    [Fact]
    public async Task The_head_is_asked_for_once_even_when_the_gate_ran_first()
    {
        // F-005: splitting the freshness check out of publishing turned one head lookup into two.
        // Reusing the value is also more correct — a second read could return a different commit
        // than the one the gate approved, which is the window the gate exists to close.
        using var handler = new FakeHttpHandler()
            .Json("""{"head":{"sha":"abc1234"}}""")
            .Json("""{"id":9001}""");

        using var http = handler.Client();
        var host = Host(http);

        await host.EnsureUnchangedAsync(42, "abc1234", anchored: true, Ct);
        await host.PublishFindingsAsync(42, [Anchored()], analysedHeadSha: "abc1234", Ct);

        // The head lookup and the comment: two requests, not three.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_batch_whose_head_still_matches_posts_normally()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"head":{"sha":"abc1234"}}""")
            .Json("""{"id":9001}""");

        using var http = handler.Client();

        var outcomes = await Host(http).PublishFindingsAsync(42, [Anchored()], analysedHeadSha: "abc1234", Ct);

        Assert.Equal(9001, Assert.IsType<PostOutcome.Opened>(Assert.Single(outcomes)).ThreadId);
    }

    [Fact]
    public async Task A_run_that_recorded_no_head_is_posted_rather_than_blocked()
    {
        // A run saved before the SHA was tracked cannot be checked. Refusing it would strand old runs
        // with no way to publish at all, which is a worse failure than the one being prevented.
        using var handler = new FakeHttpHandler()
            .Json("""{"head":{"sha":"def4567"}}""")
            .Json("""{"id":9002}""");

        using var http = handler.Client();

        var outcomes = await Host(http).PublishFindingsAsync(42, [Anchored()], analysedHeadSha: null, Ct);

        Assert.IsType<PostOutcome.Opened>(Assert.Single(outcomes));
    }

    [Fact]
    public async Task A_conversation_only_batch_is_never_blocked_by_a_moved_head()
    {
        // Nothing here is anchored to a line, so nothing can land on the wrong one. The head is not
        // even fetched — the batch does not need it.
        using var handler = new FakeHttpHandler().Json("""{"id":9003}""");
        using var http = handler.Client();

        var unanchored = new PostItem(
            "resumen general", Location: null, ExistingThreadId: null, Resolved: false, Iter: 2,
            Today: "2026-07-31");

        var outcomes = await Host(http).PublishFindingsAsync(42, [unanchored], analysedHeadSha: "abc1234", Ct);

        Assert.IsType<PostOutcome.Opened>(Assert.Single(outcomes));
        Assert.Single(handler.Requests);
    }

    /// <summary>A review-threads answer holding the given threads, each with one comment.</summary>
    private static string Threads(params (string Id, long CommentId)[] threads)
    {
        var nodes = threads.Select(t =>
            "{\"id\":\"" + t.Id + "\",\"isResolved\":false,"
            + "\"comments\":{\"nodes\":[{\"databaseId\":" + t.CommentId + "}]}}");

        return "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{\"nodes\":["
            + string.Join(",", nodes) + "]}}}}}";
    }

    [Fact]
    public void GitHubs_conversation_runs_oldest_first_so_the_summary_is_posted_first()
    {
        // The other half of `DIVERGENCE-PROV-d`, and the reason the flag exists rather than a plain
        // "post it last": the goal — the summary is the first thing read — is the same on both
        // hosts, and it is reached from opposite ends. Flipping this to match Azure would bury the
        // summary here instead.
        using var http = new HttpClient(new FakeHttpHandler());

        Assert.False(Host(http).DiscussionNewestFirst);
    }

    private static JsonElement Body(FakeHttpHandler handler) =>
        JsonDocument.Parse(handler.Only.Body!).RootElement;

    private static string Query(FakeHttpHandler handler, int index) =>
        JsonDocument.Parse(handler.Requests[index].Body!).RootElement.GetProperty("query").GetString()!;
}
