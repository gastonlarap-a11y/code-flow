using System.Text.Json.Serialization;

namespace CodeFlow.Providers.Azure;

/// <summary>
/// Azure DevOps' list envelope: every collection endpoint wraps its items in <c>value</c>.
/// </summary>
internal sealed record AzureList<T>(IReadOnlyList<T> Value);

/// <summary>An account, of which only the display name reaches the UI.</summary>
internal sealed record RawIdentity(string DisplayName);

/// <summary>A team project as named on a repository reference.</summary>
internal sealed record RawProjectRef(string Name);

/// <summary>
/// A repository reference carried on a pull request.
/// </summary>
/// <param name="Project">
/// Absent on some payloads, which is why the caller falls back to the project it was asked about. It is
/// the only place a pull request reached by a GUID-carrying link can learn its project's readable name.
/// </param>
internal sealed record RawRepoRef(string Name, RawProjectRef? Project = null);

/// <summary>
/// One reviewer's standing on a pull request.
/// </summary>
/// <param name="Vote">
/// Azure's numeric model: <c>10</c> approved, <c>5</c> approved with suggestions, <c>0</c> no vote,
/// <c>-5</c> waiting for the author, <c>-10</c> rejected. Deliberately not unified with GitHub's review
/// events — see <c>DIVERGENCE-PROV-a</c>.
/// </param>
internal sealed record RawReviewer(string Id, int Vote = 0);

/// <summary>
/// A pull request as Azure DevOps returns it.
/// </summary>
/// <param name="Reviewers">
/// Present when a single pull request is fetched and absent from the list endpoint, so it defaults to
/// empty rather than being required — reading a decision needs the single-PR call.
/// </param>
internal sealed record RawPullRequest(
    long PullRequestId,
    string Title,
    string Status,
    string SourceRefName,
    string TargetRefName,
    RawIdentity CreatedBy,
    string CreationDate,
    RawRepoRef Repository,
    string Description = "",
    bool IsDraft = false,
    IReadOnlyList<RawReviewer>? Reviewers = null);

/// <summary>One push into a pull request. Only the last one's id is ever used.</summary>
internal sealed record RawIteration(long Id);

/// <summary>
/// One changed path in an iteration.
/// </summary>
/// <param name="ObjectId">The blob on the target side. <param name="OriginalObjectId">is the base side.</param></param>
internal sealed record RawChangeItem(
    string? Path = null,
    string? ObjectId = null,
    string? OriginalObjectId = null,
    bool IsFolder = false);

/// <summary>One entry in an iteration's change list.</summary>
internal sealed record RawChangeEntry(string ChangeType = "", RawChangeItem? Item = null);

/// <summary>The iteration-changes response, whose items are not under <c>value</c> like everything else.</summary>
internal sealed record ChangesResponse(IReadOnlyList<RawChangeEntry>? ChangeEntries = null);

/// <summary>The signed-in identity, from the one preview endpoint.</summary>
internal sealed record ConnectionData(RawConnectionUser AuthenticatedUser);

/// <summary>The signed-in user's own GUID, which is what Azure keys a reviewer vote by.</summary>
internal sealed record RawConnectionUser(string Id);

/// <summary>
/// One comment inside a thread.
/// </summary>
/// <param name="CommentType">
/// <c>"text"</c> for something a person wrote. Azure files vote changes and iteration notices as
/// comments too, under other types, and those are dropped on read.
/// </param>
internal sealed record RawThreadComment(
    RawIdentity Author,
    string PublishedDate,
    string? Content = null,
    string? CommentType = null);

/// <summary>A position inside a file. Only the line is read; the column is never used.</summary>
internal sealed record RawFilePosition(long Line);

/// <summary>Where in the diff a thread is anchored, absent for a pull-request-level thread.</summary>
internal sealed record RawThreadContext(
    string? FilePath = null,
    RawFilePosition? RightFileStart = null,
    RawFilePosition? RightFileEnd = null);

