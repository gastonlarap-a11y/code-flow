using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>How a merge ended. <c>Status</c> is one of the four outcomes, never an error.</summary>
public sealed record MergeOutcome(string Status, IReadOnlyList<string> Conflicts);

/// <summary>A path with unresolved conflict stages in the index.</summary>
public sealed record ConflictFile(string Path);

/// <summary>The three sides of one conflicted file, as text. An absent side is the empty string.</summary>
public sealed record ConflictVersions(string Base, string Ours, string Theirs);

/// <summary>
/// Merging and conflict resolution.
/// </summary>
public static class Merge
{
    /// <summary>
    /// Merges a branch into HEAD, resolving to one of four outcomes in priority order (GIT-016).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Conflicts are a <b>success</b> return with <c>status == "conflicts"</c>, not an error: the
    /// repository is left mid-merge with <c>MERGE_HEAD</c> set and nothing committed, which is
    /// what the conflict-resolution UI then works on.
    /// </para>
    /// <para>
    /// A local branch always wins over an identically named remote one, because the remote lookup
    /// only happens when the local one fails. A fast-forward creates no merge commit, matching
    /// plain <c>git merge</c> rather than <c>--no-ff</c>.
    /// </para>
    /// </remarks>
    public static MergeOutcome Branch(
        string repoPath, string branchName, string? authorName = null, string? authorEmail = null)
    {
        using var repo = RepoStatus.Open(repoPath);

        var theirBranch = repo.Branches.FirstOrDefault(b => !b.IsRemote && b.FriendlyName == branchName)
                          ?? repo.Branches.FirstOrDefault(b => b.IsRemote && b.FriendlyName == branchName)
                          ?? throw new InvalidOperationException($"cannot locate local branch '{branchName}'");

        var theirCommit = theirBranch.Tip
            ?? throw new InvalidOperationException($"branch '{branchName}' has no target");

        var headCommit = repo.Info.IsHeadUnborn
            ? throw new InvalidOperationException("reference 'HEAD' not found")
            : repo.Head.Tip!;

        // LibGit2Sharp exposes no merge_analysis, so the same three questions are asked directly
        // of the merge base — which is what the analysis answers anyway.
        var mergeBase = repo.ObjectDatabase.FindMergeBase(headCommit, theirCommit);

        if (mergeBase is not null && mergeBase.Id == theirCommit.Id)
        {
            return new MergeOutcome("up_to_date", []);
        }

        if (mergeBase is not null && mergeBase.Id == headCommit.Id)
        {
            // Moves the current branch and forces the working tree to match: no merge commit.
            repo.Reset(ResetMode.Hard, theirCommit);
            return new MergeOutcome("fast_forward", []);
        }

        var merger = Signature(repo, authorName, authorEmail);
        repo.Merge(theirCommit, merger, new MergeOptions
        {
            CommitOnSuccess = false,
            FastForwardStrategy = FastForwardStrategy.NoFastForward,
        });

        if (repo.Index.Conflicts.Any())
        {
            // Left mid-merge on purpose. No cleanup here — the user has work to do.
            return new MergeOutcome("conflicts", ConflictPaths(repo));
        }

        var tree = repo.Index.WriteToTree();
        var commit = repo.ObjectDatabase.CreateCommit(
            merger, merger, $"Merge branch '{branchName}'", tree, [headCommit, theirCommit], prettifyMessage: false);

        Diff.MoveHeadTo(repo, commit);
        CleanupState(repo, commit);

        return new MergeOutcome("merged", []);
    }

    /// <summary>Whether the repository is mid-merge (GIT-019).</summary>
    /// <remarks>
    /// Merge specifically. A rebase, cherry-pick, revert or bisect all report <c>false</c> — none
    /// of which this application ever starts.
    /// </remarks>
    public static bool IsMerging(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        return repo.Info.CurrentOperation == CurrentOperation.Merge;
    }

    /// <summary>Every path with unresolved conflict stages.</summary>
    public static IReadOnlyList<ConflictFile> ListConflicts(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        return ConflictPaths(repo).Select(path => new ConflictFile(path)).ToList();
    }

    /// <summary>
    /// The three conflicting versions of a file, read straight from the index stages (GIT-017).
    /// </summary>
    /// <remarks>
    /// Not a command. It feeds the AI conflict resolver, which gets each side whole rather than
    /// having to reverse-engineer them out of the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> markers in
    /// the working copy. A side that does not exist — the file was added or deleted there — reads
    /// as the empty string, not as an error.
    /// </remarks>
    public static ConflictVersions Versions(string repoPath, string relPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        var conflict = FindConflict(repo, relPath)
            ?? throw new InvalidOperationException("no conflict for this path");

        return new ConflictVersions(Text(repo, conflict.Ancestor), Text(repo, conflict.Ours), Text(repo, conflict.Theirs));
    }

    /// <summary>
    /// Resolves a conflict by taking one whole side, writing it to disk and staging it (GIT-017).
    /// </summary>
    public static void ResolveSide(string repoPath, string relPath, string side)
    {
        if (side is not ("ours" or "theirs"))
        {
            throw new ArgumentException("side must be 'ours' or 'theirs'");
        }

        using var repo = RepoStatus.Open(repoPath);

        var conflict = FindConflict(repo, relPath)
            ?? throw new InvalidOperationException("no conflict for this path");

        var entry = (side == "ours" ? conflict.Ours : conflict.Theirs)
            ?? throw new InvalidOperationException(
                "that side has no content for this file (it was added/deleted)");

        var blob = repo.Lookup<Blob>(entry.Id)
            ?? throw new InvalidOperationException($"object not found - no match for id ({entry.Id.Sha})");

        using (var content = blob.GetContentStream())
        using (var file = File.Create(System.IO.Path.Combine(repoPath, relPath)))
        {
            content.CopyTo(file);
        }

        // Staging a conflicted path clears all three of its stages and re-adds it as a normal
        // entry, taken from what is now on disk.
        repo.Index.Add(relPath);
        repo.Index.Write();
    }

