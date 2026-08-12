using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeFlow.Providers.Azure;

/// <summary>An Azure DevOps call that failed in a way the user should see.</summary>
/// <remarks>
/// <para>
/// The message is the wire error text verbatim: <c>IpcServer</c> puts an exception's message straight
/// into the JSON-RPC <c>error</c> field, so this type <em>is</em> 1.7.2's
/// <c>Result&lt;T, String&gt;</c> boundary and the strings are a contract, not diagnostics.
/// </para>
/// <para>
/// <see cref="Unauthorized"/> is the one thing a caller can branch on. It carries no extra message —
/// the text is unchanged — so a caller that ignores it behaves exactly as before, and one that reads
/// it can offer the user the way out. See <c>DIVERGENCE-PROV-b</c>.
/// </para>
/// </remarks>
public sealed class AzureException(string message, bool unauthorized = false) : Exception(message)
{
    /// <summary>
    /// Marks a message as "the credential was refused" for callers that only receive the string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same device as <c>CHECKOUT_CONFLICT: </c>, <c>QUOTA_EXCEEDED::</c> and
    /// <c>RUN_CANCELLED::</c> — a sentinel prefix on an error the renderer has to tell apart, because
    /// the transport carries a string and nothing else. Following the three prefixes that already
    /// exist beats inventing a fourth mechanism for the same problem.
    /// </para>
    /// <para>
    /// <b>Applied at the command boundary, not at the throw site.</b> Only the pull-request list needs
    /// it; every other caller of an Azure call renders the message to a person, and putting a sentinel
    /// on all of them would leave <c>CREDENTIAL_REFUSED: </c> sitting in the middle of the review
    /// posting summary. In-process callers branch on <see cref="Unauthorized"/> instead.
    /// </para>
    /// </remarks>
    public const string RefusedPrefix = "CREDENTIAL_REFUSED: ";

    /// <summary>The call was refused for the credential, not for what it asked for.</summary>
    /// <remarks>
    /// For callers holding the exception. Callers that only see <see cref="Exception.Message"/> —
    /// anything on the far side of the IPC boundary — read <see cref="RefusedPrefix"/> instead.
    /// </remarks>
    public bool Unauthorized { get; } = unauthorized;
}

/// <summary>
/// The Azure DevOps REST client.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <c>GitHubClient</c>: static functions over an injected <see cref="HttpClient"/>, no
/// state worth holding, and a fake <see cref="HttpMessageHandler"/> as the test seam.
/// </para>
/// <para>
/// <b>One status-code branch, and only one</b> — <c>DIVERGENCE-PROV-b</c>. CodeFlow 1.7.2 collapses
/// every non-2xx from every endpoint into a single message shape (the alternative repeats
/// <c>if !status.is_success()</c> at six call sites and branches nowhere), which makes an expired PAT
/// indistinguishable from a missing repository. <c>.claude/rules/dotnet.md</c> asks for the opposite in so
/// many words — expiry is "an expected state with its own UI path, never a generic network error" —
/// and organisation policy caps PAT lifetime, so this is a state every user reaches eventually rather
/// than an edge case. So <c>401</c> and <c>403</c> are marked, and nothing else is: the gap the brief
/// names is closed and the rest of 1.7.2's behaviour is left alone.
/// </para>
/// <para>
/// Observed against the real API while verifying this slice: an unknown organisation answers <c>404</c>
/// with an HTML error <em>page</em>, and because the body is interpolated whole, that page becomes the
/// message the user reads. CodeFlow 1.7.2 has the same behaviour, and so does this — a 404 is still
/// a 404. Only the credential case was worth diverging over.
/// </para>
/// <para>
/// <b>Every call is organisation-scoped.</b> There is no ambient credential: the PAT is a parameter on
/// each function and is used only in the <c>Authorization</c> header — never in a body, a diff or a
/// comment. Azure keys a PAT per organisation, where GitHub keys a token per host.
/// </para>
/// <para>
/// The four thread-writing calls — anchored and plain comment creation, reply, and thread status —
/// arrived with the review-posting flow that is their only caller. All four are <c>UNVERIFIED</c> per
/// <c>docs/business-rules/90-ambiguities.md</c>: they compile and are covered against a fake transport, and none has run
/// against a real API.
/// </para>
/// </remarks>
public static class AzureClient
{
    /// <summary>The REST contract every call pins itself to.</summary>
    /// <remarks>
    /// <c>internal</c> rather than private so <see cref="AzureWorkItemClient"/> pins the same contract
    /// instead of declaring a second <c>"7.1"</c> that could drift from this one.
    /// </remarks>
    internal const string ApiVersion = "7.1";

    /// <summary>
    /// The contract <c>connectionData</c> alone requires.
    /// </summary>
    /// <remarks>
    /// That endpoint never went GA and the server rejects a plain <c>7.1</c> on it with a 400 demanding
    /// the suffix, so this is not a stale constant that could be tidied away.
    /// </remarks>
    private const string PreviewApiVersion = "7.1-preview";

    /// <summary>Azure's own ceiling on an iteration's change list.</summary>
    private const int MaxChangedPaths = 1000;

