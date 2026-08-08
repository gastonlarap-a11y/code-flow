using System.Net;
using System.Text.Json;
using CodeFlow.Providers.Azure;
using Xunit;

namespace CodeFlow.Tests.Providers;

/// <summary>
/// The four thread-writing calls, and the requests they build.
/// See <c>docs/business-rules/06-providers.md</c> <c>PROV-030</c>–<c>PROV-033</c>.
/// </summary>
/// <remarks>
/// All four are <c>UNVERIFIED</c> — <c>docs/business-rules/90-ambiguities.md</c> records that none has ever run against
/// a real API, here or in 1.7.2. So what can be verified is the request, and it is
/// verified exactly: method, URL and every field of the body.
/// </remarks>
public sealed class AzurePostingTests
{
    private const string Org = "contoso";
    private const string Pat = "test-pat";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A handler with the iteration lookup answered, since every anchored post starts there.</summary>
    private static FakeHttpHandler Anchoring() =>
        new FakeHttpHandler()
            .When("/iterations?", """{"value":[{"id":3}]}""")
            .When("/threads?", """{"id":501}""");

    // ---------- anchored threads ----------

    [Fact]
    public async Task An_anchored_thread_reads_the_latest_iteration_first_and_returns_its_id()
    {
        using var handler = Anchoring();
        using var http = handler.Client();

        var id = await AzureClient.PostCommentAnchoredAsync(
            http, Org, "Web", "Widget", 7, "el token viaja en la URL", "src/auth.ts", 12, 14, Pat, Ct);

        Assert.Equal(501, id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/iterations?", handler.Requests[0].Uri.ToString(), StringComparison.Ordinal);

        var post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullRequests/7/threads?api-version=7.1",
            post.Uri.ToString());
    }

    [Fact]
    public async Task The_anchored_body_carries_the_comment_the_range_and_the_iteration_window()
    {
        using var handler = Anchoring();
        using var http = handler.Client();

        await AzureClient.PostCommentAnchoredAsync(
            http, Org, "Web", "Widget", 7, "hallazgo", "src/auth.ts", 12, 14, Pat, Ct);

        var body = Body(handler, 1);

        var comment = body.GetProperty("comments").EnumerateArray().Single();
        Assert.Equal(0, comment.GetProperty("parentCommentId").GetInt64());
        Assert.Equal("hallazgo", comment.GetProperty("content").GetString());
        Assert.Equal(1, comment.GetProperty("commentType").GetInt32());
        Assert.Equal(1, body.GetProperty("status").GetInt32());

        var anchor = body.GetProperty("threadContext");
        Assert.Equal(12, anchor.GetProperty("rightFileStart").GetProperty("line").GetInt64());
        Assert.Equal(14, anchor.GetProperty("rightFileEnd").GetProperty("line").GetInt64());
        Assert.Equal(1, anchor.GetProperty("rightFileStart").GetProperty("offset").GetInt32());

        var iteration = body.GetProperty("pullRequestThreadContext").GetProperty("iterationContext");
        // Always from the first push to the latest one, and the latest is read now, not at review
        // time — BUG-REVIEW-a.
        Assert.Equal(1, iteration.GetProperty("firstComparingIteration").GetInt64());
        Assert.Equal(3, iteration.GetProperty("secondComparingIteration").GetInt64());
    }

    [Theory]
    [InlineData("src/auth.ts", "/src/auth.ts")]
    [InlineData("/src/auth.ts", "/src/auth.ts")]
    public async Task The_path_gains_a_leading_slash_when_it_has_none(string given, string expected)
    {
        using var handler = Anchoring();
        using var http = handler.Client();

        await AzureClient.PostCommentAnchoredAsync(
            http, Org, "Web", "Widget", 7, "hallazgo", given, 3, 3, Pat, Ct);

        // The opposite of GitHub, which strips one. Both are 1.7.2's.
        Assert.Equal(expected, Body(handler, 1).GetProperty("threadContext").GetProperty("filePath").GetString());
    }

    [Fact]
    public async Task An_inverted_range_ends_on_whichever_line_is_higher()
    {
        using var handler = Anchoring();
        using var http = handler.Client();

        await AzureClient.PostCommentAnchoredAsync(
            http, Org, "Web", "Widget", 7, "hallazgo", "src/auth.ts", 20, 5, Pat, Ct);

        var anchor = Body(handler, 1).GetProperty("threadContext");
        Assert.Equal(20, anchor.GetProperty("rightFileStart").GetProperty("line").GetInt64());
        Assert.Equal(20, anchor.GetProperty("rightFileEnd").GetProperty("line").GetInt64());
    }

    // ---------- plain threads ----------

