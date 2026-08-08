using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>A branch and, for a local one with an upstream, how far it has drifted from it.</summary>
public sealed record BranchInfo(
    string Name,
    bool IsHead,
    bool IsRemote,
    string? Upstream,
    int Ahead,
    int Behind,
    string? Target);

/// <summary>
/// Branch listing, creation, deletion and the three checkouts.
/// </summary>
public static class Branches
{
    /// <summary>
    /// The one error string in this whole domain that the frontend parses (GIT-003, XLANG-002).
    /// </summary>
    /// <remarks>
    /// <b>Verbatim, trailing space included.</b> <c>repoStore.ts:112</c> declares its own copy and
    /// tests it with <c>includes</c>, then strips it before showing the message. Rewording it —
    /// or losing the space — turns a recoverable conflict into a dead-end error, silently. It is
    /// a prefix rather than a match on libgit2's English text because that text has a singular
    /// form and can be reworded upstream.
    /// </remarks>
    public const string CheckoutConflictPrefix = "CHECKOUT_CONFLICT: ";

    /// <summary>
    /// Lists local and remote branches, with ahead/behind for local ones that have an upstream
    /// (GIT-007).
    /// </summary>
    /// <remarks>
    /// Remote branches always report <c>0/0</c> and no upstream: the counters compare a local
    /// branch to <i>its</i> upstream, not two remote refs to each other. Any failure along the way
    /// — no upstream configured, an upstream ref that no longer resolves — leaves the counts at
    /// zero rather than raising, so a branch tracking something deleted on the remote reads as
    /// in sync rather than as an error.
    /// </remarks>
    public static IReadOnlyList<BranchInfo> List(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        var result = new List<BranchInfo>();
        foreach (var branch in repo.Branches)
        {
            var target = branch.Tip?.Id.Sha;

            string? upstream = null;
            var ahead = 0;
            var behind = 0;

            if (!branch.IsRemote && branch.TrackedBranch is { } tracked)
            {
                upstream = tracked.FriendlyName;

                if (branch.Tip is { } localTip && tracked.Tip is { } upstreamTip)
                {
                    var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(localTip, upstreamTip);
                    ahead = divergence.AheadBy ?? 0;
                    behind = divergence.BehindBy ?? 0;
                }
            }

            result.Add(new BranchInfo(
                branch.FriendlyName, branch.IsCurrentRepositoryHead, branch.IsRemote, upstream, ahead, behind, target));
        }

        return result;
    }

    /// <summary>
    /// Creates a branch at <paramref name="startPoint"/>, or at HEAD when none is given (GIT-008).
    /// </summary>
    /// <remarks>
    /// Never forced, so creating a branch that already exists fails instead of moving it. Does not
    /// check the new branch out. <paramref name="startPoint"/> is any revparse-able expression.
    /// </remarks>
    public static void Create(string repoPath, string name, string? startPoint)
    {
        using var repo = RepoStatus.Open(repoPath);

        var target = startPoint is null
            ? repo.Head.Tip ?? throw new InvalidOperationException("reference 'HEAD' not found")
            : Peel(repo, startPoint);

        repo.CreateBranch(name, target);
    }

    /// <summary>
    /// Deletes a local or remote-tracking branch reference (GIT-009).
    /// </summary>
    /// <remarks>
    /// A bare ref delete: no check for unmerged commits, and none for the branch being checked out
    /// — libgit2 refuses that one itself, and its refusal surfaces unprefixed. Deleting a
    /// remote-tracking branch only removes the local ref; it never touches the server.
    /// </remarks>
    public static void Delete(string repoPath, string name, bool isRemote)
    {
        using var repo = RepoStatus.Open(repoPath);

        var branch = Find(repo, name, isRemote)
            ?? throw new InvalidOperationException($"cannot locate {(isRemote ? "remote-tracking" : "local")} branch '{name}'");

        repo.Branches.Remove(branch);
    }

    /// <summary>Checks out an existing local branch, moving HEAD with it (GIT-004).</summary>
    public static void CheckoutLocal(string repoPath, string name)
    {
        using var repo = RepoStatus.Open(repoPath);

        var branch = repo.Branches[name]
            ?? throw new InvalidOperationException($"cannot locate local branch '{name}'");

        // Default (safe) checkout, which is what raises the conflict this domain reports.
        Guarded(() => Commands.Checkout(repo, branch));
    }

    /// <summary>
    /// Checks out any revparse-able ref, commit, tag or SHA without moving a branch pointer
    /// (GIT-005).
    /// </summary>
    public static void CheckoutDetached(string repoPath, string refname)
    {
        using var repo = RepoStatus.Open(repoPath);

        var commit = Peel(repo, refname);
        Guarded(() => Commands.Checkout(repo, commit));
    }

    /// <summary>
    /// Switches to a local branch tracking <c>&lt;remote&gt;/&lt;name&gt;</c>, creating it only if
    /// it does not exist yet (GIT-006).
    /// </summary>
    /// <remarks>
    /// <b>An existing local branch of that name is reused unchanged</b> — its upstream is not
    /// checked, not repaired and not overwritten, even when it tracks something else or nothing at
    /// all. That is <c>AMBIGUOUS-GIT-a</c>: the source does not settle whether reuse, rejection or
    /// re-pointing was intended, so the behaviour is ported rather than decided. Only the short
    /// name after the first <c>/</c> matters; the remote portion is parsed and discarded.
    /// </remarks>
    public static string CheckoutRemoteTracking(string repoPath, string remoteBranch)
    {
        var separator = remoteBranch.IndexOf('/');
        if (separator < 0)
        {
            throw new ArgumentException("expected a name like 'origin/feature-x'");
        }

        var shortName = remoteBranch[(separator + 1)..];

        using (var repo = RepoStatus.Open(repoPath))
        {
            if (repo.Branches[shortName] is null)
            {
                var remote = repo.Branches[remoteBranch]
                    ?? throw new InvalidOperationException($"cannot locate remote-tracking branch '{remoteBranch}'");

                var created = repo.CreateBranch(shortName, remote.Tip);
                repo.Branches.Update(created, b => b.TrackedBranch = remote.CanonicalName);
            }
        }

        CheckoutLocal(repoPath, shortName);
        return shortName;
    }

    /// <summary>Tags a blocked checkout so the UI can offer to stash, and leaves everything else alone.</summary>
    private static void Guarded(Action checkout)
    {
        try
        {
            checkout();
        }
        catch (CheckoutConflictException e)
        {
            // Only this one. A bad ref or a corrupt repository keeps libgit2's raw message, with
            // no prefix, because there is nothing the UI could offer to do about it.
            throw new InvalidOperationException(CheckoutConflictPrefix + e.Message);
        }
    }

    private static Commit Peel(Repository repo, string revision)
    {
        var target = repo.Lookup(revision)
            ?? throw new InvalidOperationException($"revspec '{revision}' not found");

        return target.Peel<Commit>()
            ?? throw new InvalidOperationException($"the given reference '{revision}' does not resolve to a commit");
    }

    private static Branch? Find(Repository repo, string name, bool isRemote) =>
        repo.Branches.FirstOrDefault(b => b.IsRemote == isRemote && b.FriendlyName == name);
}
