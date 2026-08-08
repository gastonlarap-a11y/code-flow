using LibGit2Sharp;

namespace CodeFlow.Git;

/// <summary>One checkpoint and what restoring it would put back right now.</summary>
public sealed record CheckpointInfo(
    string Id,
    string Kind,
    long CreatedAt,
    IReadOnlyList<string> ChangedPaths);

/// <summary>
/// Undo for AI runs (GIT-022 … GIT-025).
/// </summary>
/// <remarks>
/// <para>
/// Before an AI action that can write to the working tree, the tree is snapshotted into a commit
/// parked outside <c>refs/heads</c>, so "undo what the agent just did" is a real operation. Two
/// properties drive the design and both are load-bearing:
/// </para>
/// <para>
/// <b>It must not disturb git's own state.</b> Nothing lands on a branch, HEAD never moves, and
/// the staging area is left exactly as the user had it — <c>git status</c> reads the same before
/// and after.
/// </para>
/// <para>
/// <b>Restoring is per file.</b> Rolling the whole tree back would also discard whatever the user
/// typed while the agent worked, so the caller sees which paths differ and only those are put
/// back. An undo button, not a time machine.
/// </para>
/// </remarks>
public static class Checkpoints
{
    /// <summary>The ref namespace, verbatim (GIT-023).</summary>
    private const string RefPrefix = "refs/codeflow/checkpoints/";

    /// <summary>
    /// How many checkpoints a repository keeps.
    /// </summary>
    /// <remarks>
    /// Snapshots are cheap because git deduplicates every unchanged blob, but they are refs that
    /// would otherwise pile up forever and keep their objects from ever being collected — and
    /// nobody undoes the fortieth-most-recent AI run.
    /// </remarks>
    private const int MaxCheckpoints = 20;

