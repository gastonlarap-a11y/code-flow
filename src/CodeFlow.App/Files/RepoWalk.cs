using LibGit2Sharp;

namespace CodeFlow.Files;

/// <summary>
/// The one walk of the working tree that "go to file", search and replace all run on
/// (<c>FILE-007</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>DIVERGENCE-FILE-a</c>: this prunes, it does not filter.</b> An ignored directory is never
/// read from disk at all, rather than being walked and dropped afterwards. In a real project
/// <c>node_modules</c> and <c>bin</c> hold more entries than the source does, and descending into
/// them is the difference between an instant palette and a spinner. Reimplementing this as
/// walk-then-filter changes both the cost and — through <see cref="MaxFiles"/> — the results.
/// </para>
/// <para>
/// <c>Repository.Ignore.IsPathIgnored</c> is libgit2's own answer, the same engine and the same
/// rules 1.7.2 asks.
/// </para>
/// </remarks>
internal static class RepoWalk
{
    /// <summary>
    /// Ceiling on how many paths "go to file" will hold. Well past any repo a person navigates by
    /// name, and low enough that a pathological tree cannot freeze the palette.
    /// </summary>
    /// <remarks>
    /// Reaching it is silent: there is no truncation flag for this cap, only for
    /// <c>maxResults</c>. That is 1.7.2's behaviour and not an oversight (<c>FILE-008</c>).
    /// </remarks>
    public const int MaxFiles = 20_000;

    /// <summary>Every non-ignored file in the repo, repo-relative and sorted.</summary>
    public static List<string> Files(Repository repo, string root)
    {
        var output = new List<string>();
        Walk(repo, root, string.Empty, output, MaxFiles);

        return output;
    }

    /// <summary>The working directory of an open repository, or 1.7.2's refusal.</summary>
    public static string WorkingDirectory(Repository repo) =>
        Path.TrimEndingDirectorySeparator(
            repo.Info.WorkingDirectory ?? throw new InvalidOperationException("bare repository"));

    private static void Walk(Repository repo, string root, string rel, List<string> output, int limit)
    {
        if (output.Count >= limit)
        {
            return;
        }

        var directory = rel.Length == 0 ? root : Path.Combine(root, rel);

        // Infos rather than paths, so "is this a directory" comes from what the enumeration already
        // read instead of a second, separately-timed `Directory.Exists`. Same reason as
        // `FileOps.ListDir` (`FILE-007`): under directory churn that race answers "file" for a
        // folder, and here the cost is silent — the walk stops descending and every file beneath it
        // vanishes from search and from "go to file", with nothing to say so.
        FileSystemInfo[] children;
        try
        {
            children = new DirectoryInfo(directory).GetFileSystemInfos();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A directory that cannot be read is skipped, not fatal: read_dir's Err is discarded
            // the same way.
            return;
        }

        // Sorted so the palette's order is stable between calls rather than filesystem-dependent.
        // Ordinal, because 1.7.2 sorts by OsString, which compares bytes.
        Array.Sort(children, (a, b) => string.CompareOrdinal(a.Name, b.Name));

        foreach (var child in children)
        {
            if (output.Count >= limit)
            {
                return;
            }

            var name = child.Name;
            if (name == ".git")
            {
                continue;
            }

            var childRel = rel.Length == 0 ? name : $"{rel}/{name}";
            // From the enumeration's own classification, like `FileOps.ListDir` and for the same
            // reason: anything else needs a `stat`, and a `stat` can be refused while the
            // enumeration succeeds (`FILE-017`).
            var isDirectory = child is DirectoryInfo;

            // git wants a trailing slash to answer "is this *directory* ignored" for rules like
            // `build/`; without it a directory-only rule does not match and we would descend anyway.
            var probe = isDirectory ? $"{childRel}/" : childRel;
            if (IsIgnored(repo, probe))
            {
                continue;
            }

            if (isDirectory)
            {
                Walk(repo, root, childRel, output, limit);
            }
            else
            {
                output.Add(childRel);
            }
        }
    }

    private static bool IsIgnored(Repository repo, string probe)
    {
        try
        {
            return repo.Ignore.IsPathIgnored(probe);
        }
        catch (LibGit2SharpException)
        {
            // `is_path_ignored(...).unwrap_or(false)`: a path libgit2 cannot judge is walked.
            return false;
        }
    }
}
