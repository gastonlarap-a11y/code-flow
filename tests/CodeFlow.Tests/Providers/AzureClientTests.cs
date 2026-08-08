using System.Net;
using System.Text;
using CodeFlow.Providers;
using CodeFlow.Providers.Azure;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The Azure DevOps REST client: the requests it makes and what it does with the replies.
/// See <c>docs/business-rules/06-providers.md</c> <c>PROV-019</c>–<c>PROV-037</c>.
/// </summary>
/// <remarks>
/// Every case runs against <see cref="FakeHttpHandler"/> — no network, no organisation, no PAT. The write
/// paths here are ones <c>docs/business-rules/90-ambiguities.md</c> records as never having run against a real API even in
/// 1.7.2, so what can be verified is the request the client builds, and that is verified exactly.
/// </remarks>
public sealed class AzureClientTests
{
    private const string Pat = "ado-test-pat";

    private const string Org = "contoso";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PullRequest(
        long id = 7,
        string status = "active",
        bool isDraft = false,
        string repoName = "Widget",
        string? projectName = "Web",
        string reviewers = "") =>
        $$"""
        {
          "pullRequestId": {{id}},
          "title": "Add the thing",
          "description": "the description",
          "status": "{{status}}",
          "isDraft": {{(isDraft ? "true" : "false")}},
          "sourceRefName": "refs/heads/feature/thing",
          "targetRefName": "refs/heads/main",
          "createdBy": { "displayName": "Ada Lovelace" },
          "creationDate": "2026-07-29T10:00:00Z",
          "repository": {
            "name": "{{repoName}}"
            {{(projectName is null ? "" : $", \"project\": {{ \"name\": \"{projectName}\" }}")}}
          }
          {{(reviewers.Length == 0 ? "" : $", \"reviewers\": [{reviewers}]")}}
        }
        """;

    // ---------- auth and versioning ----------

