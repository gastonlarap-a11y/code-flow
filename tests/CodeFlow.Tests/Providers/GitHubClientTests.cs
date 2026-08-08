using System.Net;
using System.Text.Json;
using CodeFlow.Providers;
using CodeFlow.Providers.GitHub;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The GitHub REST client: the requests it makes and what it does with the replies.
/// See <c>docs/business-rules/06-providers.md</c> <c>PROV-001</c>–<c>PROV-018</c>.
/// </summary>
/// <remarks>
/// Every case runs against <see cref="FakeHttpHandler"/> — no network, no account, no token. That is
/// the point: the write paths here are ones <c>docs/business-rules/90-ambiguities.md</c> records as never having run
/// against a real API even in 1.7.2, so what can be verified is the request the client builds,
/// and that is verified exactly.
/// </remarks>
public sealed class GitHubClientTests
{
    private const string Token = "gho-test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Pull(
        long number = 42,
        string state = "open",
        bool draft = false,
        string? mergedAt = null,
        string? body = "the description") =>
        $$"""
        {
          "number": {{number}},
          "title": "Add the thing",
          "body": {{(body is null ? "null" : $"\"{body}\"")}},
          "state": "{{state}}",
          "draft": {{(draft ? "true" : "false")}},
          "merged_at": {{(mergedAt is null ? "null" : $"\"{mergedAt}\"")}},
          "head": { "ref": "feature/thing", "sha": "abc123" },
          "base": { "ref": "main" },
          "user": { "login": "octocat" },
          "created_at": "2026-07-29T10:00:00Z",
          "html_url": "https://github.com/acme/widget/pull/{{number}}"
        }
        """;

    // ---------- hosts and headers ----------

    [Theory]
    [InlineData("github.com", "https://api.github.com")]
    [InlineData("GitHub.com", "https://api.github.com")]
    [InlineData("ghe.contoso.com", "https://ghe.contoso.com/api/v3")]
    public void The_api_root_splits_github_com_from_every_enterprise_host(string host, string expected) =>
        Assert.Equal(expected, GitHubClient.ApiRoot(host));

    [Fact]
    public async Task Every_request_carries_the_four_headers_the_api_requires()
    {
        using var handler = new FakeHttpHandler().Json("""{"login":"octocat"}""");
        using var http = handler.Client();

        await GitHubClient.GetAuthenticatedUserAsync(http, "github.com", Token, Ct);

        var request = handler.Only;
        Assert.Equal("https://api.github.com/user", request.Uri.ToString());
        Assert.Equal($"Bearer {Token}", request.Header("Authorization"));
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        // GitHub answers 403 to a request with no User-Agent, unlike Azure DevOps.
        Assert.Equal("CodeFlow", request.Header("User-Agent"));
        Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task An_enterprise_host_is_reached_on_its_own_api_path()
    {
        using var handler = new FakeHttpHandler().Json("""[]""");
        using var http = handler.Client();

        await GitHubClient.ListPullRequestsAsync(http, "ghe.contoso.com", "team", "app", Token, Ct);

        Assert.StartsWith("https://ghe.contoso.com/api/v3/repos/team/app/pulls", handler.Only.Uri.ToString(),
            StringComparison.Ordinal);
    }

    // ---------- error shapes ----------

    [Fact]
    public async Task A_transport_failure_says_it_could_not_reach_github()
    {
        using var handler = new FakeHttpHandler().TransportFailure("Connection refused");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.GetAuthenticatedUserAsync(http, "github.com", Token, Ct));

        Assert.Equal("couldn't reach GitHub: Connection refused", failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401 Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "403 Forbidden")]
    [InlineData(HttpStatusCode.NotFound, "404 Not Found")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "422 Unprocessable Entity")]
    public async Task Every_status_produces_the_same_error_shape(HttpStatusCode status, string expected)
    {
        // This is the point of the theory, not incidental coverage: 1.7.2 has no
        // status-code branch anywhere, so an expired token is indistinguishable from a missing repo.
        // Reproduced — and this test is what would notice if someone "improved" it.
        using var handler = new FakeHttpHandler().Respond(status, """{"message":"nope"}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.GetAuthenticatedUserAsync(http, "github.com", Token, Ct));

        Assert.Equal($$"""GitHub returned {{expected}}: {"message":"nope"}""", failure.Message);
    }

    [Fact]
    public async Task A_body_that_is_not_the_expected_shape_says_the_response_was_unexpected()
    {
        using var handler = new FakeHttpHandler().Json("this is not json");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.GetAuthenticatedUserAsync(http, "github.com", Token, Ct));

        Assert.StartsWith("unexpected response from GitHub: ", failure.Message, StringComparison.Ordinal);
    }

    // ---------- listing and mapping ----------

    [Fact]
    public async Task Listing_asks_for_every_state_newest_first_and_maps_each_pull()
    {
        using var handler = new FakeHttpHandler().Json($"[{Pull()}]");
        using var http = handler.Client();

        var pulls = await GitHubClient.ListPullRequestsAsync(http, "github.com", "acme", "widget", Token, Ct);

        Assert.Equal(
            "https://api.github.com/repos/acme/widget/pulls?state=all&per_page=100&sort=created&direction=desc",
            handler.Only.Uri.ToString());

        var pull = Assert.Single(pulls);
        Assert.Equal(42, pull.Id);
        Assert.Equal("Add the thing", pull.Title);
        Assert.Equal("the description", pull.Description);
        Assert.Equal("feature/thing", pull.SourceBranch);
        Assert.Equal("main", pull.TargetBranch);
        Assert.Equal("octocat", pull.Author);
        Assert.Equal("https://github.com/acme/widget/pull/42", pull.Url);
        Assert.Equal("github", pull.Provider);
    }

    [Fact]
    public async Task A_pull_request_with_no_description_maps_to_an_empty_one()
    {
        // GitHub omits `body` entirely rather than sending "", and the renderer's type says string.
        using var handler = new FakeHttpHandler().Json(Pull(body: null));
        using var http = handler.Client();

        var pull = await GitHubClient.GetPullRequestAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(string.Empty, pull.Description);
    }

    [Theory]
    [InlineData("open", false, null, "open")]
    [InlineData("open", true, null, "draft")]
    [InlineData("closed", false, null, "closed")]
    [InlineData("closed", false, "2026-07-29T12:00:00Z", "merged")]
    [InlineData("closed", true, "2026-07-29T12:00:00Z", "merged")]
    public async Task The_four_buckets_collapse_in_the_right_order(
        string state, bool draft, string? mergedAt, string expected)
    {
        // A merged pull request is also closed, and it reports as merged — that ordering is the whole
        // reason this is a function rather than a field.
        using var handler = new FakeHttpHandler().Json(Pull(state: state, draft: draft, mergedAt: mergedAt));
        using var http = handler.Client();

        var pull = await GitHubClient.GetPullRequestAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(expected, pull.Status);
    }

    [Fact]
    public async Task A_pull_request_with_no_head_commit_is_refused_rather_than_anchored_to_nothing()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"number":42,"title":"t","state":"open","head":{"ref":"f"},"base":{"ref":"main"},"user":{"login":"o"},"created_at":"x","html_url":"u"}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.HeadShaForAsync(http, "github.com", "acme", "widget", 42, Token, Ct));

        Assert.Equal("GitHub didn't report a head commit for this pull request", failure.Message);
    }

    // ---------- the diff and its fallback ----------

    [Fact]
    public async Task The_diff_is_asked_for_as_a_diff_and_returned_verbatim()
    {
        const string Diff = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new\n";
        using var handler = new FakeHttpHandler().Text(Diff);
        using var http = handler.Client();

        var diff = await GitHubClient.PullRequestDiffAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(Diff, diff);
        // One request: the fallback never fired.
        Assert.Equal("application/vnd.github.diff", handler.Only.Header("Accept"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotAcceptable, "")]
    [InlineData(HttpStatusCode.OK, "   \n  ")]
    public async Task A_refused_or_blank_diff_falls_back_to_the_changed_files(HttpStatusCode status, string body)
    {
        // A non-2xx here is not an error — GitHub answers 406 past its size limit, and that is the
        // signal to reassemble. A 2xx with nothing in it means the same thing.
        using var handler = new FakeHttpHandler()
            .Respond(status, body, "text/plain")
            .Json("""[{"filename":"a.txt","status":"modified","patch":"@@ -1 +1 @@\n-old\n+new"}]""");

        using var http = handler.Client();

        var diff = await GitHubClient.PullRequestDiffAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://api.github.com/repos/acme/widget/pulls/42/files?per_page=100&page=1",
            handler.Requests[1].Uri.ToString());

        Assert.Equal(
            "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new\n",
            diff);
    }

    [Fact]
    public async Task The_reassembled_headers_name_real_paths_even_where_the_markers_say_dev_null()
    {
        // The detail that gets missed: `diff --git` always carries both real paths, while `---`/`+++`
        // use /dev/null for an addition and a deletion. A rename reads its old path from
        // previous_filename on both.
        using var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.NotAcceptable)
            .Json("""
                [
                  {"filename":"new.txt","status":"added","patch":"@@ -0,0 +1 @@\n+hello"},
                  {"filename":"gone.txt","status":"removed","patch":"@@ -1 +0,0 @@\n-bye"},
                  {"filename":"after.txt","status":"renamed","previous_filename":"before.txt","patch":"@@ -1 +1 @@\n-a\n+b"},
                  {"filename":"logo.png","status":"modified"}
                ]
                """);

        using var http = handler.Client();

        var diff = await GitHubClient.PullRequestDiffAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(
            """
            diff --git a/new.txt b/new.txt
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1 @@
            +hello
            diff --git a/gone.txt b/gone.txt
            --- a/gone.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            diff --git a/before.txt b/after.txt
            --- a/before.txt
            +++ b/after.txt
            @@ -1 +1 @@
            -a
            +b
            diff --git a/logo.png b/logo.png
            --- a/logo.png
            +++ b/logo.png
            (binary or too large to display)

            """.ReplaceLineEndings("\n"),
            diff);
    }

    [Fact]
    public async Task The_fallback_stops_paging_as_soon_as_a_page_is_not_full()
    {
        var full = string.Join(",", Enumerable.Range(0, 100)
            .Select(i => $$"""{"filename":"f{{i}}.txt","status":"modified","patch":"@@ -1 +1 @@\n-a\n+b"}"""));

        using var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.NotAcceptable)
            .Json($"[{full}]")
            .Json("""[{"filename":"last.txt","status":"modified","patch":"@@ -1 +1 @@\n-a\n+b"}]""");

        using var http = handler.Client();

        await GitHubClient.PullRequestDiffAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        // Three requests, not four: page 2 came back short, so page 3 was never asked for. Asserting
        // the count is the only way to prove the stop condition rather than the ceiling.
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("page=2", handler.Requests[2].Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_with_no_changed_files_at_all_is_an_error()
    {
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.NotAcceptable).Json("[]");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.PullRequestDiffAsync(http, "github.com", "acme", "widget", 42, Token, Ct));

        Assert.Equal("GitHub reported no changed files for this pull request", failure.Message);
    }

    // ---------- the viewer's own decision ----------

    [Theory]
    [InlineData("", "none")]
    [InlineData("APPROVED", "approved")]
    [InlineData("CHANGES_REQUESTED", "changes_requested")]
    [InlineData("APPROVED,CHANGES_REQUESTED", "changes_requested")]
    [InlineData("CHANGES_REQUESTED,APPROVED", "approved")]
    [InlineData("APPROVED,DISMISSED", "none")]
    [InlineData("CHANGES_REQUESTED,COMMENTED", "changes_requested")]
    [InlineData("APPROVED,PENDING", "approved")]
    public async Task The_last_verdict_wins_and_only_a_verdict_counts(string states, string expected)
    {
        var reviews = states.Length == 0
            ? "[]"
            : $"[{string.Join(",", states.Split(',').Select(s => $$"""{"user":{"login":"octocat"},"state":"{{s}}"}"""))}]";

        using var handler = new FakeHttpHandler().Json("""{"login":"octocat"}""").Json(reviews);
        using var http = handler.Client();

        var decision = await GitHubClient.ViewerDecisionAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(expected, decision);
        // Two requests: the login has to be known before the reviews can be filtered by it.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://api.github.com/repos/acme/widget/pulls/42/reviews?per_page=100",
            handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task Another_reviewers_verdict_is_not_mistaken_for_the_users_own()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"login":"octocat"}""")
            .Json("""
                [
                  {"user":{"login":"someone-else"},"state":"APPROVED"},
                  {"user":{"login":"OCTOCAT"},"state":"CHANGES_REQUESTED"}
                ]
                """);

        using var http = handler.Client();

        // The login match is case-insensitive, so the second review counts and the first does not.
        Assert.Equal("changes_requested",
            await GitHubClient.ViewerDecisionAsync(http, "github.com", "acme", "widget", 42, Token, Ct));
    }

    // ---------- writes ----------

    [Fact]
    public async Task Creating_a_pull_request_sends_exactly_the_five_fields_the_api_takes()
    {
        using var handler = new FakeHttpHandler().Json(Pull(number: 7));
        using var http = handler.Client();

        var created = await GitHubClient.CreatePullRequestAsync(
            http, "github.com", "acme", "widget", "Add the thing", "why", "feature/thing", "main", draft: true,
            Token, Ct);

        Assert.Equal(HttpMethod.Post, handler.Only.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/pulls", handler.Only.Uri.ToString());

        using var body = JsonDocument.Parse(handler.Only.Body!);
        Assert.Equal(["title", "head", "base", "body", "draft"],
            body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("feature/thing", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.True(body.RootElement.GetProperty("draft").GetBoolean());

        // The created PR comes back through the same mapping as a fetched one.
        Assert.Equal(7, created.Id);
        Assert.Equal("github", created.Provider);
    }

    [Fact]
    public async Task An_approval_with_no_comment_omits_the_body_key_entirely()
    {
        // Not "body": "" — GitHub rejects an empty body on REQUEST_CHANGES, and 1.7.2 omits the
        // key rather than sending a blank one.
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        using var http = handler.Client();

        await GitHubClient.SubmitReviewAsync(http, "github.com", "acme", "widget", 42, "APPROVE", "   ", Token, Ct);

        using var body = JsonDocument.Parse(handler.Only.Body!);
        Assert.Equal(["event"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("APPROVE", body.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task A_review_with_a_comment_carries_it()
    {
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        using var http = handler.Client();

        await GitHubClient.SubmitReviewAsync(
            http, "github.com", "acme", "widget", 42, "REQUEST_CHANGES", "needs tests", Token, Ct);

        using var body = JsonDocument.Parse(handler.Only.Body!);
        Assert.Equal("needs tests", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Closing_a_pull_request_patches_its_state()
    {
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        using var http = handler.Client();

        await GitHubClient.CloseAsync(http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal(HttpMethod.Patch, handler.Only.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/pulls/42", handler.Only.Uri.ToString());
        Assert.Equal("""{"state":"closed"}""", handler.Only.Body);
    }

    // ---------- comment threads ----------

    [Fact]
    public async Task Replies_join_the_thread_they_answer_and_roots_keep_their_first_seen_order()
    {
        using var handler = new FakeHttpHandler()
            .Json("""
                [
                  {"id":10,"path":"a.ts","line":5,"start_line":3,"body":"first root","user":{"login":"ann"},"created_at":"t1"},
                  {"id":20,"path":"b.ts","line":9,"body":"second root","user":{"login":"bob"},"created_at":"t2"},
                  {"id":11,"path":"a.ts","line":99,"body":"a reply","user":{"login":"cat"},"created_at":"t3","in_reply_to_id":10}
                ]
                """)
            .Json("[]");

        using var http = handler.Client();

        var threads = await GitHubClient.ListCommentThreadsAsync(
            http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal([10, 20], threads.Select(t => t.Id));

        var first = threads[0];
        Assert.Equal(2, first.Comments.Count);
        Assert.Equal(["first root", "a reply"], first.Comments.Select(c => c.Content));
        // The location is the root's; the reply's line 99 is ignored.
        Assert.Equal("a.ts", first.FilePath);
        Assert.Equal(3, first.StartLine);
        Assert.Equal(5, first.EndLine);
    }

    [Fact]
    public async Task A_single_line_thread_reports_the_same_line_at_both_ends()
    {
        // start_line is absent for a one-line comment, and falls back to line rather than to null.
        using var handler = new FakeHttpHandler()
            .Json("""[{"id":10,"path":"a.ts","line":5,"body":"here","user":{"login":"ann"},"created_at":"t"}]""")
            .Json("[]");

        using var http = handler.Client();

        var thread = Assert.Single(
            await GitHubClient.ListCommentThreadsAsync(http, "github.com", "acme", "widget", 42, Token, Ct));

        Assert.Equal(5, thread.StartLine);
        Assert.Equal(5, thread.EndLine);
    }

    [Fact]
    public async Task Empty_comments_are_dropped_and_the_rest_are_trimmed()
    {
        using var handler = new FakeHttpHandler()
            .Json("""
                [
                  {"id":10,"path":"a.ts","line":1,"body":"  padded  ","user":{"login":"ann"},"created_at":"t"},
                  {"id":20,"path":"b.ts","line":1,"body":"   ","user":{"login":"bob"},"created_at":"t"},
                  {"id":30,"path":"c.ts","line":1,"user":{"login":"cat"},"created_at":"t"}
                ]
                """)
            .Json("[]");

        using var http = handler.Client();

        var thread = Assert.Single(
            await GitHubClient.ListCommentThreadsAsync(http, "github.com", "acme", "widget", 42, Token, Ct));

        Assert.Equal("padded", Assert.Single(thread.Comments).Content);
    }

    [Fact]
    public async Task Conversation_comments_become_their_own_location_less_threads_after_the_inline_ones()
    {
        using var handler = new FakeHttpHandler()
            .Json("""[{"id":10,"path":"a.ts","line":1,"body":"inline","user":{"login":"ann"},"created_at":"t"}]""")
            .Json("""[{"id":99,"body":"looks good","user":{"login":"bob"},"created_at":"t"}]""");

        using var http = handler.Client();

        var threads = await GitHubClient.ListCommentThreadsAsync(
            http, "github.com", "acme", "widget", 42, Token, Ct);

        Assert.Equal([10, 99], threads.Select(t => t.Id));

        var conversation = threads[1];
        Assert.Null(conversation.FilePath);
        Assert.Null(conversation.StartLine);
        Assert.Null(conversation.EndLine);
        Assert.Equal("looks good", Assert.Single(conversation.Comments).Content);

        Assert.Equal("https://api.github.com/repos/acme/widget/issues/42/comments?per_page=100",
            handler.Requests[1].Uri.ToString());
    }

    // ---------- error mapping ----------

    /// <summary>The body GitHub answers a self-approval with, trimmed to what matters.</summary>
    private const string SelfApprovalBody =
        """{"message":"Unprocessable Entity","errors":["Review Can not approve your own pull request"],"status":"422"}""";

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404 Not Found")]
    [InlineData(HttpStatusCode.Unauthorized, "401 Unauthorized")]
    [InlineData(HttpStatusCode.InternalServerError, "500 Internal Server Error")]
    public async Task A_status_that_is_not_the_self_approval_rule_still_collapses(
        HttpStatusCode status, string rendered)
    {
        // Half of DIVERGENCE-PROV-c, and the half that did not change. CodeFlow 1.7.2 collapses every
        // non-2xx into one message shape, and only the self-approval 422 was worth diverging over: an
        // expired token still reads exactly as it always did.
        using var handler = new FakeHttpHandler().Respond(status, """{"message":"nope"}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.SubmitReviewAsync(
                http, "github.com", "acme", "widget", 42, "APPROVE", "", Token, Ct));

        Assert.Equal($"GitHub returned {rendered}: {{\"message\":\"nope\"}}", failure.Message);
        Assert.False(failure.SelfApproval);
        Assert.DoesNotContain(GitHubException.SelfApprovalPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_422_that_is_not_about_self_approval_stays_undifferentiated()
    {
        // The status alone must not be the test. 422 is GitHub's answer to every validation failure —
        // a REQUEST_CHANGES with no body reaches it too — and treating all of them as "you cannot
        // approve your own pull request" would put a confidently wrong sentence in front of the user.
        using var handler = new FakeHttpHandler().Respond(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":["Body can't be blank"],"status":"422"}""");

        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.SubmitReviewAsync(
                http, "github.com", "acme", "widget", 42, "REQUEST_CHANGES", "", Token, Ct));

        Assert.False(failure.SelfApproval);
        Assert.Contains("422 Unprocessable Entity", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_your_own_pull_request_is_told_apart_from_everything_else()
    {
        // The other half. GitHub refuses this by rule, so no retry and no credential can change the
        // outcome — which is exactly what makes a generic error the wrong answer: it invites the user
        // to try again forever. CodeFlow 1.7.2 does not distinguish it either; this is the divergence.
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.UnprocessableEntity, SelfApprovalBody);
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.SubmitReviewAsync(
                http, "github.com", "acme", "widget", 42, "APPROVE", "", Token, Ct));

        Assert.True(failure.SelfApproval);

        // The message is unchanged. The `SELF_APPROVAL: ` prefix belongs to the two command boundaries
        // that consume it and deliberately not to the client, which has callers that render straight
        // to a person.
        Assert.Equal($"GitHub returned 422 Unprocessable Entity: {SelfApprovalBody}", failure.Message);
        Assert.DoesNotContain(GitHubException.SelfApprovalPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_self_approval_sentence_is_matched_regardless_of_capitalisation()
    {
        // XLANG-013 is a literal GitHub owns and can reword without telling anyone. Case is the
        // cheapest way it could drift, so it is the one variation absorbed here; anything more is
        // guesswork, and the failure mode is a graceful one — a raw 422, which is today's behaviour.
        using var handler = new FakeHttpHandler().Respond(
            HttpStatusCode.UnprocessableEntity,
            """{"errors":["Review CAN NOT APPROVE YOUR OWN PULL REQUEST"]}""");

        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<GitHubException>(
            () => GitHubClient.SubmitReviewAsync(
                http, "github.com", "acme", "widget", 42, "APPROVE", "", Token, Ct));

        Assert.True(failure.SelfApproval);
    }
}
