using CodeFlow.Activity;

namespace CodeFlow.Providers;

/// <summary>
/// One pull request, in the shape the renderer consumes.
/// </summary>
/// <remarks>
/// <para>
/// Provider-neutral: both GitHub and Azure DevOps map their own payloads into this. These are the
/// shapes the frontend already consumes, so a second provider produces the exact same ones rather
/// than a parallel set the UI would have to learn. The type therefore lives in the neutral folder
/// and neither provider owns it.
/// </para>
/// <para>
/// <paramref name="Status"/> is one of <c>open</c>, <c>draft</c>, <c>merged</c>, <c>closed</c> — a
/// four-bucket collapse the sidebar groups by. GitHub reports open/closed plus separate draft and
/// merged flags; Azure reports its own vocabulary. Both fold into these four.
/// </para>
/// </remarks>
/// <param name="Id">The PR number on GitHub, the PR id on Azure.</param>
/// <param name="Provider"><c>github</c> or <c>azure</c> — what actually answered, not what was configured.</param>
public sealed record PullRequestSummary(
    long Id,
    string Title,
    string Description,
    string Status,
    string SourceBranch,
    string TargetBranch,
    string Author,
    string CreatedAt,
    string Url,
    string Provider);

/// <summary>One comment inside a pull-request thread.</summary>
public sealed record PrThreadComment(string Author, string Content, string PublishedDate);

/// <summary>
/// One Azure DevOps team project, for the manual link dialog's first dropdown.
/// </summary>
/// <remarks>
/// Azure's own JSON already spells these two fields the way the renderer reads them, so the same record
/// is deserialised from the API and serialised back out — the only place in this feature where an
/// inbound and an outbound shape are the same type.
/// </remarks>
public sealed record AdoProject(string Id, string Name);

/// <summary>
/// One Azure DevOps repository, for the manual link dialog's second dropdown.
/// </summary>
/// <remarks>
/// Azure's Git REST API accepts either the GUID or the plain name wherever it takes a repository, so
/// whichever of the two is stored in <c>ado_repo_id</c> is callable. The dialog stores the id; a link
/// resolved from a pasted URL stores the name.
/// </remarks>
public sealed record AdoRepo(string Id, string Name);

/// <summary>
/// One thread of comments on a pull request.
/// </summary>
/// <remarks>
/// A thread anchored to a file carries its path and line range; a plain conversation comment
/// carries none of the three, which is why all of them are nullable rather than defaulted.
/// </remarks>
public sealed record PrCommentThread(
    long Id,
    string? FilePath,
    long? StartLine,
    long? EndLine,
    IReadOnlyList<PrThreadComment> Comments);

/// <summary>
/// The result of approving, requesting changes on, or closing a pull request.
/// </summary>
/// <remarks>
/// The refreshed pull request plus the history row the action filed, so the UI can update the PR
/// and append to the activity list from one reply instead of re-fetching both.
/// </remarks>
public sealed record PrActionOutcome(PullRequestSummary Pr, JobHistoryEntry Activity);

/// <summary>A drafted pull-request description, split into its title and its markdown body.</summary>
/// <remarks>
/// The model is asked for a single text whose first line is <c>TITLE: …</c>; the split happens in
/// the command layer, so what crosses the boundary is already two fields the form can bind to.
/// </remarks>
public sealed record PrDescriptionDraft(string Title, string Body);
