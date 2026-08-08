using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>One changed path and the single label describing how it changed.</summary>
public sealed record FileStatusEntry(string Path, string Status);

/// <summary>What the Changes panel renders: every changed path, in exactly one bucket.</summary>
public sealed record RepoStatusInfo(
    IReadOnlyList<FileStatusEntry> Staged,
    IReadOnlyList<FileStatusEntry> Unstaged,
    IReadOnlyList<FileStatusEntry> Untracked,
    IReadOnlyList<FileStatusEntry> Conflicted,
    string? CurrentBranch,
    bool IsDetached);

/// <summary>
/// Repository status and reset.
/// </summary>
public static class RepoStatus
{
    /// <summary>Opens a repository. Every command opens its own and disposes it.</summary>
    /// <remarks>
    /// A fresh handle on every call, with no cache and no lock. That is the safer shape here:
    /// <see cref="LibGit2Sharp.Repository"/> is not thread-safe, and commands dispatch
    /// concurrently, so a shared handle would be a real race. Two concurrent commands competing
    /// over <c>.git/index</c> is long-standing behaviour, and nothing here serialises per
    /// repository.
    /// </remarks>
    public static Repository Open(string path) => new(path);

    /// <summary>
    /// Buckets every changed path into exactly one of staged / unstaged / untracked / conflicted
    /// (GIT-001).
    /// </summary>
    public static RepoStatusInfo GetStatus(string repoPath)
    {
        using var repo = Open(repoPath);

        var staged = new List<FileStatusEntry>();
        var unstaged = new List<FileStatusEntry>();
        var untracked = new List<FileStatusEntry>();
        var conflicted = new List<FileStatusEntry>();

        foreach (var entry in repo.RetrieveStatus(StatusRequest()))
        {
            // A path with no reachable label — an ignored file, say — is dropped rather than
            // bucketed, exactly as 1.7.2's `else { continue }` does.
            if (Label(entry.State) is not { } labelled)
            {
                continue;
            }

            var item = new FileStatusEntry(entry.FilePath, labelled.Label);
            switch (labelled.Bucket)
            {
                case "staged": staged.Add(item); break;
                case "unstaged": unstaged.Add(item); break;
                case "untracked": untracked.Add(item); break;
                case "conflicted": conflicted.Add(item); break;
            }
        }

        // `head()` fails on a repository with no commits yet, and `is_branch()` is false while
        // detached — both leave the branch name unset rather than erroring.
        var currentBranch = repo.Info.IsHeadUnborn || repo.Info.IsHeadDetached
            ? null
            : repo.Head.FriendlyName;

        return new RepoStatusInfo(staged, unstaged, untracked, conflicted, currentBranch, repo.Info.IsHeadDetached);
    }

    /// <summary>
    /// Moves HEAD, and with it the current branch, to a commit (GIT-002).
    /// </summary>
    /// <remarks>
    /// <c>mode</c> is caller-supplied free text and is deliberately not validated: <c>soft</c> and
    /// <c>hard</c> are recognised and <b>everything else, typos included, is treated as
    /// mixed</b>. The frontend only ever sends <c>"mixed"</c>, from its undo-last-commit action.
    /// There is no confirmation gate on <c>hard</c> here — 1.7.2 leaves that to the
    /// caller, and adding one would be new behaviour.
    /// </remarks>
    public static void ResetToCommit(string repoPath, string oid, string mode)
    {
        using var repo = Open(repoPath);

        var target = repo.Lookup<Commit>(new ObjectId(oid))
            ?? throw new InvalidOperationException($"object not found - no match for id ({oid})");

        var reset = mode switch
        {
            "soft" => ResetMode.Soft,
            "hard" => ResetMode.Hard,
            _ => ResetMode.Mixed,
        };

        repo.Reset(reset, target);
    }

    /// <summary>
    /// The status scan 1.7.2 asks for — and, just as importantly, the parts it does not.
    /// </summary>
    /// <remarks>
    /// Rename detection is on in both halves — this is <c>BUG-GIT-a</c>'s fix: 1.7.2 forced both
    /// flags off, so a staged rename arrived as an unrelated delete plus add and the
    /// <c>"renamed"</c> label below was dead code. Both flags are stated explicitly (index is
    /// LibGit2Sharp's default, workdir is not) so the pair cannot drift apart silently.
    /// <c>IncludeIgnored</c> still overrides the library's <c>true</c> default.
    /// </remarks>
    internal static StatusOptions StatusRequest() => new()
    {
        IncludeUntracked = true,
        RecurseUntrackedDirs = true,
        IncludeIgnored = false,
        DetectRenamesInIndex = true,
        DetectRenamesInWorkDir = true,
    };

    /// <summary>
    /// The first matching bucket and label, in 1.7.2's fixed priority order.
    /// </summary>
    /// <remarks>
    /// The order is the behaviour: a path both staged and modified again afterwards is reported
    /// once, as staged, because the index checks come first and the working-tree ones are never
    /// reached for it. <c>renamed</c> is live in both halves since <c>BUG-GIT-a</c>'s fix turned
    /// rename detection on in <see cref="StatusRequest"/>.
    /// </remarks>
    private static (string Bucket, string Label)? Label(FileStatus status) => status switch
    {
        _ when status.HasFlag(FileStatus.Conflicted) => ("conflicted", "conflicted"),
        _ when status.HasFlag(FileStatus.NewInIndex) => ("staged", "added"),
        _ when status.HasFlag(FileStatus.ModifiedInIndex) => ("staged", "modified"),
        _ when status.HasFlag(FileStatus.DeletedFromIndex) => ("staged", "deleted"),
        _ when status.HasFlag(FileStatus.RenamedInIndex) => ("staged", "renamed"),
        _ when status.HasFlag(FileStatus.TypeChangeInIndex) => ("staged", "typechange"),
        _ when status.HasFlag(FileStatus.NewInWorkdir) => ("untracked", "untracked"),
        _ when status.HasFlag(FileStatus.ModifiedInWorkdir) => ("unstaged", "modified"),
        _ when status.HasFlag(FileStatus.DeletedFromWorkdir) => ("unstaged", "deleted"),
        _ when status.HasFlag(FileStatus.RenamedInWorkdir) => ("unstaged", "renamed"),
        _ when status.HasFlag(FileStatus.TypeChangeInWorkdir) => ("unstaged", "typechange"),
        _ => null,
    };
}