    /// <summary>Snapshots the working tree and returns the new checkpoint's id (GIT-022).</summary>
    /// <param name="kind">
    /// A stable action key such as <c>chat</c> or <c>fix-finding</c>, never a sentence: the UI is
    /// bilingual, so the wording belongs to the frontend's translations and only this key crosses
    /// the boundary.
    /// </param>
    public static string Create(string repoPath, string kind)
    {
        using var repo = RepoStatus.Open(repoPath);

        var tree = SnapshotTree(repo);
        var signature = Signature(repo);

        // Parented on HEAD so the checkpoint reads as a commit on top of the current state; a
        // repository with no commits gets a parentless one rather than no protection at all.
        var parents = repo.Info.IsHeadUnborn ? Array.Empty<Commit>() : [repo.Head.Tip!];

        // Two checkpoints taken in the same second are told apart only by the random suffix — the
        // id is not required to sort by time on its own.
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}"[..^24];


        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, kind, tree, parents, prettifyMessage: false);

        repo.Refs.Add(RefPrefix + id, commit.Id);
        Prune(repo);

        return id;
    }

    /// <summary>Every checkpoint, newest first.</summary>
    /// <remarks>
    /// <c>ChangedPaths</c> is computed fresh against the current working tree on every call, so it
    /// is always "what restoring right now would touch" and never a record of what changed when
    /// the checkpoint was taken.
    /// </remarks>
    public static IReadOnlyList<CheckpointInfo> List(string repoPath)
    {
        using var repo = RepoStatus.Open(repoPath);

        return repo.Refs.FromGlob(RefPrefix + "*")
            .Select(reference => (Reference: reference, Commit: reference.ResolveToDirectReference()?.Target?.Peel<Commit>()))
            .Where(x => x.Commit is not null)
            .Select(x => new CheckpointInfo(
                x.Reference.CanonicalName[RefPrefix.Length..],
                x.Commit!.MessageShort,
                x.Commit.Committer.When.ToUnixTimeSeconds(),
                ChangedPaths(repo, x.Commit)))
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Writes every differing path back to its snapshotted content and deletes the ones the run
    /// created, returning what it touched (GIT-024).
    /// </summary>
    /// <remarks>
    /// Blobs are written to the working tree directly rather than checked out, so the index and
    /// HEAD are untouched: a file that was staged before the restore stays staged, now with stale
    /// content, exactly as any manual edit after staging would leave it.
    /// </remarks>
    public static IReadOnlyList<string> Restore(string repoPath, string checkpointId)
    {
        using var repo = RepoStatus.Open(repoPath);

        var commit = Read(repo, checkpointId);
        var workdir = repo.Info.WorkingDirectory ?? throw new InvalidOperationException("bare repository");
        var paths = ChangedPaths(repo, commit);

        foreach (var relative in paths)
        {
            var target = System.IO.Path.Combine(workdir, relative);

            if (commit.Tree[relative]?.Target is Blob blob)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);

                using var content = blob.GetContentStream();
                using var file = File.Create(target);
                content.CopyTo(file);
            }
            else
            {
                // Absent from the snapshot: the run created it, so undoing means removing it.
                File.Delete(target);
            }
        }

        return paths;
    }

    /// <summary>Forgets a checkpoint (GIT-025). Deleting one that is not there is not an error.</summary>
    /// <remarks>Its objects stay in the database until git's own gc reaps them.</remarks>
    public static void Remove(string repoPath, string checkpointId)
    {
        using var repo = RepoStatus.Open(repoPath);

        if (repo.Refs[RefPrefix + checkpointId] is { } reference)
        {
            repo.Refs.Remove(reference);
        }
    }

    /// <summary>
    /// Deletes a checkpoint only when nothing differs from it — the run it protected changed
    /// nothing (GIT-025).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Remove"/>, a missing id <b>is</b> an error here. The asymmetry is
    /// deliberate: this one is called automatically after a run, where a missing checkpoint means
    /// something went wrong, while the other is a user pressing delete.
    /// </remarks>
    public static bool RemoveIfUnchanged(string repoPath, string checkpointId)
    {
        using (var repo = RepoStatus.Open(repoPath))
        {
            if (ChangedPaths(repo, Read(repo, checkpointId)).Count > 0)
            {
                return false;
            }
        }

        Remove(repoPath, checkpointId);
        return true;
    }

    /// <summary>
    /// Builds a tree holding the current working tree, without touching the real index (GIT-022).
    /// </summary>
    /// <remarks>
    /// CodeFlow 1.7.2 attaches a fresh in-memory <c>git2::Index</c> to its repository handle and
    /// writes a tree from that. LibGit2Sharp's <see cref="Index"/> has no public constructor and
    /// cannot be swapped, so the tree is built directly instead: HEAD's tree as the base, then
    /// every path git reports as changed — staged, unstaged or untracked, it makes no difference —
    /// replaced with what is on disk. That the on-disk index is never opened is not incidental;
    /// it is what the <c>snapshot-leaves-index-alone</c> scenario checks.
    /// </remarks>
    private static Tree SnapshotTree(Repository repo)
    {
        var workdir = repo.Info.WorkingDirectory ?? throw new InvalidOperationException("bare repository");

        var definition = repo.Info.IsHeadUnborn
            ? new TreeDefinition()
            : TreeDefinition.From(repo.Head.Tip!.Tree);

        foreach (var entry in repo.RetrieveStatus(RepoStatus.StatusRequest()))
        {
            var full = System.IO.Path.Combine(workdir, entry.FilePath);

            if (File.Exists(full))
            {
                definition.Add(entry.FilePath, repo.ObjectDatabase.CreateBlob(full), ModeFor(full, definition[entry.FilePath]));
            }
            else
            {
                // Deleted, or replaced by a directory. The snapshot has to record its absence, or
                // restoring would resurrect a file the user themselves removed.
                definition.Remove(entry.FilePath);
            }
        }

        return repo.ObjectDatabase.CreateTree(definition);
    }

    /// <summary>
    /// The file mode to record, preserving the executable bit.
    /// </summary>
    /// <remarks>
    /// <c>CreateBlob</c> does not carry it and <c>TreeDefinition.Add</c> will not infer it, so a
    /// script would silently come back non-executable after a restore. Windows has no such bit, so
    /// whatever the tree already recorded is kept instead of being flattened.
    /// </remarks>
    private static Mode ModeFor(string fullPath, TreeEntryDefinition? existing)
    {
        if (OperatingSystem.IsWindows())
        {
            return existing?.Mode ?? Mode.NonExecutableFile;
        }

        return File.GetUnixFileMode(fullPath).HasFlag(UnixFileMode.UserExecute)
            ? Mode.ExecutableFile
            : Mode.NonExecutableFile;
    }

    /// <summary>Paths whose current content differs from the checkpoint's, sorted and deduplicated.</summary>
    private static List<string> ChangedPaths(Repository repo, Commit commit)
    {
        // Similarity stays None here even though BUG-GIT-a's fix enabled renames on the
        // user-facing diffs: this list feeds the restore, whose `Path ?? OldPath` projection
        // needs the delete AND the add of a rename as two entries — a single Renamed entry
        // would drop the old path from the changed set and the restore would miss it.
        var changes = repo.Diff.Compare<TreeChanges>(
            commit.Tree,
            DiffTargets.Index | DiffTargets.WorkingDirectory,
            null,
            null,
            new CompareOptions { Similarity = SimilarityOptions.None });

        return changes
            .Select(change => (change.Path ?? change.OldPath).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static Commit Read(Repository repo, string checkpointId) =>
        repo.Refs[RefPrefix + checkpointId]?.ResolveToDirectReference()?.Target?.Peel<Commit>()
        ?? throw new InvalidOperationException($"checkpoint '{checkpointId}' no longer exists");

    /// <summary>
    /// Drops the oldest checkpoints past <see cref="MaxCheckpoints"/>, best-effort.
    /// </summary>
    /// <remarks>
    /// Failing to prune is never a reason to fail the snapshot the user is actually protected by,
    /// so every error here is swallowed. It runs only as a side effect of creating one: deleting a
    /// checkpoint by hand never triggers it, and there is no scheduled prune.
    /// </remarks>
    private static void Prune(Repository repo)
    {
        try
        {
            var dated = repo.Refs.FromGlob(RefPrefix + "*")
                .Select(r => (Reference: r, Commit: r.ResolveToDirectReference()?.Target?.Peel<Commit>()))
                .Where(x => x.Commit is not null)
                .OrderByDescending(x => x.Commit!.Committer.When.ToUnixTimeSeconds())
                .ToList();

            foreach (var (reference, _) in dated.Skip(MaxCheckpoints))
            {
                repo.Refs.Remove(reference);
            }
        }
        catch (LibGit2SharpException)
        {
            // Best-effort by design.
        }
    }

    /// <summary>
    /// The snapshot signature: the repository's own identity, or a fallback.
    /// </summary>
    /// <remarks>
    /// A repository with no configured <c>user.name</c> must still be protected, so this cannot be
    /// allowed to fail the way committing does.
    /// </remarks>
    private static Signature Signature(Repository repo) =>
        repo.Config.BuildSignature(DateTimeOffset.Now)
        ?? new Signature("CodeFlow", "codeflow@local", DateTimeOffset.Now);
}
