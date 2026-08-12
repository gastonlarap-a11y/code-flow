using System.Text;
using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>One line of a hunk. <c>Origin</c> is a single character: <c>" "</c>, <c>"+"</c> or <c>"-"</c>.</summary>
public sealed record DiffLine(string Origin, string Content, int? OldLineno, int? NewLineno);

/// <summary>One hunk, with its <c>@@</c> header and every line it covers.</summary>
public sealed record DiffHunkInfo(string Header, IReadOnlyList<DiffLine> Lines);

/// <summary>One file's diff.</summary>
public sealed record FileDiffInfo(
    string? OldPath,
    string? NewPath,
    string Status,
    IReadOnlyList<DiffHunkInfo> Hunks);

/// <summary>One file a commit touched, without its content (GIT-035).</summary>
/// <remarks>
/// Deliberately <see cref="FileDiffInfo"/> minus the hunks: the graph expands a commit into this
/// list and only fetches a diff once a file is picked, so carrying even one file's whole content
/// here would defeat the point.
/// </remarks>
public sealed record CommitFileInfo(string? OldPath, string? NewPath, string Status);

/// <summary>
/// Diffs, staging, discarding and commit.
/// </summary>
/// <remarks>
/// Named after diffing rather than after staging: besides the staging commands this holds
/// the three read-only diffs, <c>commit</c>, and the branch-resolution helpers the PR review
/// pipeline is built on (GIT-030) — which nobody would look for inside a file called
/// <c>Staging.cs</c>.
/// </remarks>
public static class Diff
{
    /// <summary>
    /// Large enough that a hunk covers the whole file (GIT-029).
    /// </summary>
    /// <remarks>
    /// The Changes tab wants the entire file with the edited lines highlighted, not a compact
    /// PR-style patch with a few lines of context. This is that decision, expressed as a number.
    /// </remarks>
    private const int FullFileContextLines = 1_000_000;

    /// <summary>
    /// The comparison settings every diff here uses.
    /// </summary>
    /// <remarks>
    /// <b><see cref="SimilarityOptions.Renames"/> closes <c>BUG-GIT-a</c></b>: 1.7.2 never called
    /// <c>find_similar</c>, so a rename arrived as an unrelated delete plus add and the
    /// <c>"renamed"</c> label was unreachable. Stated explicitly rather than left unset so the
    /// behaviour cannot drift with the user's <c>diff.renames</c> config. Copies stay undetected
    /// on purpose — git's own default — so <c>"copied"</c> remains defined but rare (a host
    /// could still report it on a fetched diff).
    /// </remarks>
    private static CompareOptions FullFile() => new()
    {
        ContextLines = FullFileContextLines,
        Similarity = SimilarityOptions.Renames,
    };

    /// <summary>
    /// Flattens a diff into the plain text an AI prompt is given.
    /// </summary>
    /// <remarks>
    /// Not a valid patch and not meant to be one: a per-file <c>--- path (status)</c> banner, then
    /// the changed lines with the origin character prefixed. The model reads it; no <c>git apply</c>
    /// ever does. Deleted files report their old path, since the new one is null.
    /// <para>
    /// The shaping lives in <see cref="PromptDiff"/>, which is where the reasoning is: these diffs
    /// carry whole-file context for the Changes tab (<c>GIT-029</c>), and a prompt wants what
    /// changed. That method is pure and tested; this one is the name the three prompt paths already
    /// call.
    /// </para>
    /// </remarks>
    public static string RenderForPrompt(
        IReadOnlyList<FileDiffInfo> files, int budgetChars = PromptDiff.DefaultBudgetChars) =>
        PromptDiff.Render(files, budgetChars);

    /// <summary>The working tree against the index, untracked content included (GIT-029).</summary>
    public static IReadOnlyList<FileDiffInfo> Working(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        return Collect(repo.Diff.Compare<Patch>(null, includeUntracked: true, null, FullFile()));
    }

    /// <summary>The index against HEAD's tree, or against nothing in a repository with no commits.</summary>
    public static IReadOnlyList<FileDiffInfo> Staged(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        var head = repo.Info.IsHeadUnborn ? null : repo.Head.Tip?.Tree;
        return Collect(repo.Diff.Compare<Patch>(head, DiffTargets.Index, null, null, FullFile()));
    }