    /// <summary>Stages whatever is on disk for a conflicted path, for a hand-edited resolution.</summary>
    public static void MarkResolved(string repoPath, string relPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        repo.Index.Add(relPath);
        repo.Index.Write();
    }

    /// <summary>
    /// Commits the resolved index as a two-parent merge commit and clears the merge state
    /// (GIT-018).
    /// </summary>
    /// <remarks>
    /// <c>MERGE_HEAD</c> is read fresh rather than remembered from the earlier merge call, so this
    /// still works after the application was restarted mid-conflict.
    /// </remarks>
    public static string Complete(
        string repoPath, string message, string? authorName = null, string? authorEmail = null)
    {
        using var repo = RepoStatus.Open(repoPath);

        if (repo.Index.Conflicts.Any())
        {
            throw new InvalidOperationException("There are still unresolved conflicts");
        }

        var headCommit = repo.Info.IsHeadUnborn
            ? throw new InvalidOperationException("reference 'HEAD' not found")
            : repo.Head.Tip!;

        var theirCommit = repo.Lookup<Commit>(MergeHead(repo))
            ?? throw new InvalidOperationException("MERGE_HEAD has no target");

        var signature = Signature(repo, authorName, authorEmail);
        var tree = repo.Index.WriteToTree();

        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, message, tree, [headCommit, theirCommit], prettifyMessage: false);

        Diff.MoveHeadTo(repo, commit);
        CleanupState(repo, commit);

        return commit.Sha;
    }

    /// <summary>
    /// Throws the in-progress merge away, restoring the working tree to HEAD (GIT-018).
    /// </summary>
    /// <remarks>
    /// Does not check whether conflicts exist first: calling this during a clean, uncommitted
    /// merge still discards that merge's staged result.
    /// </remarks>
    public static void Abort(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        var headCommit = repo.Info.IsHeadUnborn
            ? throw new InvalidOperationException("reference 'HEAD' not found")
            : repo.Head.Tip!;

        // A hard reset to where HEAD already is: same force-checkout of HEAD's tree 1.7.2
        // performs, and it clears the merge state on the way through.
        repo.Reset(ResetMode.Hard, headCommit);
        CleanupState(repo, headCommit);
    }

    /// <summary>
    /// Clears <c>MERGE_HEAD</c> and friends once a merge is finished.
    /// </summary>
    /// <remarks>
    /// LibGit2Sharp exposes no equivalent of libgit2's <c>git_repository_state_cleanup</c>, and
    /// <c>MERGE_HEAD</c> is not reachable through <see cref="IRepository.Refs"/> either — the
    /// native call only runs as a side effect of committing or resetting through the library's own
    /// helpers, which are not the paths used here. A hard reset onto the commit just written
    /// triggers it while leaving the tree exactly where it already is; the file removal after it
    /// is the belt-and-braces half, because a repository still holding <c>MERGE_HEAD</c> reports
    /// itself as merging forever and the conflict banner never goes away.
    /// </remarks>
    private static void CleanupState(Repository repo, Commit head)
    {
        if (repo.Info.CurrentOperation != CurrentOperation.None)
        {
            repo.Reset(ResetMode.Mixed, head);
        }

        foreach (var name in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE" })
        {
            var file = System.IO.Path.Combine(repo.Info.Path, name);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>The commit the in-progress merge is bringing in.</summary>
    private static ObjectId MergeHead(Repository repo)
    {
        var file = System.IO.Path.Combine(repo.Info.Path, "MERGE_HEAD");
        if (!File.Exists(file))
        {
            throw new InvalidOperationException("MERGE_HEAD has no target");
        }

        return new ObjectId(File.ReadAllText(file).Trim());
    }

    /// <summary>
    /// Conflicted paths, deduplicated, taking whichever stage exists for the display path.
    /// </summary>
    private static List<string> ConflictPaths(Repository repo)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var conflict in repo.Index.Conflicts)
        {
            var entry = conflict.Ours ?? conflict.Theirs ?? conflict.Ancestor;
            if (entry is not null && seen.Add(entry.Path))
            {
                result.Add(entry.Path);
            }
        }

        return result;
    }

    private static Conflict? FindConflict(Repository repo, string relPath) =>
        repo.Index.Conflicts.FirstOrDefault(c =>
            (c.Ours ?? c.Theirs ?? c.Ancestor)?.Path == relPath);

    private static string Text(Repository repo, IndexEntry? entry) =>
        entry is not null && repo.Lookup<Blob>(entry.Id) is { } blob
            ? blob.GetContentText()
            : string.Empty;

    /// <summary>
    /// The merge signature: the resolved author when both halves were supplied, else the repo's
    /// configured one — the same both-or-neither rule as <c>Diff.CommitIndex</c> (GIT-028/GIT-036).
    /// </summary>
    private static Signature Signature(Repository repo, string? authorName, string? authorEmail) =>
        authorName is not null && authorEmail is not null
            ? new Signature(authorName, authorEmail, DateTimeOffset.Now)
            : repo.Config.BuildSignature(DateTimeOffset.Now)
              ?? throw new InvalidOperationException(
                  "config value 'user.name' was not found - please set user.name and user.email");
}
