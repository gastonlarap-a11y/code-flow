using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeFlow.Providers.GitHub;

/// <summary>A GitHub call that failed in a way the user should see.</summary>
/// <remarks>
/// <para>
/// The message is the wire error text verbatim: <c>IpcServer</c> puts an exception's message straight
/// into the JSON-RPC <c>error</c> field, so this type <em>is</em> 1.7.2's
/// <c>Result&lt;T, String&gt;</c> boundary and the strings are a contract, not diagnostics.
/// </para>
/// <para>
/// <see cref="SelfApproval"/> is the one thing a caller can branch on, following
/// <c>AzureException.Unauthorized</c>. It carries no extra message — the text is unchanged — so a
/// caller that ignores it behaves exactly as before. See <c>DIVERGENCE-PROV-c</c>.
/// </para>
/// </remarks>
public sealed class GitHubException(string message, bool selfApproval = false) : Exception(message)
{
    /// <summary>
    /// Marks a message as "GitHub will not let you approve your own pull request" for callers that
    /// only receive the string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same device as <c>CREDENTIAL_REFUSED: </c>, <c>CHECKOUT_CONFLICT: </c> and
    /// <c>QUOTA_EXCEEDED::</c> — a sentinel prefix on an error the renderer has to tell apart,
    /// because the transport carries a string and nothing else.
    /// </para>
    /// <para>
    /// <b>Applied at the command boundary, not at the throw site</b>, for the reason
    /// <c>AzureException.RefusedPrefix</c> gives: only the two act-on-a-pull-request commands need
    /// it, and a sentinel on every GitHub message would leave machine punctuation in the middle of
    /// sentences a person reads. In-process callers branch on <see cref="SelfApproval"/> instead.
    /// </para>
    /// </remarks>
    public const string SelfApprovalPrefix = "SELF_APPROVAL: ";

    /// <summary>GitHub refused the review because the pull request is the caller's own.</summary>
    /// <remarks>
    /// For callers holding the exception. Callers that only see <see cref="Exception.Message"/> —
    /// anything on the far side of the IPC boundary — read <see cref="SelfApprovalPrefix"/> instead.
    /// </remarks>
    public bool SelfApproval { get; } = selfApproval;
}

/// <summary>
/// The GitHub REST client.
/// </summary>
/// <remarks>
/// <para>
/// Static functions taking an injected <see cref="HttpClient"/>, matching
/// <c>Ai/Engines/OpenAi.cs</c>: there is no state worth holding, the process-wide client is already
/// threaded through the command registration, and a fake <see cref="HttpMessageHandler"/> is a better
/// test seam than an interface with one implementation.
/// </para>
/// <para>
/// <b>Almost no status-code branching, deliberately.</b> A 401 (token expired), a 403 (missing scope)
/// and a 404 (repo or PR gone) all produce the same message shape. That is what 1.7.2 does, so
/// it is reproduced — but it is a real shortcoming, not a design: it is why the app cannot tell the
/// user "your token expired" instead of showing them a raw status line.
/// </para>
/// <para>
/// The single exception is the 422 GitHub returns for approving one's own pull request
/// (<c>DIVERGENCE-PROV-c</c>), which the operator asked for by name after meeting the raw JSON in a
/// toast. It is a state every solo maintainer reaches on every pull request they open, and no
/// credential or retry can change it. Nothing else branches: a 422 that is not that one is still an
/// undifferentiated 422, asserted by its own test.
/// </para>
/// <para>
/// One GraphQL path lives here alongside the REST ones — resolving a review thread, which has no REST
/// endpoint. It is the client's only call that does not go through <see cref="Request"/>, because its
/// headers genuinely differ, and its only one that reads a response as raw JSON.
/// </para>
/// </remarks>
public static class GitHubClient
{
    /// <summary>
    /// The dated REST contract this client pins itself to.
    /// </summary>
    /// <remarks>Sent on every REST call, so behaviour is a known snapshot rather than "latest".</remarks>
    private const string ApiVersion = "2022-11-28";

    /// <summary>GitHub rejects any request without a User-Agent with a 403, unlike Azure DevOps.</summary>
    private const string UserAgent = "CodeFlow";