    /// <summary>
    /// One commit against its first parent (GIT-029).
    /// </summary>
    /// <remarks>
    /// First parent only: for a merge commit that is what changed relative to the branch merged
    /// into, never a combined diff. A root commit diffs against an empty tree.
    /// </remarks>
    public static IReadOnlyList<FileDiffInfo> Commit(string repoPath, string oid)
    {
        using var repo = RepoStatus.Open(repoPath);
        var commit = LookupCommit(repo, oid);

        return Collect(repo.Diff.Compare<Patch>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, FullFile()));
    }

    /// <summary>
    /// The paths one commit touched, with their status and no content at all (GIT-035).
    /// </summary>
    /// <remarks>
    /// <see cref="TreeChanges"/> rather than <see cref="Patch"/> is the whole point: same trees,
    /// same first-parent rule and the same <see cref="FullFile"/> options as
    /// <see cref="Commit(string, string)"/> — so the statuses and the ordering match what the diff
    /// will later report — but libgit2 never renders a patch, which is what makes expanding a
    /// commit in the graph cheap next to the whole-file content <c>GIT-029</c> mandates.
    /// </remarks>
    public static IReadOnlyList<CommitFileInfo> CommitFiles(string repoPath, string oid)
    {
        using var repo = RepoStatus.Open(repoPath);
        var commit = LookupCommit(repo, oid);

        var changes = repo.Diff.Compare<TreeChanges>(
            commit.Parents.FirstOrDefault()?.Tree, commit.Tree, FullFile());

        return changes
            .Select(change => new CommitFileInfo(change.OldPath, change.Path, StatusLabel(change.Status)))
            .ToList();
    }

    /// <summary>
    /// One file's diff inside one commit (GIT-035).
    /// </summary>
    /// <remarks>
    /// <paramref name="oldPath"/> is not redundant with <paramref name="filePath"/>: libgit2 applies
    /// the pathspec <b>before</b> rename detection, so a renamed file filtered by its new path alone
    /// loses the matching delete and comes back as a plain <c>added</c> file whose diff shows every
    /// line as new. Passing both paths keeps both deltas alive for <c>find_similar</c> to pair up.
    /// </remarks>
    public static IReadOnlyList<FileDiffInfo> CommitFile(
        string repoPath, string oid, string filePath, string? oldPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        var commit = LookupCommit(repo, oid);

        var paths = string.IsNullOrEmpty(oldPath) || oldPath == filePath
            ? new[] { filePath }
            : new[] { filePath, oldPath };

        return Collect(repo.Diff.Compare<Patch>(
            commit.Parents.FirstOrDefault()?.Tree, commit.Tree, paths, FullFile()));
    }

    /// <summary>The commit an oid names, or the error the frontend already reports for a bad one.</summary>
    private static Commit LookupCommit(Repository repo, string oid) =>
        repo.Lookup<Commit>(new ObjectId(oid))
            ?? throw new InvalidOperationException($"object not found - no match for id ({oid})");

    /// <summary>
    /// Everything a merge of <paramref name="head"/> into <paramref name="baseRef"/> would bring
    /// in, computed from the merge base without any network call (GIT-030).
    /// </summary>
    public static IReadOnlyList<FileDiffInfo> BranchDiff(string repoPath, string baseRef, string head)
    {
        using var repo = RepoStatus.Open(repoPath);

        var baseCommit = ResolveBranchCommit(repo, baseRef);
        var headCommit = ResolveBranchCommit(repo, head);

        var mergeBase = repo.ObjectDatabase.FindMergeBase(baseCommit, headCommit)
            ?? throw new InvalidOperationException($"no merge base found between '{baseRef}' and '{head}'");

        return Collect(repo.Diff.Compare<Patch>(mergeBase.Tree, headCommit.Tree, FullFile()));
    }

    /// <summary>
    /// Everything a branch contributes over <paramref name="baseRef"/>, committed or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not <see cref="BranchDiff"/> plus <see cref="Working"/>.</b> Those two are
    /// separate comparisons against separate baselines, so a file changed by a commit on the branch
    /// <em>and</em> edited again since would appear in both, twice, with no way for a reader — or a
    /// model — to tell that the second diff continues the first rather than duplicating it. This is
    /// one comparison against one baseline, so every file appears once with its cumulative change.
    /// </para>
    /// <para>
    /// The baseline is the merge base rather than <paramref name="baseRef"/>'s tip, exactly as
    /// <see cref="BranchDiff"/> computes it (<c>GIT-030</c>): what matters is what this branch added,
    /// not what the base branch has moved on to since.
    /// </para>
    /// <para>
    /// Untracked files are included, and that is a property of the target rather than something
    /// asked for here: LibGit2Sharp turns <see cref="DiffTargets.WorkingDirectory"/> into
    /// <c>DiffModifiers.IncludeUntracked</c> on its own (verified in LibGit2Sharp 0.32.0's
    /// <c>Diff.Compare</c>). A brand-new file nobody has staged is usually the most important thing
    /// a branch contributes, so the behaviour is wanted — it is documented here because it is not
    /// visible at the call site.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FileDiffInfo> BranchContribution(string repoPath, string baseRef)
    {
        using var repo = RepoStatus.Open(repoPath);

        var baseCommit = ResolveBranchCommit(repo, baseRef);

        // HEAD rather than a named branch: the point of this diff is the working tree, and a
        // detached or just-branched HEAD still has one.
        var headCommit = repo.Head.Tip
            ?? throw new InvalidOperationException(
                "This repository has no commits yet, so there is no branch to compare.");

        var mergeBase = repo.ObjectDatabase.FindMergeBase(baseCommit, headCommit)
            ?? throw new InvalidOperationException(
                $"no merge base found between '{baseRef}' and the current branch");

        return Collect(repo.Diff.Compare<Patch>(
            mergeBase.Tree, DiffTargets.WorkingDirectory | DiffTargets.Index, null, null, FullFile()));
    }

    /// <summary>The full SHA a ref resolves to, used to record what a review ran against.</summary>
    public static string ResolveSha(string repoPath, string refname)
    {
        using var repo = RepoStatus.Open(repoPath);
        return ResolveBranchCommit(repo, refname).Sha;
    }

    /// <summary>
    /// The commit the checked-out branch is on.
    /// </summary>
    /// <remarks>
    /// Not <c>ResolveSha(repoPath, "HEAD")</c>, and the difference is not cosmetic:
    /// <see cref="ResolveBranchCommit"/> tries <c>origin/{name}</c> first (<c>GIT-030</c>), so on a
    /// clone that has an <c>origin/HEAD</c> — which is most of them — that call answers with the
    /// remote's default branch instead of the branch you are standing on. Stamping a review with
    /// that SHA would record it as having judged <c>main</c>.
    /// </remarks>
    public static string HeadSha(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        return repo.Head.Tip?.Sha
            ?? throw new InvalidOperationException("This repository has no commits yet, so HEAD names nothing.");
    }

    /// <summary>The paths that changed between two refs, so a re-review can tell what it may skip.</summary>
    public static IReadOnlyList<string> ChangedFilesBetween(string repoPath, string from, string to) =>
        BranchDiff(repoPath, from, to)
            .Select(f => f.NewPath ?? f.OldPath)
            .OfType<string>()
            .ToList();

    /// <summary>
    /// Resolves a PR's base or head to a commit, preferring the remote-tracking ref (GIT-030).
    /// </summary>
    /// <remarks>
    /// <c>origin/&lt;name&gt;</c> is tried before the bare local name on purpose: a stale local
    /// branch is exactly what makes an up-to-date PR diff come back empty. A name starting with
    /// <c>refs/</c> is used verbatim and nothing else is tried, which is how a freshly fetched
    /// <c>refs/pull/&lt;n&gt;/head</c> resolves. Only <c>origin</c> is ever consulted by name.
    /// </remarks>
    private static Commit ResolveBranchCommit(Repository repo, string name)
    {
        string[] candidates = name.StartsWith("refs/", StringComparison.Ordinal)
            ? [name]
            : [$"origin/{name}", $"refs/remotes/origin/{name}", name];

        foreach (var candidate in candidates)
        {
            if (repo.Lookup(candidate)?.Peel<Commit>() is { } commit)
            {
                return commit;
            }
        }

        throw new InvalidOperationException(
            $"Could not find branch '{name}' locally or on origin — try fetching this repository first.");
    }

    /// <summary>
    /// Stages one path — or, when it is gone from disk, stages its removal (GIT-013).
    /// </summary>
    public static void StageFile(string repoPath, string filePath)
    {
        using var repo = RepoStatus.Open(repoPath);

        if (File.Exists(Path.Combine(repoPath, filePath)))
        {
            repo.Index.Add(filePath);
        }
        else
        {
            repo.Index.Remove(filePath);
        }

        repo.Index.Write();
    }

    /// <summary>Stages everything, the equivalent of <c>git add -A</c>.</summary>
    public static void StageAll(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);
        Commands.Stage(repo, "*");
    }

    /// <summary>Resets one path in the index back to HEAD's version.</summary>
    public static void UnstageFile(string repoPath, string filePath)
    {
        using var repo = RepoStatus.Open(repoPath);

        // No HEAD to reset to in a repository with no commits — an error, as in 1.7.2.
        var head = repo.Info.IsHeadUnborn
            ? throw new InvalidOperationException("reference 'HEAD' not found")
            : repo.Head.Tip!;

        repo.Index.Replace(head, [filePath]);
        repo.Index.Write();
    }

    /// <summary>Rewrites the whole index from HEAD's tree.</summary>
    public static void UnstageAll(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        var head = repo.Info.IsHeadUnborn
            ? throw new InvalidOperationException("reference 'HEAD' not found")
            : repo.Head.Tip!;

        repo.Index.Replace(head);
        repo.Index.Write();
    }

    /// <summary>Restores one path from the index, discarding the unstaged edit on top of it.</summary>
    public static void DiscardFileChanges(string repoPath, string filePath)
    {
        using var repo = RepoStatus.Open(repoPath);
        RestoreFromIndex(repo, repoPath, filePath);
    }

    /// <summary>
    /// Discards exactly what the Changes section lists, and nothing else (GIT-012).
    /// </summary>
    /// <remarks>
    /// Tracked paths go back to <b>what the index holds</b>, not to HEAD — so a file staged and
    /// then edited again keeps its staged content and only loses the edit on top. Staged-only
    /// changes are in neither list and are untouched. Conflicted paths are skipped entirely:
    /// resolving a merge belongs to the conflict banner, not to this button.
    /// </remarks>
    public static void DiscardAllChanges(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        var workdir = repo.Info.WorkingDirectory
            ?? throw new InvalidOperationException("bare repository");

        var tracked = new List<string>();
        var untracked = new List<string>();

        foreach (var entry in repo.RetrieveStatus(RepoStatus.StatusRequest()))
        {
            var state = entry.State;
            if (state.HasFlag(FileStatus.Conflicted))
            {
                continue;
            }

            if (state.HasFlag(FileStatus.NewInWorkdir))
            {
                untracked.Add(entry.FilePath);
            }
            else if (state.HasFlag(FileStatus.ModifiedInWorkdir)
                     || state.HasFlag(FileStatus.DeletedFromWorkdir)
                     || state.HasFlag(FileStatus.RenamedInWorkdir)
                     || state.HasFlag(FileStatus.TypeChangeInWorkdir))
            {
                tracked.Add(entry.FilePath);
            }
        }

        foreach (var filePath in tracked)
        {
            RestoreFromIndex(repo, workdir, filePath);
        }

        foreach (var filePath in untracked)
        {
            var full = Path.Combine(workdir, filePath);
            try
            {
                File.Delete(full);
            }
            catch (Exception e) when (e is not FileNotFoundException and not DirectoryNotFoundException)
            {
                // Not atomic: whatever was already deleted stays deleted. Ported as-is.
                throw new InvalidOperationException($"{filePath}: {e.Message}");
            }

            RemoveEmptiedDirectories(Path.GetDirectoryName(full), workdir);
        }
    }

    /// <summary>
    /// Writes a path's staged content back over the working-tree file.
    /// </summary>
    /// <remarks>
    /// CodeFlow 1.7.2 uses <c>checkout_index</c> with an explicit path list, which restores from
    /// the <b>index</b> rather than from a commit. LibGit2Sharp has no equivalent —
    /// <see cref="IRepository.CheckoutPaths"/> only accepts a committish, and using HEAD instead
    /// would throw away staged content that must survive. So the entry's blob is written out
    /// directly, through the checkout filters so line endings match what a checkout would produce,
    /// and the executable bit is reapplied because writing a stream does not carry it.
    /// </remarks>
    private static void RestoreFromIndex(Repository repo, string workdir, string filePath)
    {
        if (repo.Index[filePath] is not { } entry || repo.Lookup<Blob>(entry.Id) is not { } blob)
        {
            // Not in the index: checkout_index has nothing to restore for it either.
            return;
        }

        var full = Path.Combine(workdir, filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using (var content = blob.GetContentStream(new FilteringOptions(filePath)))
        using (var file = File.Create(full))
        {
            content.CopyTo(file);
        }

        if (!OperatingSystem.IsWindows() && entry.Mode == Mode.ExecutableFile)
        {
            File.SetUnixFileMode(
                full,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Walks up deleting directories the deletion just emptied, stopping at the first that is not.
    /// </summary>
    /// <remarks>
    /// Git records no empty directories, so the folder an untracked file lived in is usually
    /// untracked itself and would be left behind as a stray empty folder in the file tree.
    /// Deleting a directory only ever succeeds when it is already empty, so this cannot take
    /// anything with it.
    /// </remarks>
    private static void RemoveEmptiedDirectories(string? directory, string workdir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workdir));

        while (directory is not null)
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (string.Equals(current, root, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                Directory.Delete(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not empty, or not ours to remove. Either way the walk stops — this is tidying,
                // not part of the discard. The filter matters: an unfiltered catch here would also
                // swallow a programmer error introduced later, and this method has no other signal.
                return;
            }

            directory = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    /// Commits whatever is staged right now (GIT-028).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The explicit author is used only when <b>both</b> halves are supplied; one without the
    /// other falls back to the configured identity entirely rather than merging the two. The same
    /// signature is used for author and committer.
    /// </para>
    /// <para>
    /// Always exactly one parent — HEAD, or none for a root commit. This is why the commit is
    /// built through the object database instead of <see cref="IRepository.Commit"/>, which would
    /// notice <c>MERGE_HEAD</c> and quietly produce a two-parent merge commit. CodeFlow 1.7.2 does
    /// not do that, and committing during a merge through this path is a real thing a user can do.
    /// Completing a merge is a different command.
    /// </para>
    /// </remarks>
    public static string CommitIndex(string repoPath, string message, string? authorName, string? authorEmail)
    {
        using var repo = RepoStatus.Open(repoPath);

        var tree = repo.Index.WriteToTree();

        var signature = authorName is not null && authorEmail is not null
            ? new Signature(authorName, authorEmail, DateTimeOffset.Now)
            : repo.Config.BuildSignature(DateTimeOffset.Now)
              ?? throw new InvalidOperationException(
                  "config value 'user.name' was not found - please set user.name and user.email");

        var parents = repo.Info.IsHeadUnborn ? Array.Empty<Commit>() : [repo.Head.Tip!];

        // prettifyMessage: false — 1.7.2 passes the message through untouched, and
        // collapsing blank lines or stripping '#' comments would silently edit a user's text.
        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, message, tree, parents, prettifyMessage: false);

        MoveHeadTo(repo, commit);
        return commit.Sha;
    }

    /// <summary>Advances whatever HEAD points at — the current branch, or HEAD itself when detached.</summary>
    internal static void MoveHeadTo(Repository repo, Commit commit)
    {
        if (repo.Refs["HEAD"] is SymbolicReference symbolic)
        {
            var branch = symbolic.TargetIdentifier;
            if (repo.Refs[branch] is { } existing)
            {
                repo.Refs.UpdateTarget(existing, commit.Id);
            }
            else
            {
                // First commit on an unborn branch: the ref does not exist yet.
                repo.Refs.Add(branch, commit.Id);
            }
        }
        else
        {
            repo.Refs.UpdateTarget(repo.Refs["HEAD"], commit.Id);
        }
    }

    /// <summary>Turns a patch into the shape the frontend renders.</summary>
    private static List<FileDiffInfo> Collect(Patch patch) =>
        patch.Select(entry => new FileDiffInfo(
                entry.OldPath,
                entry.Path,
                StatusLabel(entry.Status),
                UnifiedPatch.Hunks(entry.Patch)))
            .ToList();

    /// <summary>
    /// The delta labels.
    /// </summary>
    /// <remarks>
    /// <c>renamed</c> is live since <c>BUG-GIT-a</c>'s fix turned rename detection on in
    /// <c>FullFile()</c>; <c>copied</c> stays defined but rare — copy detection is off here, as
    /// in git's own defaults. The renderer already maps and colours both
    /// (<c>renderer/src/lib/fileStatus.ts</c>).
    /// </remarks>
    private static string StatusLabel(ChangeKind status) => status switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Modified => "modified",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Copied => "copied",
        ChangeKind.TypeChanged => "typechange",
        ChangeKind.Conflicted => "conflicted",
        ChangeKind.Untracked => "untracked",
        ChangeKind.Ignored => "ignored",
        _ => "unmodified",
    };
}

/// <summary>
/// Reads hunks and line numbers back out of unified-diff text.
/// </summary>
/// <remarks>
/// <para>
/// CodeFlow 1.7.2 walks libgit2's own delta/hunk/line callbacks, which give the origin character
/// and both line numbers directly. LibGit2Sharp does not expose those callbacks at all — the
/// closest it offers is the rendered patch text per file — so the same information is recovered by
/// parsing it. The line numbers are counted from the <c>@@</c> header exactly as libgit2 assigns
/// them: context advances both sides, a deletion only the old side, an addition only the new one.
/// </para>
/// <para>
/// Known difference: libgit2 reports the no-trailing-newline marker as a line with its own origin
/// character, whereas here the <c>\ No newline at end of file</c> line is skipped. It carries no
/// file content, and no consumer keys off it.
/// </para>
/// </remarks>
internal static class UnifiedPatch
{
    public static IReadOnlyList<DiffHunkInfo> Hunks(string? patchText)
    {
        var hunks = new List<DiffHunkInfo>();
        if (string.IsNullOrEmpty(patchText))
        {
            return hunks;
        }

        List<DiffLine>? lines = null;
        var oldLineno = 0;
        var newLineno = 0;

        foreach (var line in patchText.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                lines = [];
                hunks.Add(new DiffHunkInfo(line.TrimEnd(), lines));
                (oldLineno, newLineno) = Start(line);
                continue;
            }

            // Everything before the first @@ is the file header libgit2 never surfaced as a line.
            if (lines is null || line.Length == 0 || line[0] == '\\')
            {
                continue;
            }

            var origin = line[0];
            var content = line[1..].TrimEnd('\r');

            switch (origin)
            {
                case ' ':
                    lines.Add(new DiffLine(" ", content, oldLineno++, newLineno++));
                    break;
                case '-':
                    lines.Add(new DiffLine("-", content, oldLineno++, null));
                    break;
                case '+':
                    lines.Add(new DiffLine("+", content, null, newLineno++));
                    break;
                default:
                    // Not part of a hunk body — the next file's header in a multi-file patch.
                    lines = null;
                    break;
            }
        }

        return hunks;
    }

    /// <summary>The first old and new line number, from <c>@@ -old,count +new,count @@</c>.</summary>
    private static (int Old, int New) Start(string header)
    {
        var old = 1;
        var updated = 1;

        foreach (var part in header.Split(' '))
        {
            if (part.Length < 2 || (part[0] != '-' && part[0] != '+'))
            {
                continue;
            }

            var digits = part[1..];
            var comma = digits.IndexOf(',');
            if (comma >= 0)
            {
                digits = digits[..comma];
            }

            if (!int.TryParse(digits, out var value))
            {
                continue;
            }

            if (part[0] == '-')
            {
                old = value;
            }
            else
            {
                updated = value;
            }
        }

        return (old, updated);
    }
}