    [Fact]
    public async Task A_plain_thread_omits_both_context_blocks()
    {
        using var handler = new FakeHttpHandler().Json("""{"id":502}""");
        using var http = handler.Client();

        var id = await AzureClient.PostCommentAsync(
            http, Org, "Web", "Widget", 7, "resumen de la revisión", Pat, Ct);

        Assert.Equal(502, id);
        // No iteration lookup: an unanchored comment has nothing to anchor against.
        Assert.Single(handler.Requests);

        var body = Body(handler, 0);
        Assert.False(body.TryGetProperty("threadContext", out _));
        Assert.False(body.TryGetProperty("pullRequestThreadContext", out _));
        Assert.Equal(
            "resumen de la revisión",
            body.GetProperty("comments").EnumerateArray().Single().GetProperty("content").GetString());
    }

    // ---------- replies and status ----------

    [Fact]
    public async Task A_reply_posts_under_the_hardcoded_first_comment()
    {
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        using var http = handler.Client();

        await AzureClient.ReplyThreadAsync(http, Org, "Web", "Widget", 7, 501, "sigue presente", Pat, Ct);

        Assert.Equal(HttpMethod.Post, handler.Only.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullRequests/7"
            + "/threads/501/comments?api-version=7.1",
            handler.Only.Uri.ToString());

        var body = Body(handler, 0);
        // Literal 1, never re-derived from the thread's actual root comment. Reproduced.
        Assert.Equal(1, body.GetProperty("parentCommentId").GetInt64());
        Assert.Equal("sigue presente", body.GetProperty("content").GetString());
        Assert.Equal(1, body.GetProperty("commentType").GetInt32());
    }

    [Fact]
    public async Task Marking_a_thread_fixed_patches_its_status()
    {
        using var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        using var http = handler.Client();

        await AzureClient.SetThreadStatusAsync(http, Org, "Web", "Widget", 7, 501, 2, Pat, Ct);

        Assert.Equal(HttpMethod.Patch, handler.Only.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/Web/_apis/git/repositories/Widget/pullRequests/7"
            + "/threads/501?api-version=7.1",
            handler.Only.Uri.ToString());
        // 2 is Azure's "fixed", which is how a resolved finding closes its thread.
        Assert.Equal(2, Body(handler, 0).GetProperty("status").GetInt32());
    }

    // ---------- the defect all four share ----------

    [Theory]
    [InlineData("anchored")]
    [InlineData("plain")]
    [InlineData("reply")]
    [InlineData("status")]
    public async Task The_repository_id_goes_into_the_url_unencoded(string call)
    {
        // BUG-PROV-a, pinned rather than fixed: the organisation and the project are encoded and the
        // repository id is not, in all four of these — while the read paths encode all three.
        using var handler = Anchoring().When("/threads/", "").Respond(HttpStatusCode.OK);
        using var http = handler.Client();

        switch (call)
        {
            case "anchored":
                await AzureClient.PostCommentAnchoredAsync(
                    http, Org, "My Project", "Odd#Repo", 7, "x", "a.ts", 1, 1, Pat, Ct);
                break;
            case "plain":
                await AzureClient.PostCommentAsync(http, Org, "My Project", "Odd#Repo", 7, "x", Pat, Ct);
                break;
            case "reply":
                await AzureClient.ReplyThreadAsync(http, Org, "My Project", "Odd#Repo", 7, 501, "x", Pat, Ct);
                break;
            default:
                await AzureClient.SetThreadStatusAsync(http, Org, "My Project", "Odd#Repo", 7, 501, 2, Pat, Ct);
                break;
        }

        // AbsoluteUri rather than ToString, which decodes the escapes back and would hide the point.
        var uri = handler.Requests[^1].Uri.AbsoluteUri;
        Assert.Contains("/My%20Project/", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("Odd%23Repo", uri, StringComparison.Ordinal);
        // The raw '#' ends the path and turns everything after it into a fragment. That is what the
        // defect looks like on the wire.
        Assert.EndsWith("/repositories/Odd", handler.Requests[^1].Uri.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);
    }

    // ---------- errors ----------

    [Fact]
    public async Task A_rejected_thread_reports_the_status_and_the_body()
    {
        using var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.Forbidden, """{"message":"no permission"}""");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(() =>
            AzureClient.PostCommentAsync(http, Org, "Web", "Widget", 7, "x", Pat, Ct));

        Assert.Equal("""Azure DevOps returned 403 Forbidden: {"message":"no permission"}""", failure.Message);
    }

    [Fact]
    public async Task A_created_thread_that_will_not_parse_has_its_own_wording()
    {
        using var handler = new FakeHttpHandler().Json("not json at all");
        using var http = handler.Client();

        var failure = await Assert.ThrowsAsync<AzureException>(() =>
            AzureClient.PostCommentAsync(http, Org, "Web", "Widget", 7, "x", Pat, Ct));

        // Every other endpoint in this client says "unexpected response from Azure DevOps". The
        // reference has two wordings for this, so this codebase does too.
        Assert.StartsWith("couldn't read Azure DevOps response: ", failure.Message, StringComparison.Ordinal);
    }

    private static JsonElement Body(FakeHttpHandler handler, int index) =>
        JsonDocument.Parse(handler.Requests[index].Body!).RootElement;
}
