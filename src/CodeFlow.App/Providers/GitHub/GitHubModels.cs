using System.Text.Json.Serialization;

namespace CodeFlow.Providers.GitHub;

/// <summary>A GitHub account, of which only the login is ever used.</summary>
internal sealed record RawUser(string Login);

/// <summary>One side of a pull request — its branch and, on the head, the commit to anchor to.</summary>
/// <param name="Ref">
/// Named for the JSON field, which is <c>ref</c> — a C# keyword, hence the explicit property name.
/// It is the only field in this file that needs one.
/// </param>
internal sealed record RawRef(
    [property: JsonPropertyName("ref")] string Ref,
    string Sha = "");

/// <summary>A pull request as GitHub returns it.</summary>
/// <remarks>
/// Every nullable field here is nullable in the API too: <c>body</c> is absent on a PR opened with no
/// description, and <c>merged_at</c> is absent until it merges — which is what distinguishes a merged
/// PR from a closed one.
/// </remarks>
internal sealed record RawPull(
    long Number,
    string Title,
    string State,
    RawRef Head,
    RawRef Base,
    RawUser User,
    string CreatedAt,
    string HtmlUrl,
    string? Body = null,
    bool Draft = false,
    string? MergedAt = null);

/// <summary>One changed file, for the diff the client reassembles when GitHub will not render one.</summary>
/// <param name="Patch">
/// Absent for a binary file, and for one whose diff GitHub decided was too large to inline. Both are
/// listed as a bare header, which is honest: the review is told the file changed but not how.
/// </param>
internal sealed record RawPullFile(
    string Filename,
    string Status,
    string? PreviousFilename = null,
    string? Patch = null);

/// <summary>One submitted review, for working out the signed-in user's own decision.</summary>
internal sealed record RawReview(RawUser User, string State);

/// <summary>One inline review comment, anchored to a file and a line.</summary>
/// <param name="InReplyToId">
/// Set on a reply and absent on the comment that starts a thread, which is the only thing tying a
/// thread together — GitHub exposes no thread id over REST.
/// </param>
internal sealed record RawReviewComment(
    long Id,
    RawUser User,
    string CreatedAt,
    string? Path = null,
    long? Line = null,
    long? StartLine = null,
    string? Body = null,
    long? InReplyToId = null);

/// <summary>One conversation-level comment, which has no file or line at all.</summary>
internal sealed record RawIssueComment(long Id, RawUser User, string CreatedAt, string? Body = null);

/// <summary>The body of <c>POST /pulls</c>.</summary>
internal sealed record CreatePullRequestBody(
    string Title,
    string Head,
    string Base,
    string Body,
    bool Draft);

/// <summary>
/// The body of <c>POST /pulls/{n}/reviews</c>.
/// </summary>
/// <remarks>
/// <see cref="Body"/> is omitted entirely when the caller supplied no comment — not sent as an empty
/// string — so an approval can carry no text. A <c>REQUEST_CHANGES</c> review does require one, which
/// GitHub enforces itself.
/// </remarks>
internal sealed record SubmitReviewBody(
    string Event,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body);

/// <summary>The body of <c>PATCH /pulls/{n}</c> that closes a pull request without merging.</summary>
internal sealed record CloseBody(string State);

/// <summary>What GitHub answers when a comment is created: its id, and nothing else that is used.</summary>
/// <remarks>
/// The id matters for an inline review comment, which a re-review replies to and resolves. An issue
/// comment's id is returned too and then discarded — issue comments are not threaded.
/// </remarks>
internal sealed record CommentCreated(long Id);

/// <summary>
/// The body of <c>POST /pulls/{n}/comments</c> — an inline comment anchored to a file and a line.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Line"/> is the <em>last</em> line of the range, which is where GitHub anchors; the
/// optional <see cref="StartLine"/> marks where the highlight begins.
/// </para>
/// <para>
/// Both optional fields are omitted for a single-line comment rather than sent equal to
/// <see cref="Line"/>: <b>GitHub answers 422 when <c>start_line == line</c></b>, so sending them
/// always would break exactly the common case.
/// </para>
/// </remarks>
internal sealed record AnchoredCommentBody(
    string Body,
    string CommitId,
    string Path,
    long Line,
    string Side,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StartLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartSide);

/// <summary>The body of a plain comment post: an issue comment, or a reply on a review comment.</summary>
internal sealed record CommentBody(string Body);

/// <summary>The body of a GraphQL call — one query or one mutation, already interpolated.</summary>
internal sealed record GraphQlRequest(string Query);