    /// <summary>GitHub's own hard ceiling for the changed-files endpoint: 3 pages of 100.</summary>
    private const int MaxFilePages = 3;

    private const int FilePageSize = 100;

    /// <summary>The REST API base for a host.</summary>
    /// <remarks>
    /// GitHub.com serves its API from a dedicated <c>api.</c> subdomain; a GitHub Enterprise Server
    /// serves it from <c>/api/v3</c> on the same host. Any host that is not github.com is treated as
    /// Enterprise — including a typo, since nothing here can tell the difference.
    /// </remarks>
    public static string ApiRoot(string host) =>
        host.Equals(RepoDetection.GitHubCom, StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com"
            : $"https://{host}/api/v3";

    /// <summary>The login the token authenticates as.</summary>
    /// <remarks>
    /// What Settings calls right after a token is saved, so a bad token or a wrong Enterprise host
    /// surfaces immediately instead of when PRs are first listed.
    /// </remarks>
    public static async Task<string> GetAuthenticatedUserAsync(
        HttpClient http, string host, string token, CancellationToken cancellationToken)
    {
        var user = await GetAsync(http, $"{ApiRoot(host)}/user", token,
            GitHubJsonContext.Default.RawUser, cancellationToken).ConfigureAwait(false);

        return user.Login;
    }

    /// <summary>
    /// The repository's pull requests, newest first, every state.
    /// </summary>
    /// <remarks>
    /// Capped at 100 with no further pagination — 1.7.2's own limit. A repository with more
    /// than 100 pull requests silently shows only the newest hundred, which is why reaching a specific
    /// old PR goes through <see cref="GetPullRequestAsync"/> instead.
    /// </remarks>
    public static async Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        HttpClient http, string host, string owner, string repo, string token, CancellationToken cancellationToken)
    {
        var url = $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls?state=all&per_page=100&sort=created&direction=desc";

        var raw = await GetAsync(http, url, token, GitHubJsonContext.Default.IReadOnlyListRawPull, cancellationToken)
            .ConfigureAwait(false);

        return [.. raw.Select(Map)];
    }