/// <summary>
/// One comment thread on a pull request.
/// </summary>
/// <param name="Status">
/// Absent on some threads, and an absent status counts as open — the same bucket as
/// <c>active</c> and <c>pending</c>.
/// </param>
internal sealed record RawThread(
    long Id,
    string? Status = null,
    IReadOnlyList<RawThreadComment>? Comments = null,
    RawThreadContext? ThreadContext = null);

/// <summary>
/// The body of <c>POST …/pullrequests</c>.
/// </summary>
/// <remarks>
/// Azure requires the full <c>refs/heads/</c> prefix on both refs, which is the inverse of what it
/// returns. CodeFlow 1.7.2 does not check whether the caller already passed a prefixed branch, so a
/// prefixed name would be doubled; reproduced.
/// </remarks>
internal sealed record CreatePullRequestBody(
    string SourceRefName,
    string TargetRefName,
    string Title,
    string Description,
    bool IsDraft);

/// <summary>The body of <c>PUT …/reviewers/{userId}</c>, which both adds the reviewer and casts the vote.</summary>
internal sealed record VoteBody(int Vote);

/// <summary>The body of <c>PATCH …/pullRequests/{id}</c> that abandons it — Azure's close-without-merge.</summary>
internal sealed record StatusBody(string Status);

/// <summary>What Azure answers when a thread is created: its id, and nothing else that is used.</summary>
/// <remarks>
/// The id is kept on the finding so a later re-review replies to this thread rather than opening a
/// duplicate — the whole point of the review memory.
/// </remarks>
internal sealed record ThreadCreated(long Id);

/// <summary>
/// The body of <c>POST …/pullRequests/{id}/threads</c>, both anchored and plain.
/// </summary>
/// <remarks>
/// The two optional halves are what separate them: a plain conversation comment omits both, and an
/// anchored one carries the file position plus the iteration the comment is measured against.
/// <c>status: 1</c> is Azure's <c>active</c>.
/// </remarks>
internal sealed record ThreadBody(
    IReadOnlyList<ThreadComment> Comments,
    int Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ThreadAnchor? ThreadContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ThreadIteration? PullRequestThreadContext);

/// <summary>
/// One comment inside a thread body, or the whole body of a reply.
/// </summary>
/// <param name="ParentCommentId">
/// <c>0</c> when opening a thread. A reply sends <c>1</c> — 1.7.2 hardcodes it rather than
/// reading the thread's actual root comment, relying on Azure numbering the first comment of a
/// thread this application created as <c>1</c> within that thread.
/// </param>
/// <param name="CommentType"><c>1</c> is Azure's <c>text</c>.</param>
internal sealed record ThreadComment(long ParentCommentId, string Content, int CommentType);

/// <summary>Where an anchored thread attaches, on the right-hand (post-change) side of the diff.</summary>
/// <param name="FilePath">
/// Carries a leading slash, added when missing — the opposite of GitHub, which strips one. Both are
/// 1.7.2's, and the divergence is deliberate.
/// </param>
internal sealed record ThreadAnchor(string FilePath, FilePosition RightFileStart, FilePosition RightFileEnd);

/// <summary>One end of an anchored range. <c>Offset</c> is always <c>1</c> — the start of the line.</summary>
internal sealed record FilePosition(long Line, int Offset);

/// <summary>Which pushes an anchored thread is measured between.</summary>
internal sealed record ThreadIteration(IterationWindow IterationContext);

/// <summary>
/// The iteration window: always from the first push to the latest one.
/// </summary>
/// <remarks>
/// The latest iteration is read at <em>post</em> time, not the one the review analysed — see
/// <c>BUG-REVIEW-a</c>.
/// </remarks>
internal sealed record IterationWindow(long FirstComparingIteration, long SecondComparingIteration);

/// <summary>
/// The body of <c>PATCH …/threads/{id}</c>.
/// </summary>
/// <remarks>
/// Azure's thread statuses: <c>1</c> active, <c>2</c> fixed, <c>3</c> wontFix, <c>4</c> closed,
/// <c>5</c> byDesign, <c>6</c> pending. A resolved finding's thread is marked <c>2</c>. Distinct
/// from <see cref="StatusBody"/>, whose <c>status</c> is a pull request's own and is a string.
/// </remarks>
internal sealed record ThreadStatusBody(int Status);