    [Fact]
    public async Task Every_request_authenticates_as_basic_with_an_empty_user_and_the_pat_as_the_password()
    {
        using var handler = new FakeHttpHandler().Json("""{"value":[]}""");
        using var http = handler.Client();

        await AzureClient.ListProjectsAsync(http, Org, Pat, Ct);

        var authorization = handler.Only.Header("Authorization");
        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($":{Pat}"))}", authorization);

        // The empty user name is the whole trick, and it is what 1.7.2 base64-encodes.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization!["Basic ".Length..]));
        Assert.Equal($":{Pat}", decoded);
    }

    [Fact]
    public async Task No_request_carries_an_accept_or_user_agent_header_unlike_the_github_client()
    {
        using var handler = new FakeHttpHandler().Json("""{"value":[]}""");
        using var http = handler.Client();

        await AzureClient.ListProjectsAsync(http, Org, Pat, Ct);

        // GitHub answers 403 without a User-Agent and needs its own Accept; Azure needs neither, and the
        // reference sends neither. Sending them anyway would be a difference nobody asked for.
        Assert.Null(handler.Only.Header("User-Agent"));
        Assert.Null(handler.Only.Header("Accept"));
    }

    [Fact]
    public async Task Every_endpoint_pins_the_ga_contract()
    {
        using var handler = new FakeHttpHandler().Json("""{"value":[]}""");
        using var http = handler.Client();

        await AzureClient.ListProjectsAsync(http, Org, Pat, Ct);

        Assert.Contains("api-version=7.1", handler.Only.Uri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("preview", handler.Only.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_data_is_the_one_endpoint_on_the_preview_contract()
    {
        using var handler = new FakeHttpHandler().Json("""{"authenticatedUser":{"id":"user-guid"}}""");
        using var http = handler.Client();

        var id = await AzureClient.AuthenticatedUserIdAsync(http, Org, Pat, Ct);

        Assert.Equal("user-guid", id);

        // The endpoint never went GA and the server rejects a plain 7.1 with a 400 demanding the suffix.
        Assert.Equal(
            "https://dev.azure.com/contoso/_apis/connectionData?api-version=7.1-preview",
            handler.Only.Uri.ToString());
    }

    // ---------- organisation normalisation ----------

    [Theory]
    [InlineData("contoso", "contoso")]
    [InlineData("  contoso  ", "contoso")]
    [InlineData("contoso/", "contoso")]
    [InlineData("https://dev.azure.com/contoso", "contoso")]
    [InlineData("http://dev.azure.com/contoso", "contoso")]
    [InlineData("https://dev.azure.com/contoso/Web", "contoso")]
    [InlineData("https://contoso.visualstudio.com", "contoso")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection", "contoso")]
    public void An_organisation_saved_in_any_of_its_three_forms_reduces_to_the_bare_name(
        string saved, string expected) =>
        Assert.Equal(expected, AzureClient.NormalizeOrg(saved));

    [Fact]
    public void An_unrecognised_organisation_string_is_assumed_to_already_be_a_name()
    {
        // Not an error: whatever else the user typed is passed through trimmed, and the server decides.
        Assert.Equal("my org", AzureClient.NormalizeOrg("  my org  "));
    }

    [Fact]
    public void The_legacy_host_suffix_is_matched_case_sensitively()
    {
        // Same asymmetry the remote detection has, and for the same reason: 1.7.2 compares bytes.
        Assert.Equal("https://contoso.VisualStudio.com", AzureClient.NormalizeOrg("https://contoso.VisualStudio.com"));
    }

    [Fact]
    public async Task A_url_shaped_organisation_never_reaches_the_path_with_its_colon_intact()
    {
        using var handler = new FakeHttpHandler().Json("""{"value":[]}""");
        using var http = handler.Client();

        await AzureClient.ListProjectsAsync(http, "https://dev.azure.com/contoso", Pat, Ct);

        // Azure's IIS rejects a literal colon anywhere in the path, which is why normalisation exists.
        Assert.Equal("https://dev.azure.com/contoso/_apis/projects?api-version=7.1", handler.Only.Uri.ToString());
    }

    // ---------- path-segment encoding ----------

    [Theory]
    [InlineData("Web", "Web")]
    [InlineData("My Project", "My%20Project")]
    [InlineData("a-b.c_d~e", "a-b.c_d~e")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("café", "caf%C3%A9")]
    public void A_path_segment_keeps_only_the_unreserved_set_and_escapes_the_rest_in_upper_case_hex(
        string segment, string expected) =>
        Assert.Equal(expected, AzureClient.Encode(segment));

    [Fact]
    public async Task The_repository_is_encoded_when_a_single_pull_request_is_fetched()
    {
        using var handler = new FakeHttpHandler().Json(PullRequest());
        using var http = handler.Client();

        await AzureClient.GetPullRequestAsync(http, Org, "My Project", "Odd#Repo", 7, Pat, Ct);

        // A `#` is the character that makes the bug below visible: encoded it is a path segment, raw it is
        // the start of a fragment. Here it is encoded, so the whole path and query survive.
        Assert.Equal(
            "/contoso/My%20Project/_apis/git/repositories/Odd%23Repo/pullRequests/7",
            handler.Only.Uri.AbsolutePath);
        Assert.Equal(string.Empty, handler.Only.Uri.Fragment);
    }

    [Fact]
    public async Task The_repository_is_sent_raw_when_the_list_is_fetched_and_a_reserved_character_breaks_the_url()
    {
        // BUG-PROV-a, reproduced rather than fixed: encode_segment reaches repo_id in three of its twelve
        // call sites and not in the other nine, so a repository referenced by a name rather than a GUID
        // works or fails depending on which call you make.
        //
        // A space would not show it — Uri escapes a literal space itself, so raw and encoded reach the wire
        // identically. A `#` does show it: unencoded, everything after it is parsed as a fragment, which is
        // never sent. The request that leaves here asks for a repository called "Odd" and has lost its
        // searchCriteria entirely. That is the defect, pinned, so it cannot be quietly tidied.
        using var handler = new FakeHttpHandler().Json("""{"value":[]}""");
        using var http = handler.Client();

        await AzureClient.ListPullRequestsAsync(http, Org, "My Project", "Odd#Repo", Pat, Ct);

        var uri = handler.Only.Uri;
        Assert.Equal("/contoso/My%20Project/_apis/git/repositories/Odd", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
        Assert.StartsWith("#Repo/pullrequests", uri.Fragment, StringComparison.Ordinal);

        // The project, by contrast, is encoded at every call site — which is what makes this an
        // inconsistency rather than a policy.
        Assert.Contains("My%20Project", uri.AbsolutePath, StringComparison.Ordinal);
    }

    // ---------- listing and mapping ----------

    [Fact]
    public async Task Listing_pull_requests_asks_for_every_status_in_one_call_and_sets_no_page_size()
    {
        using var handler = new FakeHttpHandler().Json($$"""{"value":[{{PullRequest()}}]}""");
        using var http = handler.Client();

        var pulls = await AzureClient.ListPullRequestsAsync(http, Org, "Web", "Widget", Pat, Ct);

        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullrequests"
            + "?searchCriteria.status=all&api-version=7.1",
            handler.Only.Uri.ToString());

        // AMBIGUOUS-PROV-c: no $top and no paging, unlike GitHub's explicit per_page=100. Whatever the
        // server defaults to applies, and the source does not say what that is.
        Assert.DoesNotContain("$top", handler.Only.Uri.ToString(), StringComparison.Ordinal);
        Assert.Single(pulls);
    }

    [Fact]
    public async Task A_pull_request_maps_onto_the_shared_summary_shape()
    {
        using var handler = new FakeHttpHandler().Json($$"""{"value":[{{PullRequest()}}]}""");
        using var http = handler.Client();

        var pr = (await AzureClient.ListPullRequestsAsync(http, Org, "Web", "Widget", Pat, Ct))[0];

        Assert.Equal(7, pr.Id);
        Assert.Equal("Add the thing", pr.Title);
        Assert.Equal("the description", pr.Description);
        Assert.Equal("Ada Lovelace", pr.Author);
        Assert.Equal("2026-07-29T10:00:00Z", pr.CreatedAt);
        Assert.Equal("azure", pr.Provider);

        // Both refs lose the prefix Azure reports and requires.
        Assert.Equal("feature/thing", pr.SourceBranch);
        Assert.Equal("main", pr.TargetBranch);
    }

    [Fact]
    public async Task The_browser_url_is_synthesised_because_the_api_returns_no_page_a_person_can_open()
    {
        using var handler = new FakeHttpHandler().Json($$"""{"value":[{{PullRequest(repoName: "My Repo")}}]}""");
        using var http = handler.Client();

        var pr = (await AzureClient.ListPullRequestsAsync(http, "https://dev.azure.com/contoso", "My Project", "id", Pat, Ct))[0];

        // Built from the encoded organisation and project the caller passed plus the repository's own name.
        Assert.Equal(
            "https://dev.azure.com/contoso/My%20Project/_git/My%20Repo/pullrequest/7", pr.Url);
    }

    [Theory]
    [InlineData("completed", false, "merged")]
    [InlineData("abandoned", false, "closed")]
    [InlineData("active", true, "draft")]
    [InlineData("active", false, "open")]
    public async Task Azures_status_vocabulary_collapses_into_the_four_buckets_the_sidebar_groups_by(
        string status, bool isDraft, string expected)
    {
        using var handler = new FakeHttpHandler().Json($$"""{"value":[{{PullRequest(status: status, isDraft: isDraft)}}]}""");
        using var http = handler.Client();

        var pr = (await AzureClient.ListPullRequestsAsync(http, Org, "Web", "Widget", Pat, Ct))[0];

        Assert.Equal(expected, pr.Status);
    }

    [Fact]
    public async Task An_abandoned_draft_is_closed_rather_than_draft()
    {
        // The precedence is load-bearing: the status is checked before the draft flag, so a draft that was
        // abandoned shows up where the user closed it, not in the drafts group.
        using var handler = new FakeHttpHandler()
            .Json($$"""{"value":[{{PullRequest(status: "abandoned", isDraft: true)}}]}""");
        using var http = handler.Client();

        var pr = (await AzureClient.ListPullRequestsAsync(http, Org, "Web", "Widget", Pat, Ct))[0];

        Assert.Equal("closed", pr.Status);
    }

    [Fact]
    public async Task A_missing_description_reads_as_empty_rather_than_failing()
    {
        using var handler = new FakeHttpHandler().Json(
            """
            {
              "pullRequestId": 7, "title": "t", "status": "active",
              "sourceRefName": "refs/heads/f", "targetRefName": "refs/heads/main",
              "createdBy": { "displayName": "Ada" }, "creationDate": "now",
              "repository": { "name": "Widget" }
            }
            """);
        using var http = handler.Client();

        var pr = await AzureClient.GetPullRequestAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(string.Empty, pr.Summary.Description);
        Assert.Equal("open", pr.Summary.Status);
    }

    // ---------- a single pull request, and its canonical names ----------

    [Fact]
    public async Task Fetching_one_pull_request_recovers_the_names_a_guid_carrying_link_never_had()
    {
        using var handler = new FakeHttpHandler().Json(PullRequest(repoName: "Widget", projectName: "Web"));
        using var http = handler.Client();

        // What Azure's own notification e-mails link by.
        var pr = await AzureClient.GetPullRequestAsync(
            http, Org, "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "b1c2d3e4", 7, Pat, Ct);

        Assert.Equal("Web", pr.ProjectName);
        Assert.Equal("Widget", pr.RepoName);

        // And the browser URL uses the canonical project name, not the GUID that was asked for.
        Assert.Equal("https://dev.azure.com/contoso/Web/_git/Widget/pullrequest/7", pr.Summary.Url);
    }

    [Fact]
    public async Task A_response_with_no_project_falls_back_to_the_project_that_was_asked_about()
    {
        using var handler = new FakeHttpHandler().Json(PullRequest(projectName: null));
        using var http = handler.Client();

        var pr = await AzureClient.GetPullRequestAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal("Web", pr.ProjectName);
    }

    // ---------- creating ----------

    [Fact]
    public async Task Creating_a_pull_request_adds_the_full_ref_prefix_azure_requires()
    {
        using var handler = new FakeHttpHandler().Json(PullRequest());
        using var http = handler.Client();

        await AzureClient.CreatePullRequestAsync(
            http, Org, "Web", "Widget", "Add the thing", "the description", "feature/thing", "main", draft: true,
            Pat, Ct);

        Assert.Equal(HttpMethod.Post, handler.Only.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullrequests?api-version=7.1",
            handler.Only.Uri.ToString());

        // The inverse of what the read path strips.
        Assert.Contains("\"sourceRefName\":\"refs/heads/feature/thing\"", handler.Only.Body!, StringComparison.Ordinal);
        Assert.Contains("\"targetRefName\":\"refs/heads/main\"", handler.Only.Body!, StringComparison.Ordinal);
        Assert.Contains("\"isDraft\":true", handler.Only.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_already_prefixed_branch_is_doubled_rather_than_detected()
    {
        // CodeFlow 1.7.2 does not guard against it, and a silent correction here would be a behaviour this
        // port invented. Pinned so it reads as reproduced rather than overlooked.
        using var handler = new FakeHttpHandler().Json(PullRequest());
        using var http = handler.Client();

        await AzureClient.CreatePullRequestAsync(
            http, Org, "Web", "Widget", "t", "d", "refs/heads/feature", "main", draft: false, Pat, Ct);

        Assert.Contains(
            "\"sourceRefName\":\"refs/heads/refs/heads/feature\"", handler.Only.Body!, StringComparison.Ordinal);
    }

    // ---------- the viewer's own decision ----------

    [Theory]
    [InlineData(10, "approved")]
    [InlineData(5, "approved")]
    [InlineData(0, "none")]
    [InlineData(-5, "changes_requested")]
    [InlineData(-10, "changes_requested")]
    public async Task Azures_five_point_vote_collapses_into_the_three_strings_the_ui_reads(int vote, string expected)
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"authenticatedUser":{"id":"user-guid"}}""")
            .Json(PullRequest(reviewers: $$"""{"id":"user-guid","vote":{{vote}}}"""));
        using var http = handler.Client();

        var decision = await AzureClient.ViewerDecisionAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(expected, decision);
    }

    [Fact]
    public async Task Reading_a_decision_asks_for_the_identity_first_and_then_the_pull_request()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"authenticatedUser":{"id":"user-guid"}}""")
            .Json(PullRequest());
        using var http = handler.Client();

        await AzureClient.ViewerDecisionAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        // Two requests, in this order: an Azure vote is keyed by reviewer id, so the id has to be known
        // before the reviewer list means anything.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("connectionData", handler.Requests[0].Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("/pullRequests/7", handler.Requests[1].Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_id_matches_without_regard_to_case()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"authenticatedUser":{"id":"USER-GUID"}}""")
            .Json(PullRequest(reviewers: """{"id":"user-guid","vote":10}"""));
        using var http = handler.Client();

        Assert.Equal("approved", await AzureClient.ViewerDecisionAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    [Fact]
    public async Task A_viewer_who_is_not_a_reviewer_has_decided_nothing()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"authenticatedUser":{"id":"user-guid"}}""")
            .Json(PullRequest(reviewers: """{"id":"somebody-else","vote":-10}"""));
        using var http = handler.Client();

        Assert.Equal("none", await AzureClient.ViewerDecisionAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    [Fact]
    public async Task A_list_response_carrying_no_reviewers_is_read_as_no_vote()
    {
        // The list endpoint omits reviewers entirely, which is why the field defaults instead of being
        // required — reading a decision needs the single-PR call.
        using var handler = new FakeHttpHandler()
            .Json("""{"authenticatedUser":{"id":"user-guid"}}""")
            .Json(PullRequest());
        using var http = handler.Client();

        Assert.Equal("none", await AzureClient.ViewerDecisionAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    // ---------- acting ----------

    [Fact]
    public async Task Casting_a_vote_puts_it_on_the_reviewer_resource_which_also_adds_the_reviewer()
    {
        using var handler = new FakeHttpHandler()
            .Json("""{"authenticatedUser":{"id":"user-guid"}}""")
            .Json("{}");
        using var http = handler.Client();

        await AzureClient.SetReviewerVoteAsync(http, Org, "Web", "Widget", 7, 10, Pat, Ct);

        var vote = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, vote.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullRequests/7"
            + "/reviewers/user-guid?api-version=7.1",
            vote.Uri.ToString());
        Assert.Equal("""{"vote":10}""", vote.Body);
    }

    [Fact]
    public async Task Abandoning_a_pull_request_patches_its_status()
    {
        using var handler = new FakeHttpHandler().Json("{}");
        using var http = handler.Client();

        await AzureClient.AbandonPullRequestAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(HttpMethod.Patch, handler.Only.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullRequests/7?api-version=7.1",
            handler.Only.Uri.ToString());
        Assert.Equal("""{"status":"abandoned"}""", handler.Only.Body);
    }

    // ---------- comment threads ----------

    [Fact]
    public async Task An_anchored_thread_carries_its_file_and_line_range_through_untouched()
    {
        using var handler = new FakeHttpHandler().Json(
            """
            {"value":[{
              "id": 11,
              "status": "active",
              "threadContext": {
                "filePath": "/src/app.ts",
                "rightFileStart": { "line": 12 },
                "rightFileEnd": { "line": 14 }
              },
              "comments": [{
                "content": "  this needs a guard  ",
                "commentType": "text",
                "author": { "displayName": "Ada Lovelace" },
                "publishedDate": "2026-07-29T10:00:00Z"
              }]
            }]}
            """);
        using var http = handler.Client();

        var threads = await AzureClient.ListCommentThreadsAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        var thread = Assert.Single(threads);
        Assert.Equal(11, thread.Id);

        // The leading slash survives: the write path adds one, the read path does not take it away.
        Assert.Equal("/src/app.ts", thread.FilePath);
        Assert.Equal(12, thread.StartLine);
        Assert.Equal(14, thread.EndLine);

        // The content is trimmed, though.
        Assert.Equal("this needs a guard", Assert.Single(thread.Comments).Content);
        Assert.Equal("Ada Lovelace", thread.Comments[0].Author);
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("ACTIVE", true)]
    [InlineData("pending", true)]
    [InlineData("fixed", false)]
    [InlineData("wontFix", false)]
    [InlineData("closed", false)]
    [InlineData("byDesign", false)]
    public async Task Only_threads_azures_own_ui_still_treats_as_open_are_kept(string status, bool kept)
    {
        using var handler = new FakeHttpHandler().Json(Thread($"\"{status}\""));
        using var http = handler.Client();

        var threads = await AzureClient.ListCommentThreadsAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(kept ? 1 : 0, threads.Count);
    }

    [Fact]
    public async Task A_thread_with_no_status_at_all_counts_as_open()
    {
        using var handler = new FakeHttpHandler().Json(Thread(status: null));
        using var http = handler.Client();

        Assert.Single(await AzureClient.ListCommentThreadsAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    [Theory]
    [InlineData("\"text\"", true)]
    [InlineData(null, true)]
    [InlineData("\"system\"", false)]
    [InlineData("\"Text\"", false)]
    public async Task Only_comments_a_person_wrote_survive(string? commentType, bool kept)
    {
        // Azure files vote changes and iteration notices as comments under other types. The comparison is
        // exact, so "Text" is not "text" — 1.7.2 compares bytes.
        using var handler = new FakeHttpHandler().Json(Thread("\"active\"", commentType));
        using var http = handler.Client();

        var threads = await AzureClient.ListCommentThreadsAsync(http, Org, "Web", "Widget", 7, Pat, Ct);

        Assert.Equal(kept ? 1 : 0, threads.Count);
    }

    [Fact]
    public async Task A_thread_left_with_nothing_readable_disappears_entirely()
    {
        using var handler = new FakeHttpHandler().Json(
            """
            {"value":[{
              "id": 11, "status": "active",
              "comments": [
                { "content": "   ", "author": { "displayName": "Ada" }, "publishedDate": "now" },
                { "content": "a vote changed", "commentType": "system",
                  "author": { "displayName": "Azure" }, "publishedDate": "now" }
              ]
            }]}
            """);
        using var http = handler.Client();

        // A blank comment and a system comment leave a thread with no reviewer text, and an empty thread in
        // the UI would be a conversation with nothing in it.
        Assert.Empty(await AzureClient.ListCommentThreadsAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    [Fact]
    public async Task A_conversation_thread_carries_no_anchor()
    {
        using var handler = new FakeHttpHandler().Json(Thread("\"active\""));
        using var http = handler.Client();

        var thread = Assert.Single(await AzureClient.ListCommentThreadsAsync(http, Org, "Web", "Widget", 7, Pat, Ct));

        Assert.Null(thread.FilePath);
        Assert.Null(thread.StartLine);
        Assert.Null(thread.EndLine);
    }

    // ---------- iterations ----------

    [Fact]
    public async Task The_latest_iteration_is_the_last_one_listed()
    {
        using var handler = new FakeHttpHandler().Json("""{"value":[{"id":1},{"id":2},{"id":7}]}""");
        using var http = handler.Client();

        Assert.Equal(7, await AzureClient.GetLatestIterationIdAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    [Fact]
    public async Task An_empty_iteration_list_falls_back_to_the_first_rather_than_failing()
    {
        // Per the source's own comment: this should not happen for a real pull request, but a comment
        // landing on iteration 1 beats the whole review failing to post.
        using var handler = new FakeHttpHandler().Json("""{"value":[]}""");
        using var http = handler.Client();

        Assert.Equal(1, await AzureClient.GetLatestIterationIdAsync(http, Org, "Web", "Widget", 7, Pat, Ct));
    }

    [Fact]
    public async Task The_iteration_lookup_sends_the_repository_raw_even_though_its_caller_encodes_it()
    {
        // BUG-PROV-a at its least obvious: pull_request_diff encodes repo_id for its own changes and blob
        // URLs, then calls this, which interpolates it raw. One operation, both conventions.
        using var handler = new FakeHttpHandler().Json("""{"value":[{"id":3}]}""");
        using var http = handler.Client();

        await AzureClient.GetLatestIterationIdAsync(http, Org, "Web", "My Repo", 7, Pat, Ct);

        Assert.DoesNotContain("My%20Repo", handler.Only.Uri.ToString(), StringComparison.Ordinal);
    }

    // ---------- error mapping ----------

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404 Not Found")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "422 Unprocessable Entity")]
    [InlineData(HttpStatusCode.InternalServerError, "500 Internal Server Error")]
    public async Task A_status_that_is_not_about_the_credential_still_collapses(
        HttpStatusCode status, string rendered)
    {
        // Half of DIVERGENCE-PROV-b, and the half that did not change. CodeFlow 1.7.2 collapses every
        // non-2xx into one message shape; only the credential case was worth diverging over, so a 404
        // is still a 404 and reads exactly as it always did — including the HTML error page an unknown
        // organisation returns, interpolated whole.
        using var handler = new FakeHttpHandler().Respond(status, """{"message":"nope"}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureClient.ListProjectsAsync(http, Org, Pat, Ct));

        Assert.Equal($"Azure DevOps returned {rendered}: {{\"message\":\"nope\"}}", failure.Message);
        Assert.False(failure.Unauthorized);
        Assert.DoesNotContain(AzureException.RefusedPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401 Unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "403 Forbidden")]
    public async Task A_refused_credential_is_told_apart_from_everything_else(
        HttpStatusCode status, string rendered)
    {
        // The other half. AGENTS.md: expiry is "an expected state with its own UI path, never a
        // generic network error" — organisation policy caps PAT lifetime, so this is a state every user
        // reaches rather than an edge case. CodeFlow 1.7.2 does not distinguish it anywhere in the implementation;
        // this is the deliberate divergence, and 401 vs 403 is "wrong token" vs "token without the
        // scope", both of which mean the same thing to the person holding it.
        using var handler = new FakeHttpHandler().Respond(status, """{"message":"nope"}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureClient.ListProjectsAsync(http, Org, Pat, Ct));

        Assert.True(failure.Unauthorized);

        // The message is unchanged. The `CREDENTIAL_REFUSED: ` prefix belongs to one command boundary
        // — `list_pull_requests`, whose consumer only ever sees a string — and deliberately not to
        // every Azure error: the review posting summary renders these messages to a person, and a
        // sentinel mid-sentence there would be worse than the gap this closes.
        Assert.Equal($"Azure DevOps returned {rendered}: {{\"message\":\"nope\"}}", failure.Message);
        Assert.DoesNotContain(AzureException.RefusedPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transport_failure_says_it_could_not_reach_the_host()
    {
        using var handler = new FakeHttpHandler().TransportFailure("no such host is known");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureClient.ListProjectsAsync(http, Org, Pat, Ct));

        Assert.Equal("couldn't reach Azure DevOps: no such host is known", failure.Message);
    }

    [Fact]
    public async Task A_body_that_is_not_the_expected_json_says_the_response_was_unexpected()
    {
        using var handler = new FakeHttpHandler().Json("this is not json");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(
            () => AzureClient.ListProjectsAsync(http, Org, Pat, Ct));

        Assert.StartsWith("unexpected response from Azure DevOps: ", failure.Message, StringComparison.Ordinal);
    }

    // ---------- the manual link dialog's two lookups ----------

    [Fact]
    public async Task Listing_projects_unwraps_the_value_envelope()
    {
        using var handler = new FakeHttpHandler().Json(
            """{"value":[{"id":"p1","name":"Web"},{"id":"p2","name":"Mobile"}]}""");
        using var http = handler.Client();

        var projects = await AzureClient.ListProjectsAsync(http, Org, Pat, Ct);

        Assert.Equal(["Web", "Mobile"], projects.Select(p => p.Name));
        Assert.Equal("p1", projects[0].Id);
    }

    [Fact]
    public async Task Listing_repositories_is_scoped_to_one_project()
    {
        using var handler = new FakeHttpHandler().Json("""{"value":[{"id":"r1","name":"Widget"}]}""");
        using var http = handler.Client();

        var repos = await AzureClient.ListReposAsync(http, Org, "My Project", Pat, Ct);

        Assert.Equal(
            "https://dev.azure.com/contoso/My%20Project/_apis/git/repositories?api-version=7.1",
            handler.Only.Uri.AbsoluteUri);
        Assert.Equal("Widget", Assert.Single(repos).Name);
    }

    private static string Thread(string? status, string? commentType = "\"text\"") =>
        $$"""
        {"value":[{
          "id": 11,
          {{(status is null ? "" : $"\"status\": {status},")}}
          "comments": [{
            "content": "a real comment",
            {{(commentType is null ? "" : $"\"commentType\": {commentType},")}}
            "author": { "displayName": "Ada Lovelace" },
            "publishedDate": "2026-07-29T10:00:00Z"
          }]
        }]}
        """;
}