    /// <summary>Files past this are dropped from a diff, with a note appended in their place.</summary>
    private const int MaxDiffFiles = 80;

    /// <summary>A blob larger than this on either side is listed rather than diffed.</summary>
    private const int MaxBlobBytes = 512 * 1024;

    /// <summary>How many files render at once. Each render issues up to two blob reads, in sequence.</summary>
    private const int MaxConcurrentRenders = 6;

    /// <summary>The all-zero object id Azure uses for "no blob on this side".</summary>
    private const string NullObjectId = "0000000000000000000000000000000000000000";

    // ---------- the manual link dialog ----------

    /// <summary>Every team project in an organisation.</summary>
    /// <remarks>No pagination: 1.7.2 sets no page size and unwraps whatever one call returns.</remarks>
    public static async Task<IReadOnlyList<AdoProject>> ListProjectsAsync(
        HttpClient http, string org, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{OrgSegment(org)}/_apis/projects?api-version={ApiVersion}";
        var response = await GetAsync(http, url, pat, AzureJsonContext.Default.AzureListAdoProject, cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    /// <summary>Every git repository in a team project.</summary>
    public static async Task<IReadOnlyList<AdoRepo>> ListReposAsync(
        HttpClient http, string org, string project, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
            + $"/_apis/git/repositories?api-version={ApiVersion}";

        var response = await GetAsync(http, url, pat, AzureJsonContext.Default.AzureListAdoRepo, cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }

    // ---------- pull requests ----------

    /// <summary>
    /// Every pull request on a repository, active, completed and abandoned in one call.
    /// </summary>
    /// <remarks>
    /// <c>AMBIGUOUS-PROV-c</c>: no <c>$top</c> and no pagination, unlike GitHub's explicit
    /// <c>per_page=100</c>, so whatever page size the server defaults to applies uncontrolled. The source
    /// does not say what that is, and neither does this.
    /// </remarks>
    public static async Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        HttpClient http, string org, string project, string repoId, string pat, CancellationToken cancellationToken)
    {
        var orgSegment = OrgSegment(org);
        var projectSegment = Encode(project);

        // repoId raw: BUG-PROV-a.
        var url = $"https://dev.azure.com/{orgSegment}/{projectSegment}/_apis/git/repositories/{repoId}"
            + $"/pullrequests?searchCriteria.status=all&api-version={ApiVersion}";

        var response = await GetAsync(
            http, url, pat, AzureJsonContext.Default.AzureListRawPullRequest, cancellationToken)
            .ConfigureAwait(false);

        return [.. response.Value.Select(pr => Map(orgSegment, projectSegment, pr))];
    }

    /// <summary>
    /// One pull request by id, plus the canonical names Azure reports for its project and repository.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ListPullRequestsAsync"/> this reaches a pull request however far down the list it
    /// is, which is what a pasted link needs. Both <paramref name="project"/> and
    /// <paramref name="repoId"/> may be a GUID or a name — Azure's own notification e-mails link by GUID —
    /// and a GUID cannot be matched against a local clone's git remote, which only ever spells out names.
    /// So the names off the response are returned alongside the summary, and the browser URL is built from
    /// the canonical project name rather than from whatever the caller passed.
    /// </remarks>
    public static async Task<AzurePullRequest> GetPullRequestAsync(
        HttpClient http, string org, string project, string repoId, long prId, string pat,
        CancellationToken cancellationToken)
    {
        var orgSegment = OrgSegment(org);

        // repoId encoded here and raw in most siblings: BUG-PROV-a.
        var url = $"https://dev.azure.com/{orgSegment}/{Encode(project)}/_apis/git/repositories/{Encode(repoId)}"
            + $"/pullRequests/{prId}?api-version={ApiVersion}";

        var raw = await GetAsync(http, url, pat, AzureJsonContext.Default.RawPullRequest, cancellationToken)
            .ConfigureAwait(false);

        var projectName = raw.Repository.Project?.Name ?? project;

        return new AzurePullRequest(
            Map(orgSegment, Encode(projectName), raw), projectName, raw.Repository.Name);
    }

    /// <summary>Opens a pull request.</summary>
    public static async Task<PullRequestSummary> CreatePullRequestAsync(
        HttpClient http, string org, string project, string repoId, string title, string description,
        string sourceBranch, string targetBranch, bool draft, string pat, CancellationToken cancellationToken)
    {
        var orgSegment = OrgSegment(org);
        var projectSegment = Encode(project);

        // repoId raw: BUG-PROV-a.
        var url = $"https://dev.azure.com/{orgSegment}/{projectSegment}/_apis/git/repositories/{repoId}"
            + $"/pullrequests?api-version={ApiVersion}";

        var body = new CreatePullRequestBody(
            $"refs/heads/{sourceBranch}", $"refs/heads/{targetBranch}", title, description, draft);

        var raw = await SendJsonAsync(
            http, HttpMethod.Post, url, pat, body, AzureJsonContext.Default.CreatePullRequestBody,
            AzureJsonContext.Default.RawPullRequest, cancellationToken).ConfigureAwait(false);

        return Map(orgSegment, projectSegment, raw);
    }

    /// <summary>
    /// The signed-in user's own decision on a pull request.
    /// </summary>
    /// <remarks>
    /// Two requests: the identity first, then the pull request — the single-PR read is the only one that
    /// includes <c>reviewers</c>. Azure's five-point vote collapses to three strings, which loses the
    /// distinction between approving outright and approving with suggestions, and between rejecting and
    /// waiting for the author. That collapse is 1.7.2's.
    /// </remarks>
    public static async Task<string> ViewerDecisionAsync(
        HttpClient http, string org, string project, string repoId, long prId, string pat,
        CancellationToken cancellationToken)
    {
        var userId = await AuthenticatedUserIdAsync(http, org, pat, cancellationToken).ConfigureAwait(false);

        // repoId encoded here, like GetPullRequestAsync: BUG-PROV-a.
        var url = $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
            + $"/_apis/git/repositories/{Encode(repoId)}/pullRequests/{prId}?api-version={ApiVersion}";

        var pr = await GetAsync(http, url, pat, AzureJsonContext.Default.RawPullRequest, cancellationToken)
            .ConfigureAwait(false);

        var vote = pr.Reviewers?
            .FirstOrDefault(r => string.Equals(r.Id, userId, StringComparison.OrdinalIgnoreCase))?.Vote ?? 0;

        return vote switch
        {
            > 0 => "approved",
            < 0 => "changes_requested",
            _ => "none",
        };
    }

    /// <summary>
    /// Casts the signed-in user's vote, adding them as a reviewer if they were not one.
    /// </summary>
    /// <remarks>
    /// Azure's equivalent of submitting a review, and it carries no message: a vote is a number. Whatever
    /// comment the caller holds has nowhere to go here, which is why the app's blank-comment default is
    /// GitHub-only.
    /// </remarks>
    public static async Task SetReviewerVoteAsync(
        HttpClient http, string org, string project, string repoId, long prId, int vote, string pat,
        CancellationToken cancellationToken)
    {
        var userId = await AuthenticatedUserIdAsync(http, org, pat, cancellationToken).ConfigureAwait(false);

        // repoId raw: BUG-PROV-a.
        var url = $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
            + $"/_apis/git/repositories/{repoId}/pullRequests/{prId}/reviewers/{userId}?api-version={ApiVersion}";

        await SendJsonAsync(
            http, HttpMethod.Put, url, pat, new VoteBody(vote), AzureJsonContext.Default.VoteBody,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Abandons a pull request — Azure's close-without-merge.</summary>
    public static async Task AbandonPullRequestAsync(
        HttpClient http, string org, string project, string repoId, long prId, string pat,
        CancellationToken cancellationToken)
    {
        // repoId raw: BUG-PROV-a.
        var url = $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
            + $"/_apis/git/repositories/{repoId}/pullRequests/{prId}?api-version={ApiVersion}";

        await SendJsonAsync(
            http, HttpMethod.Patch, url, pat, new StatusBody("abandoned"), AzureJsonContext.Default.StatusBody,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a comment thread anchored to a file and a line range, and returns its thread id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is what a re-review replies to and marks fixed, so a finding keeps one thread for the
    /// pull request's whole life instead of a duplicate each time.
    /// </para>
    /// <para>
    /// The path is sent <b>with</b> a leading slash, added when missing — the opposite of GitHub,
    /// which strips one. Both are 1.7.2's.
    /// </para>
    /// <para>
    /// The iteration is read here, at post time, so the comment lands on the pull request's
    /// <em>current</em> push while carrying line numbers computed from the diff the review analysed —
    /// see <c>BUG-REVIEW-a</c>. <c>UNVERIFIED</c>: this write has never run against a real API, per
    /// <c>docs/business-rules/90-ambiguities.md</c>.
    /// </para>
    /// </remarks>
    public static async Task<long> PostCommentAnchoredAsync(
        HttpClient http, string org, string project, string repoId, long prId,
        string content, string filePath, long startLine, long endLine, string pat,
        CancellationToken cancellationToken)
    {
        var iterationId = await GetLatestIterationIdAsync(http, org, project, repoId, prId, pat, cancellationToken)
            .ConfigureAwait(false);

        var body = new ThreadBody(
            [new ThreadComment(ParentCommentId: 0, content, CommentType: 1)],
            Status: 1,
            new ThreadAnchor(
                filePath.StartsWith('/') ? filePath : $"/{filePath}",
                new FilePosition(startLine, Offset: 1),
                // Guards an inverted range; a single line makes both ends the same.
                new FilePosition(Math.Max(endLine, startLine), Offset: 1)),
            new ThreadIteration(new IterationWindow(FirstComparingIteration: 1, iterationId)));

        return await PostThreadAsync(http, ThreadsUrl(org, project, repoId, prId), pat, body, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Opens a pull-request-level comment thread, anchored to nothing.</summary>
    /// <remarks>
    /// Used for the summary and as the fallback for a finding whose location could not be parsed. The
    /// same endpoint as the anchored one, minus both context blocks. <c>UNVERIFIED</c>.
    /// </remarks>
    public static Task<long> PostCommentAsync(
        HttpClient http, string org, string project, string repoId, long prId, string content, string pat,
        CancellationToken cancellationToken) =>
        PostThreadAsync(
            http,
            ThreadsUrl(org, project, repoId, prId),
            pat,
            new ThreadBody(
                [new ThreadComment(ParentCommentId: 0, content, CommentType: 1)],
                Status: 1,
                ThreadContext: null,
                PullRequestThreadContext: null),
            cancellationToken);

    /// <summary>Adds a follow-up comment to an existing thread.</summary>
    /// <remarks>
    /// <c>parentCommentId</c> is the literal <c>1</c>, not the thread's actual root comment: the
    /// reference assumes Azure numbers the first comment of a thread this application opened as
    /// <c>1</c> within that thread, and never re-derives it. Reproduced. <c>UNVERIFIED</c>.
    /// </remarks>
    public static Task ReplyThreadAsync(
        HttpClient http, string org, string project, string repoId, long prId, long threadId,
        string content, string pat, CancellationToken cancellationToken) =>
        SendJsonAsync(
            http,
            HttpMethod.Post,
            $"{ThreadsUrl(org, project, repoId, prId, query: null)}/{threadId}/comments?api-version={ApiVersion}",
            pat,
            new ThreadComment(ParentCommentId: 1, content, CommentType: 1),
            AzureJsonContext.Default.ThreadComment,
            cancellationToken);

    /// <summary>Sets a thread's status — <c>2</c> is <c>fixed</c>, which is how a resolved finding closes.</summary>
    /// <remarks>
    /// Nothing checks that the value is one of Azure's six; 1.7.2 does not either.
    /// <c>UNVERIFIED</c>.
    /// </remarks>
    public static Task SetThreadStatusAsync(
        HttpClient http, string org, string project, string repoId, long prId, long threadId, int status,
        string pat, CancellationToken cancellationToken) =>
        SendJsonAsync(
            http,
            HttpMethod.Patch,
            $"{ThreadsUrl(org, project, repoId, prId, query: null)}/{threadId}?api-version={ApiVersion}",
            pat,
            new ThreadStatusBody(status),
            AzureJsonContext.Default.ThreadStatusBody,
            cancellationToken);

    /// <summary>
    /// A pull request's still-open comment threads, so a human reviewer's feedback shows up beside the
    /// AI's own findings.
    /// </summary>
    /// <remarks>
    /// Threads Azure's own UI treats as done — fixed, won't-fix, closed, by-design — are dropped, and a
    /// thread with no status counts as open. Within a kept thread only real reviewer text survives: Azure
    /// files vote changes and iteration notices as comments too. A thread left with nothing is dropped.
    /// The anchor is passed through untouched, including its leading slash — the write path adds one, the
    /// read path does not remove it.
    /// </remarks>
    public static async Task<IReadOnlyList<PrCommentThread>> ListCommentThreadsAsync(
        HttpClient http, string org, string project, string repoId, long prId, string pat,
        CancellationToken cancellationToken)
    {
        // repoId raw: BUG-PROV-a.
        var url = $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
            + $"/_apis/git/repositories/{repoId}/pullRequests/{prId}/threads?api-version={ApiVersion}";

        var response = await GetAsync(http, url, pat, AzureJsonContext.Default.AzureListRawThread, cancellationToken)
            .ConfigureAwait(false);

        var threads = new List<PrCommentThread>();
        foreach (var thread in response.Value.Where(Open))
        {
            var comments = Readable(thread);
            if (comments.Count == 0)
            {
                continue;
            }

            threads.Add(new PrCommentThread(
                thread.Id,
                thread.ThreadContext?.FilePath,
                thread.ThreadContext?.RightFileStart?.Line,
                thread.ThreadContext?.RightFileEnd?.Line,
                comments));
        }

        return threads;
    }

    /// <summary>An absent status counts as open, in the same bucket as active and pending.</summary>
    private static bool Open(RawThread thread) =>
        thread.Status is null
        || thread.Status.ToLowerInvariant() is "active" or "pending";

    /// <summary>The comments a person actually wrote, trimmed, with the empty ones gone.</summary>
    private static List<PrThreadComment> Readable(RawThread thread)
    {
        var comments = new List<PrThreadComment>();
        foreach (var comment in thread.Comments ?? [])
        {
            // Exact match, and "text" when the field is absent — 1.7.2 compares bytes.
            if ((comment.CommentType ?? "text") != "text")
            {
                continue;
            }

            var content = comment.Content?.Trim();
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            comments.Add(new PrThreadComment(comment.Author.DisplayName, content, comment.PublishedDate));
        }

        return comments;
    }

    // ---------- the diff ----------

    /// <summary>
    /// The id of a pull request's latest iteration.
    /// </summary>
    /// <remarks>
    /// Falls back to <c>1</c> on an empty list rather than failing: per the source's own comment, that
    /// "shouldn't happen for a real PR, but a comment landing on iteration 1 beats the whole review
    /// failing to post".
    /// </remarks>
    public static async Task<long> GetLatestIterationIdAsync(
        HttpClient http, string org, string project, string repoId, long prId, string pat,
        CancellationToken cancellationToken)
    {
        // repoId raw: BUG-PROV-a — and note this is called by PullRequestDiffAsync, which encodes it for
        // its own URLs. One operation, both conventions. Reproduced, not unified.
        var url = $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
            + $"/_apis/git/repositories/{repoId}/pullRequests/{prId}/iterations?api-version={ApiVersion}";

        var response = await GetAsync(
            http, url, pat, AzureJsonContext.Default.AzureListRawIteration, cancellationToken)
            .ConfigureAwait(false);

        return response.Value.Count > 0 ? response.Value[^1].Id : 1;
    }

    /// <summary>
    /// Reads one blob's raw bytes.
    /// </summary>
    /// <remarks>
    /// The only error string in this client that drops the response body, which is 1.7.2's — a
    /// blob endpoint's body is file content, not a diagnostic. Takes segments already encoded, because its
    /// only caller has them.
    /// </remarks>
    private static async Task<byte[]> GetBlobAsync(
        HttpClient http, string orgSegment, string projectSegment, string repoSegment, string sha, string pat,
        CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{orgSegment}/{projectSegment}/_apis/git/repositories/{repoSegment}"
            + $"/blobs/{sha}?api-version={ApiVersion}";

        using var request = Request(HttpMethod.Get, url, pat);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureException(
                $"Azure DevOps returned {StatusText.Of(response.StatusCode)} reading a file");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A pull request's full diff, assembled here because Azure has no endpoint that returns one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every changed file's two blobs are fetched and rendered locally. That is what makes reviewing a
    /// pull request from a pasted link possible with no clone: GitHub can hand over a diff as text, Azure
    /// cannot.
    /// </para>
    /// <para>
    /// The change list is read without <c>$compareTo</c>, so it measures against the base of the whole
    /// pull request rather than the last push.
    /// </para>
    /// </remarks>
    public static async Task<string> PullRequestDiffAsync(
        HttpClient http, string org, string project, string repoId, long prId, string pat,
        CancellationToken cancellationToken)
    {
        var iterationId = await GetLatestIterationIdAsync(http, org, project, repoId, prId, pat, cancellationToken)
            .ConfigureAwait(false);

        var orgSegment = OrgSegment(org);
        var projectSegment = Encode(project);
        var repoSegment = Encode(repoId);

        var url = $"https://dev.azure.com/{orgSegment}/{projectSegment}/_apis/git/repositories/{repoSegment}"
            + $"/pullRequests/{prId}/iterations/{iterationId}/changes"
            + $"?$top={MaxChangedPaths}&api-version={ApiVersion}";

        var changes = await GetAsync(http, url, pat, AzureJsonContext.Default.ChangesResponse, cancellationToken)
            .ConfigureAwait(false);

        var files = Changed(changes);
        var total = files.Count;
        var truncated = total > MaxDiffFiles;

        var sections = await RenderAsync(
            http, orgSegment, projectSegment, repoSegment, [.. files.Take(MaxDiffFiles)], pat, cancellationToken)
            .ConfigureAwait(false);

        var output = new StringBuilder();
        foreach (var section in sections)
        {
            output.Append(section);
            if (output.Length > 0 && output[^1] != '\n')
            {
                output.Append('\n');
            }
        }

        if (string.IsNullOrWhiteSpace(output.ToString()))
        {
            throw new AzureException("This pull request has no file changes to review");
        }

        if (truncated)
        {
            output.Append(CultureInfo.InvariantCulture,
                $"\n(only the first {MaxDiffFiles} of {total} changed files are included)\n");
        }

        return output.ToString();
    }

    /// <summary>One changed file, reduced to what rendering it needs.</summary>
    /// <param name="Change">Azure's lower-cased change type, quoted back in the placeholder lines.</param>
    private readonly record struct ChangedFile(string Path, string Change, string? OldId, string? NewId);

    /// <summary>
    /// The change entries worth rendering.
    /// </summary>
    /// <remarks>
    /// Folders are skipped, and Azure's repo-absolute paths lose their leading slash — a finding has to
    /// cite a repo-relative path, and so should a diff header. A side is dropped when the change type says
    /// so, and also when the id is empty or the all-zero placeholder.
    /// </remarks>
    private static List<ChangedFile> Changed(ChangesResponse changes)
    {
        var files = new List<ChangedFile>();
        foreach (var entry in changes.ChangeEntries ?? [])
        {
            if (entry.Item is not { IsFolder: false, Path: { } rawPath })
            {
                continue;
            }

            var path = rawPath.TrimStart('/');
            if (path.Length == 0)
            {
                continue;
            }

            var change = entry.ChangeType.ToLowerInvariant();
            var oldId = change.Contains("add", StringComparison.Ordinal) ? null : Usable(entry.Item.OriginalObjectId);
            var newId = change.Contains("delete", StringComparison.Ordinal) ? null : Usable(entry.Item.ObjectId);

            files.Add(new ChangedFile(path, change, oldId, newId));
        }

        return files;
    }

    private static string? Usable(string? objectId) =>
        string.IsNullOrEmpty(objectId) || objectId == NullObjectId ? null : objectId;

    /// <summary>
    /// Renders every file, at most <see cref="MaxConcurrentRenders"/> at a time and in list order.
    /// </summary>
    /// <remarks>
    /// The concurrency is per file, not per blob: a single file reads its old side and then its new side,
    /// in sequence. Ordering is preserved because the diff is read by a person and by a model, and a diff
    /// whose files arrive in fetch-completion order would differ run to run for no reason.
    /// </remarks>
    private static async Task<string[]> RenderAsync(
        HttpClient http, string orgSegment, string projectSegment, string repoSegment,
        ChangedFile[] files, string pat, CancellationToken cancellationToken)
    {
        var sections = new string[files.Length];
        using var slots = new SemaphoreSlim(MaxConcurrentRenders);

        await Task.WhenAll(files.Select(async (file, index) =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                sections[index] = await RenderOneAsync(
                    http, orgSegment, projectSegment, repoSegment, file, pat, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);

        return sections;
    }

    /// <summary>
    /// One file's section of the diff, or the placeholder that stands in for it.
    /// </summary>
    /// <remarks>
    /// The size check runs <em>after</em> both blobs are fetched, not before — 1.7.2 has no way to
    /// know a blob's size without reading it, so an oversized file is downloaded and then discarded. That
    /// is a real cost, and reproducing it keeps the request count honest.
    /// </remarks>
    private static async Task<string> RenderOneAsync(
        HttpClient http, string orgSegment, string projectSegment, string repoSegment,
        ChangedFile file, string pat, CancellationToken cancellationToken)
    {
        byte[] before;
        byte[] after;
        try
        {
            before = await SideAsync(http, orgSegment, projectSegment, repoSegment, file.OldId, pat, cancellationToken)
                .ConfigureAwait(false);
            after = await SideAsync(http, orgSegment, projectSegment, repoSegment, file.NewId, pat, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AzureException)
        {
            // One unreadable file does not fail the review: it is listed as unreadable and the rest stands.
            return $"diff --git a/{file.Path} b/{file.Path}\n(couldn't read this file from Azure DevOps)\n";
        }

        if (before.Length > MaxBlobBytes || after.Length > MaxBlobBytes)
        {
            return $"diff --git a/{file.Path} b/{file.Path}\n({file.Change}, too large to display)\n";
        }

        return UnifiedPatch.Render(file.Path, before, after)
            ?? $"diff --git a/{file.Path} b/{file.Path}\n({file.Change}, binary)\n";
    }

    /// <summary>A side's bytes, or none at all when this side has no blob.</summary>
    private static Task<byte[]> SideAsync(
        HttpClient http, string orgSegment, string projectSegment, string repoSegment, string? objectId, string pat,
        CancellationToken cancellationToken) =>
        objectId is null
            ? Task.FromResult(Array.Empty<byte>())
            : GetBlobAsync(http, orgSegment, projectSegment, repoSegment, objectId, pat, cancellationToken);

    // ---------- identity ----------

    /// <summary>
    /// The signed-in user's Azure DevOps GUID.
    /// </summary>
    /// <remarks>
    /// Organisation-scoped, not repository-scoped, and the one endpoint on the preview contract. Needed
    /// because an Azure vote is keyed by reviewer id, where a GitHub review is attributed to whoever's
    /// token submitted it.
    /// </remarks>
    public static async Task<string> AuthenticatedUserIdAsync(
        HttpClient http, string org, string pat, CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{OrgSegment(org)}/_apis/connectionData?api-version={PreviewApiVersion}";

        var data = await GetAsync(http, url, pat, AzureJsonContext.Default.ConnectionData, cancellationToken)
            .ConfigureAwait(false);

        return data.AuthenticatedUser.Id;
    }

    // ---------- mapping ----------

    /// <summary>
    /// Maps one raw pull request onto the shared summary shape.
    /// </summary>
    /// <remarks>
    /// The browser URL is <em>synthesised</em>, not read from the response — Azure returns an API URL, not
    /// a page a person can open. It is built from the already-encoded organisation and project segments the
    /// caller passed, plus the repository's own canonical name.
    /// </remarks>
    private static PullRequestSummary Map(string orgSegment, string projectSegment, RawPullRequest pr) =>
        new(
            pr.PullRequestId,
            pr.Title,
            pr.Description,
            Bucket(pr.Status, pr.IsDraft),
            StripRef(pr.SourceRefName),
            StripRef(pr.TargetRefName),
            pr.CreatedBy.DisplayName,
            pr.CreationDate,
            $"https://dev.azure.com/{orgSegment}/{projectSegment}/_git/{Encode(pr.Repository.Name)}"
                + $"/pullrequest/{pr.PullRequestId}",
            "azure");

    /// <summary>
    /// Azure's status vocabulary collapsed into the four buckets the sidebar groups by.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing: a completed or abandoned pull request buckets by that, so an abandoned
    /// draft is <c>closed</c> rather than <c>draft</c>.
    /// </remarks>
    private static string Bucket(string status, bool isDraft) => status switch
    {
        "completed" => "merged",
        "abandoned" => "closed",
        _ when isDraft => "draft",
        _ => "open",
    };

    /// <summary>A branch as a person writes it. Azure reports and requires the full ref.</summary>
    private static string StripRef(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    // ---------- path segments ----------

    /// <summary>The organisation, normalised then encoded — the form every URL here needs.</summary>
    /// <remarks>
    /// <c>internal</c> so the work-item client builds its URLs the same way. Normalisation is not
    /// cosmetic: <see cref="NormalizeOrg"/> reduces a saved <c>https://dev.azure.com/acme</c> or
    /// <c>acme.visualstudio.com</c> to <c>acme</c>, and Azure's server rejects a literal <c>:</c>
    /// anywhere in a request path.
    /// </remarks>
    internal static string OrgSegment(string org) => Encode(NormalizeOrg(org));

    /// <summary>
    /// Reduces whatever the user saved as their "organisation" to the bare name.
    /// </summary>
    /// <remarks>
    /// Accepts a bare name, a full <c>https://dev.azure.com/{org}</c> URL, or the legacy
    /// <c>https://{org}.visualstudio.com</c> form. It exists because Azure's server rejects any literal
    /// <c>:</c> in a request path (IIS request validation), so a raw URL interpolated into a path segment
    /// would fail as a confusing 400 or 404 rather than as "that is not an organisation name".
    /// </remarks>
    internal static string NormalizeOrg(string org)
    {
        var trimmed = org.Trim().TrimEnd('/');

        foreach (var prefix in (string[])["https://dev.azure.com/", "http://dev.azure.com/"])
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = trimmed[prefix.Length..].TrimEnd('/');
                var first = rest.Split('/')[0];
                return first.Length > 0 ? first : rest;
            }
        }

        var withoutScheme = Scheme(trimmed);
        if (withoutScheme is not null)
        {
            var host = withoutScheme.Split('/')[0];
            if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
            {
                return host[..^".visualstudio.com".Length];
            }
        }

        return trimmed;
    }

    private static string? Scheme(string value) =>
        value.StartsWith("https://", StringComparison.Ordinal) ? value["https://".Length..]
        : value.StartsWith("http://", StringComparison.Ordinal) ? value["http://".Length..]
        : null;

    /// <summary>
    /// Percent-encodes one path segment, byte by byte.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than delegating to <c>Uri.EscapeDataString</c>, because the unreserved set has to
    /// be exactly 1.7.2's — <c>A-Za-z0-9-._~</c> — and the escapes upper-case hex. Applied to the
    /// organisation and the project at every call site, and to the repository at only three of them, which
    /// is <c>BUG-PROV-a</c>.
    /// </remarks>
    internal static string Encode(string segment)
    {
        var output = new StringBuilder(segment.Length);
        foreach (var b in Encoding.UTF8.GetBytes(segment))
        {
            if (b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                output.Append((char)b);
            }
            else
            {
                output.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return output.ToString();
    }

    // ---------- transport ----------

    /// <summary>
    /// Builds a request with the one header Azure needs.
    /// </summary>
    /// <remarks>
    /// Basic auth with an empty user name and the PAT as the password. Unlike the GitHub client there is no
    /// <c>Accept</c>, no <c>User-Agent</c> and no API-version header: Azure takes its version in the query
    /// string, and its server does not reject an agent-less request the way GitHub's does.
    /// </remarks>
    /// <summary>The threads collection for a pull request.</summary>
    /// <remarks>
    /// <paramref name="repoId"/> is interpolated <b>raw</b>, unencoded, while the organisation and the
    /// project are encoded — <c>BUG-PROV-a</c>, the same split <see cref="GetLatestIterationIdAsync"/>
    /// already reproduces. Reproduced, not unified.
    /// </remarks>
    /// <param name="query">
    /// Appended as-is. Null returns the bare collection URL, which the per-thread endpoints extend with
    /// an id before adding their own query.
    /// </param>
    private static string ThreadsUrl(
        string org, string project, string repoId, long prId, string? query = "?api-version=" + ApiVersion) =>
        $"https://dev.azure.com/{OrgSegment(org)}/{Encode(project)}"
        + $"/_apis/git/repositories/{repoId}/pullRequests/{prId}/threads{query}";

    /// <summary>Creates a thread and answers its id.</summary>
    /// <remarks>
    /// Its own helper rather than <c>SendJsonAsync</c> for one reason: a body that will not parse
    /// reports <c>"couldn't read Azure DevOps response"</c> here, where every other endpoint in this
    /// client says <c>"unexpected response from Azure DevOps"</c>. CodeFlow 1.7.2 has two wordings, so
    /// this port has two.
    /// </remarks>
    private static async Task<long> PostThreadAsync(
        HttpClient http, string url, string pat, ThreadBody body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, url, pat);
        request.Content = JsonContent.Create(body, AzureJsonContext.Default.ThreadBody);

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        try
        {
            var created = await response.Content
                .ReadFromJsonAsync(AzureJsonContext.Default.ThreadCreated, cancellationToken)
                .ConfigureAwait(false);

            return (created ?? throw new JsonException("the response body was JSON null")).Id;
        }
        catch (Exception failure) when (failure is JsonException or NotSupportedException)
        {
            throw new AzureException($"couldn't read Azure DevOps response: {StatusText.Reason(failure)}");
        }
    }

    /// <summary>
    /// Any URL on this host, read as raw bytes, with this file's auth and transport handling.
    /// </summary>
    /// <remarks>
    /// One <c>internal</c> member instead of promoting <c>Request</c>, <c>SendAsync</c> and
    /// <c>EnsureSuccessAsync</c> separately: <see cref="AzureWorkItemClient"/> needs exactly this —
    /// a work-item attachment is bytes behind the same Basic auth — and everything else about how
    /// the request is built stays private to this file.
    /// <para>
    /// The failure text follows <c>GetBlobAsync</c>'s shape and drops the response body: an
    /// attachment that fails to download answers with the file's own bytes or an error page, and
    /// interpolating either into a message helps nobody.
    /// </para>
    /// </remarks>
    internal static async Task<byte[]> GetBytesAsync(
        HttpClient http, string url, string pat, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, pat);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureException(
                $"Azure DevOps returned {StatusText.Of(response.StatusCode)} reading a file",
                response.StatusCode
                    is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string pat)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}")));

        return request;
    }

    /// <summary>
    /// A GET returning a deserialised resource, with this file's error mapping.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so <see cref="AzureWorkItemClient"/> reaches Azure through the same transport.
    /// Sharing it is the point: <c>DIVERGENCE-PROV-b</c>'s 401/403 marking lives in
    /// <see cref="EnsureSuccessAsync"/>, and a second client with its own <c>SendAsync</c> would map
    /// a refused credential to a generic failure without anything failing to compile.
    /// </remarks>
    internal static async Task<T> GetAsync<T>(
        HttpClient http, string url, string pat, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, pat);
        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request with a JSON body and ignores whatever came back.</summary>
    private static async Task SendJsonAsync<TBody>(
        HttpClient http, HttpMethod method, string url, string pat,
        TBody body, JsonTypeInfo<TBody> bodyType, CancellationToken cancellationToken)
    {
        using var request = Request(method, url, pat);
        request.Content = JsonContent.Create(body, bodyType);

        using var response = await SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request with a JSON body and deserialises the resource it returned.</summary>
    /// <remarks><c>internal</c> for the same reason as <see cref="GetAsync{T}"/>: WIQL and the
    /// work-item batch read are POSTs, and they must share this error mapping.</remarks>
    internal static async Task<TResult> SendJsonAsync<TBody, TResult>(
        HttpClient http, HttpMethod method, string url, string pat,
        TBody body, JsonTypeInfo<TBody> bodyType, JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
    {
        using var request = Request(method, url, pat);
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
            throw new AzureException($"couldn't reach Azure DevOps: {StatusText.Reason(failure)}");
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
            throw new AzureException($"unexpected response from Azure DevOps: {StatusText.Reason(failure)}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        // DIVERGENCE-PROV-b, and the whole of it. The message is byte-for-byte what it always was —
        // every caller that only reads `.Message` is unaffected — and the only thing added is a flag
        // saying the credential was refused rather than the request. Azure answers 401 for a PAT it
        // cannot read and 403 for one whose scopes fall short; both mean "fix the token", which is a
        // different sentence and a different button from "that repository is not there".
        // The message is byte-for-byte what it always was. The prefix is *not* applied here: it is
        // for one consumer — the PR list, which only ever sees a string — and every other caller
        // renders this message to a human. A sentinel in the review-posting failure summary would be
        // machine punctuation in the middle of a sentence the user reads.
        var unauthorized = response.StatusCode
            is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;

        throw new AzureException(
            $"Azure DevOps returned {StatusText.Of(response.StatusCode)}: {Readable(body)}", unauthorized);
    }

    /// <summary>
    /// The response body, or a sentence when the body is a web page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>DIVERGENCE-PROV-c</c>.</b> Azure answers an unknown organisation, and an unauthenticated
    /// request, with its sign-in <em>page</em> — a full HTML document. Interpolating it whole, which
    /// is what 1.7.2 did and what this client did until now, makes the error message tens of
    /// kilobytes of markup and a base64 logo. Observed for real: a 404 for a mistyped organisation
    /// rendered as an entire HTML document where an error toast should be.
    /// </para>
    /// <para>
    /// The status code stays exactly as it was, so nothing that parses the prefix changes. Only the
    /// body is replaced, and only when it is unmistakably a page rather than an API error — a JSON
    /// error from the API is what actually explains a failure and is never touched.
    /// </para>
    /// </remarks>
    private static string Readable(string body)
    {
        var start = body.AsSpan().TrimStart();
        var isPage = start.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);

        return isPage
            ? "the server answered with a sign-in page instead of the API. Check that the "
              + "organisation name is right and that its token has not expired."
            : body;
    }

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

/// <summary>
/// A pull request plus the canonical names Azure reports for its project and repository.
/// </summary>
/// <remarks>
/// The names exist because a link can address a pull request by GUID, and a GUID matches no git remote.
/// Everything downstream — matching a local clone, addressing the blobs endpoint — uses these rather than
/// what the link carried.
/// </remarks>
public sealed record AzurePullRequest(PullRequestSummary Summary, string ProjectName, string RepoName);