    /// <summary>
    /// One pull request by number.
    /// </summary>
    /// <remarks>
    /// Unlike the list this reaches a PR however old it is, which is what a pasted link needs.
    /// </remarks>
    public static async Task<PullRequestSummary> GetPullRequestAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken)
    {
        var raw = await GetAsync(http, PullUrl(host, owner, repo, number), token,
            GitHubJsonContext.Default.RawPull, cancellationToken).ConfigureAwait(false);

        return Map(raw);
    }

    /// <summary>
    /// The head commit SHA an inline comment must be anchored to.
    /// </summary>
    /// <remarks>
    /// GitHub requires <c>commit_id</c> on every review comment, and it is fetched fresh right before
    /// posting so it points at the PR's current tip.
    /// </remarks>
    public static async Task<string> HeadShaForAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken)
    {
        var raw = await GetAsync(http, PullUrl(host, owner, repo, number), token,
            GitHubJsonContext.Default.RawPull, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(raw.Head.Sha)
            ? throw new GitHubException("GitHub didn't report a head commit for this pull request")
            : raw.Head.Sha;
    }

    /// <summary>
    /// The pull request's unified diff, read from GitHub rather than from a local clone.
    /// </summary>
    /// <remarks>
    /// This is what makes reviewing a PR from nothing but its link possible. Asks for the <c>diff</c>
    /// media type first — one request, the real thing git would produce — and falls back to
    /// reassembling it from the per-file hunks when GitHub declines to render it whole (it answers 406
    /// past its size limit). <b>A non-2xx here is not an error</b>: it is the signal to fall back.
    /// Only a transport failure aborts.
    /// </remarks>
    public static async Task<string> PullRequestDiffAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, PullUrl(host, owner, repo, number), token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.diff"));

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var diff = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(diff))
            {
                return diff;
            }
        }

        return await DiffFromFilesAsync(http, host, owner, repo, number, token, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Opens a pull request. The head branch must already exist on the remote.</summary>
    public static async Task<PullRequestSummary> CreatePullRequestAsync(
        HttpClient http, string host, string owner, string repo,
        string title, string body, string head, string @base, bool draft, string token,
        CancellationToken cancellationToken)
    {
        var raw = await SendJsonAsync(
            http,
            HttpMethod.Post,
            $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls",
            token,
            new CreatePullRequestBody(title, head, @base, body, draft),
            GitHubJsonContext.Default.CreatePullRequestBody,
            GitHubJsonContext.Default.RawPull,
            cancellationToken).ConfigureAwait(false);

        return Map(raw);
    }

    /// <summary>
    /// Which decision the signed-in user has already recorded on this pull request.
    /// </summary>
    /// <remarks>
    /// Read from the host rather than remembered locally: the user may well have approved it from the
    /// website, from another machine, or before this app ever saw the PR. Two requests — the login,
    /// then the reviews. GitHub keeps every review ever submitted, in order, so the last one carrying
    /// a verdict wins, and a <c>DISMISSED</c> review is a verdict being taken back.
    /// </remarks>
    public static async Task<string> ViewerDecisionAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken)
    {
        var login = await GetAuthenticatedUserAsync(http, host, token, cancellationToken).ConfigureAwait(false);

        var url = $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls/{number}/reviews?per_page=100";
        var reviews = await GetAsync(http, url, token, GitHubJsonContext.Default.IReadOnlyListRawReview,
            cancellationToken).ConfigureAwait(false);

        var decision = "none";
        foreach (var review in reviews.Where(r => r.User.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
        {
            decision = review.State.ToUpperInvariant() switch
            {
                "APPROVED" => "approved",
                "CHANGES_REQUESTED" => "changes_requested",
                "DISMISSED" => "none",
                // COMMENTED and PENDING are not verdicts — they leave the previous one standing.
                _ => decision,
            };
        }

        return decision;
    }

    /// <summary>
    /// Submits a review verdict.
    /// </summary>
    /// <remarks>
    /// <paramref name="event"/> is GitHub's own verb, <c>APPROVE</c> or <c>REQUEST_CHANGES</c>. GitHub
    /// infers the reviewer from the token, so there is no user lookup. `UNVERIFIED`: this write has
    /// never run against a real API — see <c>docs/business-rules/90-ambiguities.md</c>.
    /// </remarks>
    public static Task SubmitReviewAsync(
        HttpClient http, string host, string owner, string repo, long number,
        string @event, string body, string token, CancellationToken cancellationToken) =>
        SendJsonAsync(
            http,
            HttpMethod.Post,
            $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls/{number}/reviews",
            token,
            new SubmitReviewBody(@event, string.IsNullOrWhiteSpace(body) ? null : body),
            GitHubJsonContext.Default.SubmitReviewBody,
            cancellationToken);

    /// <summary>Closes the pull request without merging.</summary>
    /// <remarks>`UNVERIFIED`, as with <see cref="SubmitReviewAsync"/>.</remarks>
    public static Task CloseAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            http,
            HttpMethod.Patch,
            PullUrl(host, owner, repo, number),
            token,
            new CloseBody("closed"),
            GitHubJsonContext.Default.CloseBody,
            cancellationToken);

    /// <summary>
    /// Posts an inline review comment anchored to a file and a line, and returns its comment id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is what a re-review replies to and resolves, so a finding keeps one conversation for the
    /// pull request's whole life instead of a new comment each time.
    /// </para>
    /// <para>
    /// The path is sent with any leading slash <b>stripped</b> — the opposite of Azure DevOps, which
    /// adds one. Both are 1.7.2's, and the divergence is deliberate.
    /// </para>
    /// <para>
    /// <c>UNVERIFIED</c>: this write has never run against a real API — see
    /// <c>docs/business-rules/90-ambiguities.md</c>.
    /// </para>
    /// </remarks>
    /// <param name="commitId">
    /// The commit the comment anchors to. Its caller fetches the pull request's <em>current</em> head
    /// rather than reading the SHA the review actually analysed — see <c>BUG-REVIEW-a</c>.
    /// </param>
    public static async Task<long> PostCommentAnchoredAsync(
        HttpClient http, string host, string owner, string repo, long number,
        string content, string filePath, long startLine, long endLine, string commitId, string token,
        CancellationToken cancellationToken)
    {
        // GitHub anchors to the last line of the range; start_line marks where the highlight begins,
        // and is omitted entirely for a single line.
        var line = Math.Max(endLine, startLine);
        var multiLine = startLine < line;

        var created = await SendJsonAsync(
            http,
            HttpMethod.Post,
            $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls/{number}/comments",
            token,
            new AnchoredCommentBody(
                content, commitId, filePath.TrimStart('/'), line, "RIGHT",
                multiLine ? startLine : null,
                multiLine ? "RIGHT" : null),
            GitHubJsonContext.Default.AnchoredCommentBody,
            GitHubJsonContext.Default.CommentCreated,
            cancellationToken).ConfigureAwait(false);

        return created.Id;
    }

    /// <summary>Posts a comment on the pull request's conversation, anchored to nothing.</summary>
    /// <remarks>
    /// A different endpoint from the inline one: GitHub models a pull request as an issue, and this is
    /// an issue comment. Used for the summary and as the fallback for a finding whose location could
    /// not be parsed. The id comes back and is discarded — issue comments are not threaded.
    /// <c>UNVERIFIED</c>.
    /// </remarks>
    public static async Task<long> PostCommentAsync(
        HttpClient http, string host, string owner, string repo, long number, string content, string token,
        CancellationToken cancellationToken)
    {
        var created = await SendJsonAsync(
            http,
            HttpMethod.Post,
            $"{ApiRoot(host)}/repos/{owner}/{repo}/issues/{number}/comments",
            token,
            new CommentBody(content),
            GitHubJsonContext.Default.CommentBody,
            GitHubJsonContext.Default.CommentCreated,
            cancellationToken).ConfigureAwait(false);

        return created.Id;
    }

    /// <summary>Replies to an existing inline review comment, keeping one conversation per finding.</summary>
    /// <remarks>GitHub threads replies off the root comment's id. <c>UNVERIFIED</c>.</remarks>
    public static Task ReplyReviewCommentAsync(
        HttpClient http, string host, string owner, string repo, long number, long commentId,
        string content, string token, CancellationToken cancellationToken) =>
        SendJsonAsync(
            http,
            HttpMethod.Post,
            $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls/{number}/comments/{commentId}/replies",
            token,
            new CommentBody(content),
            GitHubJsonContext.Default.CommentBody,
            cancellationToken);

    /// <summary>
    /// Marks the review thread that owns a comment as resolved — GitHub's equivalent of Azure's
    /// <c>fixed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no REST endpoint for it, so this is the client's only GraphQL path, and it is two
    /// calls: find the thread whose comments include this one by <c>databaseId</c>, then resolve it.
    /// Both are capped at the first 100 threads and the first 100 comments of each — a pull request
    /// past either cap silently finds nothing.
    /// </para>
    /// <para>
    /// Best-effort at its call site: a failure leaves the thread open and never fails the post.
    /// <c>UNVERIFIED</c>.
    /// </para>
    /// </remarks>
    public static async Task ResolveReviewThreadAsync(
        HttpClient http, string host, string owner, string repo, long number, long commentId, string token,
        CancellationToken cancellationToken)
    {
        var endpoint = GraphQlRoot(host);

        // Interpolated into the query text rather than sent as GraphQL variables, exactly as the
        // reference does it. Reproduced rather than tidied: variables would change the request body.
        using var found = await GraphQlAsync(
            http,
            endpoint,
            token,
            $$"""query { repository(owner: "{{owner}}", name: "{{repo}}") { pullRequest(number: {{number}}) { reviewThreads(first: 100) { nodes { id isResolved comments(first: 100) { nodes { databaseId } } } } } } }""",
            "GitHub GraphQL returned",
            cancellationToken).ConfigureAwait(false);

        var threadId = ThreadIdFor(found.RootElement, commentId);

        using var _ = await GraphQlAsync(
            http,
            endpoint,
            token,
            $$"""mutation { resolveReviewThread(input: { threadId: "{{threadId}}" }) { thread { isResolved } } }""",
            "GitHub GraphQL resolve returned",
            cancellationToken).ConfigureAwait(false);

        // The mutation's own answer is not inspected — 1.7.2 does not check isResolved either.
    }

    /// <summary>
    /// The pull request's existing comments, as threads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two requests: the inline review comments, grouped into threads by their reply chain, and the
    /// conversation-level comments, appended as their own location-less threads so nothing a reviewer
    /// wrote is dropped. Empty comments are discarded.
    /// </para>
    /// <para>
    /// <c>AMBIGUOUS-PROV-a</c>: the grouping assumes the API returns each reply after the comment it
    /// replies to. Neither verified nor enforced — a reply arriving first would start a thread of its
    /// own, carrying its own file and line.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<PrCommentThread>> ListCommentThreadsAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken)
    {
        var root = ApiRoot(host);

        var inline = await GetAsync(http, $"{root}/repos/{owner}/{repo}/pulls/{number}/comments?per_page=100",
            token, GitHubJsonContext.Default.IReadOnlyListRawReviewComment, cancellationToken).ConfigureAwait(false);

        // Insertion-ordered so threads come back in the order their roots first appeared.
        var order = new List<long>();
        var threads = new Dictionary<long, (string? Path, long? Start, long? End, List<PrThreadComment> Comments)>();

        foreach (var comment in inline)
        {
            if (Content(comment.Body) is not { } content)
            {
                continue;
            }

            // A reply points at the comment that started the thread; a root replies to nothing, so it
            // keys on its own id.
            var rootId = comment.InReplyToId ?? comment.Id;
            var entry = new PrThreadComment(comment.User.Login, content, comment.CreatedAt);

            if (threads.TryGetValue(rootId, out var thread))
            {
                thread.Comments.Add(entry);
                continue;
            }

            // The location comes from the first comment seen for the thread; a reply's own path and
            // line are ignored.
            order.Add(rootId);
            threads[rootId] = (comment.Path, comment.StartLine ?? comment.Line, comment.Line, [entry]);
        }

        var result = order
            .Select(id => (Id: id, Thread: threads[id]))
            .Select(pair => new PrCommentThread(
                pair.Id, pair.Thread.Path, pair.Thread.Start, pair.Thread.End, pair.Thread.Comments))
            .ToList();

        var conversation = await GetAsync(http, $"{root}/repos/{owner}/{repo}/issues/{number}/comments?per_page=100",
            token, GitHubJsonContext.Default.IReadOnlyListRawIssueComment, cancellationToken).ConfigureAwait(false);

        foreach (var comment in conversation)
        {
            if (Content(comment.Body) is not { } content)
            {
                continue;
            }

            result.Add(new PrCommentThread(comment.Id, null, null, null,
                [new PrThreadComment(comment.User.Login, content, comment.CreatedAt)]));
        }

        return result;
    }

    /// <summary>
    /// Reassembles a unified diff from the changed-files endpoint.
    /// </summary>
    /// <remarks>
    /// Each entry carries only its hunks, so the <c>diff --git</c> / <c>---</c> / <c>+++</c> headers —
    /// the part every diff parser keys on — have to be put back. Note that the <c>diff --git</c> line
    /// always names real paths even for an addition or a deletion, where <c>---</c> and <c>+++</c> do
    /// use <c>/dev/null</c>. Binary and over-size files have no hunks at all and are listed as a bare
    /// header, which is honest: the reader is told the file changed but not how.
    /// </remarks>
    private static async Task<string> DiffFromFilesAsync(
        HttpClient http, string host, string owner, string repo, long number, string token,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();

        for (var page = 1; page <= MaxFilePages; page++)
        {
            var url =
                $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls/{number}/files?per_page={FilePageSize}&page={page}";

            var files = await GetAsync(http, url, token, GitHubJsonContext.Default.IReadOnlyListRawPullFile,
                cancellationToken).ConfigureAwait(false);

            foreach (var file in files)
            {
                var previous = file.PreviousFilename ?? file.Filename;
                var oldPath = file.Status == "added" ? "/dev/null" : $"a/{previous}";
                var newPath = file.Status == "removed" ? "/dev/null" : $"b/{file.Filename}";

                output.Append(CultureInfo.InvariantCulture,
                    $"diff --git a/{previous} b/{file.Filename}\n--- {oldPath}\n+++ {newPath}\n");

                if (file.Patch is { } patch)
                {
                    output.Append(patch);
                    if (!patch.EndsWith('\n'))
                    {
                        output.Append('\n');
                    }
                }
                else
                {
                    output.Append("(binary or too large to display)\n");
                }
            }

            if (files.Count < FilePageSize)
            {
                break;
            }
        }

        return output.Length == 0
            ? throw new GitHubException("GitHub reported no changed files for this pull request")
            : output.ToString();
    }

    /// <summary>
    /// Collapses GitHub's state, draft and merged flags into the four buckets the sidebar groups by.
    /// </summary>
    /// <remarks>
    /// Order matters: a merged pull request is also closed, and it reports as merged.
    /// </remarks>
    private static string BucketStatus(RawPull pull) =>
        pull.MergedAt is not null ? "merged"
        : pull.State == "closed" ? "closed"
        : pull.Draft ? "draft"
        : "open";

    private static PullRequestSummary Map(RawPull pull) => new(
        pull.Number,
        pull.Title,
        pull.Body ?? string.Empty,
        BucketStatus(pull),
        pull.Head.Ref,
        pull.Base.Ref,
        pull.User.Login,
        pull.CreatedAt,
        pull.HtmlUrl,
        "github");

    /// <summary>A comment's text, trimmed, or null when there is nothing left of it.</summary>
    private static string? Content(string? body) =>
        string.IsNullOrWhiteSpace(body) ? null : body.Trim();

    private static string PullUrl(string host, string owner, string repo, long number) =>
        $"{ApiRoot(host)}/repos/{owner}/{repo}/pulls/{number}";

    /// <summary>The GraphQL endpoint for a host.</summary>
    /// <remarks>
    /// Not <see cref="ApiRoot"/> plus a suffix: GitHub.com serves it from <c>api.github.com/graphql</c>
    /// and an Enterprise Server from <c>/api/graphql</c> — <b>not</b> <c>/api/v3/graphql</c>, which is
    /// the REST base. Deriving one from the other would break every Enterprise host.
    /// </remarks>
    private static string GraphQlRoot(string host) =>
        host.Equals(RepoDetection.GitHubCom, StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com/graphql"
            : $"https://{host}/api/graphql";

    /// <summary>Posts one GraphQL document and returns the parsed answer.</summary>
    /// <remarks>
    /// <para>
    /// Its own request builder, carrying <em>only</em> <c>Authorization</c> and <c>User-Agent</c>: the
    /// REST builder's <c>Accept</c> and <c>X-GitHub-Api-Version</c> are REST-only, and 1.7.2
    /// does not send them here.
    /// </para>
    /// <para>
    /// The failure message is the caller's prefix plus the status and <b>no body</b> — every REST
    /// error in this client includes the body, and this one does not. That asymmetry is the
    /// reference's, reproduced rather than tidied.
    /// </para>
    /// </remarks>
    private static async Task<JsonDocument> GraphQlAsync(
        HttpClient http, string endpoint, string token, string document, string failurePrefix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Content = JsonContent.Create(new GraphQlRequest(document), GitHubJsonContext.Default.GraphQlRequest);

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubException($"{failurePrefix} {StatusText.Of(response.StatusCode)}");
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException failure)
        {
            // Bare, with none of the "unexpected response from GitHub" framing the REST paths add —
            // 1.7.2 surfaces the parse error itself here.
            throw new GitHubException(StatusText.Reason(failure));
        }
    }

    /// <summary>
    /// The node id of the review thread whose comments include this one.
    /// </summary>
    /// <remarks>
    /// Walked as raw JSON rather than deserialised into records: this is the only GraphQL shape in the
    /// client, four levels of single-field nesting deep, and a tree of types nothing else would use
    /// buys nothing over reading the three leaves it actually needs.
    /// </remarks>
    private static string ThreadIdFor(JsonElement response, long commentId)
    {
        // Every link is checked for being an object before it is walked, because any of them can come
        // back as JSON null: a token that cannot see the repository gets `data.repository: null`, not
        // an error status. 1.7.2's index-and-as_array() degrades the same way.
        var nodes = Child(response, "data", "repository", "pullRequest", "reviewThreads", "nodes");
        if (nodes is not { ValueKind: JsonValueKind.Array })
        {
            throw new GitHubException("no review threads in GraphQL response");
        }

        foreach (var thread in nodes.Value.EnumerateArray())
        {
            if (Owns(thread, commentId) && thread.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString()!;
            }
        }

        throw new GitHubException("couldn't find the review thread for this comment");

        static bool Owns(JsonElement thread, long commentId) =>
            Child(thread, "comments", "nodes") is { ValueKind: JsonValueKind.Array } comments
            && comments.EnumerateArray().Any(comment =>
                Child(comment, "databaseId") is { ValueKind: JsonValueKind.Number } databaseId
                && databaseId.GetInt64() == commentId);
    }

    /// <summary>Walks a chain of object properties, answering null the moment one is missing or not an object.</summary>
    private static JsonElement? Child(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current;
    }

    // ---------- transport ----------

    /// <summary>Builds a request carrying the four headers every REST call needs.</summary>
    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);

        // Both classic and fine-grained tokens authenticate as a bearer on the modern REST API, so one
        // scheme covers whatever the user pasted.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);

        return request;
    }

    private static async Task<T> GetAsync<T>(
        HttpClient http, string url, string token, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, token);
        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request with a JSON body and ignores whatever came back.</summary>
    private static async Task SendJsonAsync<TBody>(
        HttpClient http, HttpMethod method, string url, string token,
        TBody body, JsonTypeInfo<TBody> bodyType, CancellationToken cancellationToken)
    {
        using var request = Request(method, url, token);
        request.Content = JsonContent.Create(body, bodyType);

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request with a JSON body and deserialises the resource it created.</summary>
    private static async Task<TResult> SendJsonAsync<TBody, TResult>(
        HttpClient http, HttpMethod method, string url, string token,
        TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
    {
        using var request = Request(method, url, token);
        request.Content = JsonContent.Create(body, bodyType);

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(response, resultType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request, turning a transport failure into 1.7.2's message.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
            when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new GitHubException($"couldn't reach GitHub: {StatusText.Reason(failure)}");
        }
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        try
        {
            var value = await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false);
            return value ?? throw new JsonException("the response body was JSON null");
        }
        catch (Exception failure) when (failure is JsonException or NotSupportedException)
        {
            throw new GitHubException($"unexpected response from GitHub: {StatusText.Reason(failure)}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        // DIVERGENCE-PROV-c. The message is byte-for-byte what it always was — every caller that only
        // reads `.Message` is unaffected — and the only thing added is a flag. The prefix is *not*
        // applied here; see GitHubException.SelfApprovalPrefix for why it goes on at the two command
        // boundaries that consume it.
        //
        // The status alone is not enough to identify this: 422 is GitHub's answer to every validation
        // failure, including a review body that is empty when it may not be. So the sentence has to be
        // matched too, and that sentence is GitHub's to change — hence XLANG-013 recording it as a
        // literal owned by someone else. If GitHub rewords it, this degrades to today's behaviour: a
        // raw 422, which is what every other validation failure already shows.
        var selfApproval = response.StatusCode == HttpStatusCode.UnprocessableEntity
            && body.Contains(SelfApprovalSentence, StringComparison.OrdinalIgnoreCase);

        throw new GitHubException(
            $"GitHub returned {StatusText.Of(response.StatusCode)}: {body}", selfApproval);
    }

    /// <summary>
    /// The sentence GitHub puts in a 422 when the reviewer is the pull request's own author.
    /// </summary>
    /// <remarks>
    /// Verbatim from the API, missing space and all — <c>"Can not approve"</c>, not <c>"Cannot
    /// approve"</c>. <c>XLANG-013</c>. Matched case-insensitively so a change in capitalisation alone
    /// does not break it, which is the cheapest of the ways this can rot.
    /// </remarks>
    private const string SelfApprovalSentence = "Can not approve your own pull request";

    /// <summary>The body as text, or empty when it cannot be read — never a failure of its own.</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }
}
