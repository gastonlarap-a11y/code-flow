using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>One commit, with its parent links and every ref name pointing at it.</summary>
public sealed record CommitInfo(
    string Id,
    string ShortId,
    string Summary,
    string AuthorName,
    string AuthorEmail,
    long Timestamp,
    IReadOnlyList<string> ParentIds,
    IReadOnlyList<string> Refs);

/// <summary>
/// History walks (GIT-020, GIT-021).
/// </summary>
/// <remarks>
/// Raw parent links only. Lane and layout computation for the graph view happens in the frontend,
/// which is what keeps this call cheap to re-run.
/// </remarks>
public static class CommitGraph
{
    /// <summary>
    /// Walks history newest first, topologically, from HEAD or from every branch (GIT-020).
    /// </summary>
    /// <remarks>
    /// <paramref name="allRefs"/> pushes local and remote <b>branches</b> — not tags, so a commit
    /// only a tag points at is never visited, even though its tag name would appear in
    /// <c>Refs</c> if it were reached some other way.
    /// </remarks>
    public static IReadOnlyList<CommitInfo> List(string repoPath, bool allRefs, int limit)
    {
        using var repo = RepoStatus.Open(repoPath);

        if (limit <= 0 || repo.Info.IsHeadUnborn)
        {
            return [];
        }

        var refMap = RefMap(repo);

        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = allRefs
                ? repo.Branches.Select(b => b.CanonicalName).ToList()
                : (object)repo.Head,
        };

        return repo.Commits.QueryBy(filter).Take(limit).Select(c => Describe(c, refMap)).ToList();
    }

    /// <summary>
    /// Commits on HEAD's branch that its upstream does not have — what <c>git push</c> would send
    /// (GIT-021).
    /// </summary>
    /// <remarks>
    /// Empty rather than an error when HEAD is detached or the branch has no upstream: there is
    /// nothing to compare against, which is not a failure. No limit either, so a badly diverged
    /// branch returns everything.
    /// </remarks>
    public static IReadOnlyList<CommitInfo> Unpushed(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        if (repo.Info.IsHeadUnborn || repo.Info.IsHeadDetached)
        {
            return [];
        }

        if (repo.Head.TrackedBranch is not { } upstream || upstream.Tip is null)
        {
            return [];
        }

        var refMap = RefMap(repo);

        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = repo.Head.Tip,
            ExcludeReachableFrom = upstream.Tip,
        };

        return repo.Commits.QueryBy(filter).Select(c => Describe(c, refMap)).ToList();
    }

    /// <summary>Commit id to every branch or tag shorthand pointing at it.</summary>
    /// <remarks>
    /// Annotated tags point at a tag object rather than at a commit, so each ref is peeled before
    /// being recorded — otherwise a tagged commit would never match its own tag.
    /// </remarks>
    private static Dictionary<string, List<string>> RefMap(Repository repo)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        string[] prefixes = ["refs/heads/", "refs/remotes/", "refs/tags/"];

        foreach (var reference in repo.Refs)
        {
            var prefix = prefixes.FirstOrDefault(p => reference.CanonicalName.StartsWith(p, StringComparison.Ordinal));
            if (prefix is null)
            {
                continue;
            }

            if (reference.ResolveToDirectReference()?.Target?.Peel<Commit>() is not { } target)
            {
                continue;
            }

            if (!map.TryGetValue(target.Id.Sha, out var names))
            {
                names = [];
                map[target.Id.Sha] = names;
            }

            names.Add(reference.CanonicalName[prefix.Length..]);
        }

        return map;
    }

    private static CommitInfo Describe(Commit commit, Dictionary<string, List<string>> refMap)
    {
        var id = commit.Id.Sha;

        return new CommitInfo(
            id,
            id[..Math.Min(7, id.Length)],
            commit.MessageShort,
            commit.Author.Name,
            commit.Author.Email,
            // The committer's time, not the author's: git2's `commit.time()` is the commit time,
            // and the two differ on anything rebased, cherry-picked or amended.
            commit.Committer.When.ToUnixTimeSeconds(),
            commit.Parents.Select(p => p.Id.Sha).ToList(),
            refMap.TryGetValue(id, out var refs) ? refs : []);
    }
}
