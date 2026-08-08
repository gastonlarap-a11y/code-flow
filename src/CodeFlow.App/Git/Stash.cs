using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>One stash entry: its position in the stack, its message, and its commit.</summary>
public sealed record StashInfo(int Index, string Message, string Oid);

/// <summary>
/// The stash stack (GIT-014, GIT-015).
/// </summary>
public static class Stash
{
    /// <summary>Every stash, most recent first — index 0 is <c>stash@{0}</c>.</summary>
    /// <remarks>
    /// Read from the <c>refs/stash</c> reflog, not from <see cref="LibGit2Sharp.Stash"/>. The two
    /// carry different strings: <c>Stash.Message</c> is the stash <i>commit</i>'s message, while
    /// 1.7.2's <c>stash_foreach</c> hands out the <i>reflog</i> message — and the reflog
    /// message is the one <see cref="Rename"/> rewrites, so reading the commit would report a
    /// rename as having done nothing.
    /// </remarks>
    public static IReadOnlyList<StashInfo> List(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        return repo.Refs.Log("refs/stash")
            .Select((entry, index) => new StashInfo(index, entry.Message, entry.To.Sha))
            .ToList();
    }

    /// <summary>
    /// Stashes the working tree and the index together, defaulting the message to <c>WIP</c>.
    /// </summary>
    /// <remarks>
    /// No keep-index option is exposed: this is plain <c>git stash push</c>, never
    /// <c>--keep-index</c>. The frontend also calls this on its own, as the recovery path when a
    /// checkout is blocked (GIT-003).
    /// </remarks>
    public static void Save(string repoPath, string? message, bool includeUntracked)
    {
        using var repo = RepoStatus.Open(repoPath);

        var stasher = repo.Config.BuildSignature(DateTimeOffset.Now)
            ?? throw new InvalidOperationException(
                "config value 'user.name' was not found - please set user.name and user.email");

        var options = includeUntracked ? StashModifiers.Default | StashModifiers.IncludeUntracked : StashModifiers.Default;

        repo.Stashes.Add(stasher, message ?? "WIP", options);
    }

    /// <summary>Applies a stash without removing it, and says how that went (GIT-015).</summary>
    /// <remarks>
    /// <b>The outcome is a return value, not an exception.</b> LibGit2Sharp's
    /// <see cref="StashCollection.Apply(int)"/> hands back a <see cref="StashApplyStatus"/> and
    /// throws nothing when the stash collides with the working tree — so discarding it, as this did
    /// until now, reported a conflicted apply to the UI as a success and left the user with
    /// conflict markers on disk and no idea why.
    /// <para>
    /// Deliberately not tagged with <see cref="Branches.CheckoutConflictPrefix"/>: that prefix marks
    /// an <em>error</em> the frontend parses, and a conflict here is an outcome to act on, the same
    /// shape <c>MergeOutcome.Status</c> already uses for the merge that conflicts.
    /// </para>
    /// </remarks>
    public static string Apply(string repoPath, int index)
    {
        using var repo = RepoStatus.Open(repoPath);
        return Outcome(repo, repo.Stashes.Apply(index));
    }

    /// <summary>Applies a stash and removes it only if that left no conflicts (GIT-015).</summary>
    /// <remarks>
    /// <b>Not <see cref="StashCollection.Pop(int)"/>.</b> That one drops the entry even when the
    /// apply conflicted — verified against a real repository: the working tree ends up full of
    /// markers and the stash is gone, so the one copy of that work only exists half-merged on disk.
    /// `git stash pop` itself keeps the entry in that case, and so does this: apply, then drop only
    /// on a clean result. It is what makes "carry my changes to the other branch" recoverable.
    /// </remarks>
    public static string Pop(string repoPath, int index)
    {
        using var repo = RepoStatus.Open(repoPath);

        var outcome = Outcome(repo, repo.Stashes.Apply(index));
        if (outcome == "applied")
        {
            repo.Stashes.Remove(index);
        }

        return outcome;
    }

    /// <summary>
    /// The wire value for a stash application's outcome.
    /// </summary>
    /// <remarks>
    /// The index is what settles it. LibGit2Sharp reports <see cref="StashApplyStatus.Applied"/>
    /// for an apply that wrote conflict markers and left conflicted entries behind — its
    /// <see cref="StashApplyStatus.Conflicts"/> covers the case where the merge could not even be
    /// attempted — so trusting the enum alone would call that a success, which is the bug this
    /// whole path exists to close.
    /// </remarks>
    private static string Outcome(Repository repo, StashApplyStatus status) => status switch
    {
        StashApplyStatus.Applied => repo.Index.Conflicts.Any() ? "conflicts" : "applied",
        StashApplyStatus.Conflicts => "conflicts",
        StashApplyStatus.NotFound => "not_found",
        StashApplyStatus.UncommittedChanges => "uncommitted_changes",
        _ => "unknown",
    };

    /// <summary>Removes a stash without applying it.</summary>
    public static void Drop(string repoPath, int index)
    {
        using var repo = RepoStatus.Open(repoPath);
        repo.Stashes.Remove(index);
    }

    /// <summary>
    /// Renames a stash by dropping it and re-appending its own commit under a new message
    /// (GIT-014, DIVERGENCE-GIT-a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Git has no rename-a-stash operation: the message lives in the <c>refs/stash</c> reflog, and
    /// reflog entries cannot be edited in place. So the entry's commit is captured, the entry is
    /// dropped through the same path the Drop button uses, and <c>refs/stash</c> is re-pointed at
    /// that same commit with the new message — which writes exactly one fresh reflog entry. The
    /// reference tried splicing the reflog by hand first and it left a stray duplicate behind.
    /// </para>
    /// <para>
    /// <b>The renamed stash therefore becomes <c>stash@{0}</c>, whichever slot it was in</b>, and
    /// everything above it shifts down. Nothing is lost or duplicated, and the reordering is a
    /// deliberate, preserved side effect — the same trade-off
    /// <c>git stash pop &amp;&amp; git stash push -m</c> has. The message is used verbatim, with
    /// no <c>"On &lt;branch&gt;: "</c> prefix, unlike one the git CLI writes.
    /// </para>
    /// </remarks>
    public static void Rename(string repoPath, int index, string newMessage)
    {
        using var repo = RepoStatus.Open(repoPath);

        var entry = repo.Refs.Log("refs/stash").ElementAtOrDefault(index)
            ?? throw new InvalidOperationException("Stash not found");

        var oid = entry.To;

        repo.Stashes.Remove(index);
        repo.Refs.Add("refs/stash", oid, newMessage, allowOverwrite: true);
    }
}
