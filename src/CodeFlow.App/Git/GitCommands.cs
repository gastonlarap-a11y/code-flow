using System.Text.Json;
using CodeFlow.Ipc;
using CodeFlow.Storage;
using CodeFlow.Workspaces;

namespace CodeFlow.Git;

/// <summary>
/// The 44 git and checkpoint commands.
/// See <c>docs/business-rules/04-git.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// One registration file for the whole domain, deliberately. This is the file someone opens to
/// find where a command lives; splitting it would mean guessing which piece holds
/// <c>stash_pop</c>. The implementations live one file per git topic, which is where the
/// size actually is.
/// </para>
/// <para>
/// <b>Every synchronous command runs inside <see cref="Task.Run"/>.</b> That is not ceremony:
/// <c>IpcServer.PumpAsync</c> starts each dispatch without awaiting it, but a handler that never
/// reaches a real suspension point runs to completion on the pump's own thread — which is what
/// <c>resize_terminal</c> does, correctly, because resizing a PTY is one cheap call. LibGit2Sharp
/// is synchronous and blocking, and a revwalk over a large history or a diff of a large file is
/// neither cheap nor bounded. There is exactly one RPC connection for the whole application, so
/// blocking the pump would stall the terminal and AI cancellation too, with a symptom that points
/// nowhere near Git. The rule is applied uniformly, including to cheap commands like
/// <c>is_merging</c>: one extra context switch costs nothing, and deciding case by case only has
/// to be wrong once.
/// </para>
/// <para>
/// Known limit, worth stating rather than discovering: the token handed to <see cref="Task.Run"/>
/// prevents the work from starting and is observed after it ends, but it cannot abort a
/// LibGit2Sharp call already in flight. CodeFlow 1.7.2 cannot cancel its synchronous commands
/// either.
/// </para>
/// </remarks>
public static class GitCommands
{
    public static CommandRegistry AddGitCommands(this CommandRegistry registry, GitNetwork network, Database database) =>
        registry
            // ---------- network ----------
            //
            // The only four that are genuinely asynchronous: they run the git binary and stream
            // its output, so they need no Task.Run to stay off the pump thread.
            .Add("git_clone", async (p, ct) =>
            {
                await network.CloneAsync(Arg(p, "url"), Arg(p, "dest"), ct).ConfigureAwait(false);
                return Unit();
            })
            .Add("git_fetch", async (p, ct) =>
            {
                await network.FetchAsync(Arg(p, "repoPath"), OptionalArg(p, "remoteName"), ct).ConfigureAwait(false);
                return Unit();
            })
            .Add("git_pull", async (p, ct) =>
            {
                await network.PullAsync(Arg(p, "repoPath"), ct).ConfigureAwait(false);
                return Unit();
            })
            .Add("git_push", async (p, ct) =>
            {
                await network.PushAsync(Arg(p, "repoPath"), Bool(p, "setUpstream"), ct).ConfigureAwait(false);
                return Unit();
            })
            // ---------- status ----------
            .Add("get_status", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => RepoStatus.GetStatus(repoPath), GitJsonContext.Default.RepoStatusInfo, ct);
            })
            .Add("reset_to_commit", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var oid = Arg(p, "oid");
                var mode = Arg(p, "mode");
                return RunUnit(() => RepoStatus.ResetToCommit(repoPath, oid, mode), ct);
            })

            // ---------- AI checkpoints ----------
            .Add("list_ai_checkpoints", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(
                    () => Checkpoints.List(repoPath), GitJsonContext.Default.IReadOnlyListCheckpointInfo, ct);
            })
            .Add("restore_ai_checkpoint", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var checkpointId = Arg(p, "checkpointId");
                return Run(
                    () => Checkpoints.Restore(repoPath, checkpointId),
                    GitJsonContext.Default.IReadOnlyListString,
                    ct);
            })
            .Add("delete_ai_checkpoint", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var checkpointId = Arg(p, "checkpointId");
                return RunUnit(() => Checkpoints.Remove(repoPath, checkpointId), ct);
            })

            // ---------- history ----------
            .Add("list_commits", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var allRefs = Bool(p, "allRefs");
                var limit = Number(p, "limit");
                return Run(
                    () => CommitGraph.List(repoPath, allRefs, limit), GitJsonContext.Default.IReadOnlyListCommitInfo, ct);
            })
            .Add("list_unpushed_commits", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(
                    () => CommitGraph.Unpushed(repoPath), GitJsonContext.Default.IReadOnlyListCommitInfo, ct);
            })

            // ---------- diffs ----------
            .Add("get_working_diff", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Diff.Working(repoPath), GitJsonContext.Default.IReadOnlyListFileDiffInfo, ct);
            })
            .Add("get_staged_diff", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Diff.Staged(repoPath), GitJsonContext.Default.IReadOnlyListFileDiffInfo, ct);
            })
            .Add("get_commit_diff", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var oid = Arg(p, "oid");
                return Run(() => Diff.Commit(repoPath, oid), GitJsonContext.Default.IReadOnlyListFileDiffInfo, ct);
            })
            .Add("list_commit_files", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var oid = Arg(p, "oid");
                return Run(
                    () => Diff.CommitFiles(repoPath, oid), GitJsonContext.Default.IReadOnlyListCommitFileInfo, ct);
            })
            .Add("get_commit_file_diff", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var oid = Arg(p, "oid");
                var filePath = Arg(p, "filePath");
                var oldPath = OptionalArg(p, "oldPath");
                return Run(
                    () => Diff.CommitFile(repoPath, oid, filePath, oldPath),
                    GitJsonContext.Default.IReadOnlyListFileDiffInfo,
                    ct);
            })

            // ---------- staging ----------
            .Add("stage_file", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var filePath = Arg(p, "filePath");
                return RunUnit(() => Diff.StageFile(repoPath, filePath), ct);
            })
            .Add("stage_all", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return RunUnit(() => Diff.StageAll(repoPath), ct);
            })
            .Add("unstage_file", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var filePath = Arg(p, "filePath");
                return RunUnit(() => Diff.UnstageFile(repoPath, filePath), ct);
            })
            .Add("unstage_all", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return RunUnit(() => Diff.UnstageAll(repoPath), ct);
            })
            .Add("discard_file_changes", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var filePath = Arg(p, "filePath");
                return RunUnit(() => Diff.DiscardFileChanges(repoPath, filePath), ct);
            })
            .Add("discard_all_changes", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return RunUnit(() => Diff.DiscardAllChanges(repoPath), ct);
            })
            .Add("commit", async (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var message = Arg(p, "message");
                var (authorName, authorEmail) = await ResolveAuthor(
                    database, repoPath, OptionalArg(p, "authorName"), OptionalArg(p, "authorEmail"), ct)
                    .ConfigureAwait(false);
                return await Run(
                    () => Diff.CommitIndex(repoPath, message, authorName, authorEmail),
                    GitJsonContext.Default.String,
                    ct).ConfigureAwait(false);
            })

            // ---------- branches ----------
            .Add("list_branches", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Branches.List(repoPath), GitJsonContext.Default.IReadOnlyListBranchInfo, ct);
            })
            .Add("create_branch", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var name = Arg(p, "name");
                var startPoint = OptionalArg(p, "startPoint");
                return RunUnit(() => Branches.Create(repoPath, name, startPoint), ct);
            })
            .Add("delete_branch", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var name = Arg(p, "name");
                var isRemote = Bool(p, "isRemote");
                return RunUnit(() => Branches.Delete(repoPath, name, isRemote), ct);
            })
            .Add("checkout_local_branch", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var name = Arg(p, "name");
                return RunUnit(() => Branches.CheckoutLocal(repoPath, name), ct);
            })
            .Add("checkout_detached", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var refname = Arg(p, "refname");
                return RunUnit(() => Branches.CheckoutDetached(repoPath, refname), ct);
            })
            .Add("checkout_remote_tracking", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var remoteBranch = Arg(p, "remoteBranch");
                return Run(
                    () => Branches.CheckoutRemoteTracking(repoPath, remoteBranch), GitJsonContext.Default.String, ct);
            })

            // ---------- merge and conflicts ----------
            .Add("merge_branch", async (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var branchName = Arg(p, "branchName");
                var (authorName, authorEmail) = await ResolveAuthor(database, repoPath, null, null, ct)
                    .ConfigureAwait(false);
                return await Run(
                    () => Merge.Branch(repoPath, branchName, authorName, authorEmail),
                    GitJsonContext.Default.MergeOutcome,
                    ct).ConfigureAwait(false);
            })
            .Add("is_merging", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Merge.IsMerging(repoPath), GitJsonContext.Default.Boolean, ct);
            })
            .Add("list_conflicts", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Merge.ListConflicts(repoPath), GitJsonContext.Default.IReadOnlyListConflictFile, ct);
            })
            .Add("resolve_conflict_side", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var relPath = Arg(p, "relPath");
                var side = Arg(p, "side");
                return RunUnit(() => Merge.ResolveSide(repoPath, relPath, side), ct);
            })
            .Add("mark_conflict_resolved", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var relPath = Arg(p, "relPath");
                return RunUnit(() => Merge.MarkResolved(repoPath, relPath), ct);
            })
            .Add("complete_merge", async (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var message = Arg(p, "message");
                var (authorName, authorEmail) = await ResolveAuthor(database, repoPath, null, null, ct)
                    .ConfigureAwait(false);
                return await Run(
                    () => Merge.Complete(repoPath, message, authorName, authorEmail),
                    GitJsonContext.Default.String,
                    ct).ConfigureAwait(false);
            })
            .Add("abort_merge", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return RunUnit(() => Merge.Abort(repoPath), ct);
            })

            // ---------- stash ----------
            .Add("list_stashes", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Stash.List(repoPath), GitJsonContext.Default.IReadOnlyListStashInfo, ct);
            })
            .Add("stash_save", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var message = OptionalArg(p, "message");
                var includeUntracked = Bool(p, "includeUntracked");
                return RunUnit(() => Stash.Save(repoPath, message, includeUntracked), ct);
            })
            // Both return their outcome rather than unit: a conflicting apply is not an exception
            // in LibGit2Sharp, so unit would silently read as success (GIT-015).
            .Add("stash_apply", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var index = Number(p, "index");
                return Run(() => Stash.Apply(repoPath, index), GitJsonContext.Default.String, ct);
            })
            .Add("stash_pop", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var index = Number(p, "index");
                return Run(() => Stash.Pop(repoPath, index), GitJsonContext.Default.String, ct);
            })
            .Add("stash_drop", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var index = Number(p, "index");
                return RunUnit(() => Stash.Drop(repoPath, index), ct);
            })
            .Add("rename_stash", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var index = Number(p, "index");
                var newMessage = Arg(p, "newMessage");
                return RunUnit(() => Stash.Rename(repoPath, index, newMessage), ct);
            })

            // ---------- remotes ----------
            .Add("list_remotes", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                return Run(() => Remotes.List(repoPath), GitJsonContext.Default.IReadOnlyListRemoteInfo, ct);
            })
            .Add("set_remote_url", (p, ct) =>
            {
                var repoPath = Arg(p, "repoPath");
                var name = Arg(p, "name");
                var url = Arg(p, "url");
                return RunUnit(() => Remotes.SetUrl(repoPath, name, url), ct);
            })

            // ---------- identity ----------
            .Add("get_git_identity", (_, ct) => Run(Identity.Get, GitJsonContext.Default.GitIdentity, ct))
            .Add("set_git_identity", (p, ct) =>
            {
                var name = Arg(p, "name");
                var email = Arg(p, "email");
                return RunUnit(() => Identity.Set(name, email), ct);
            });

    // ---------- dispatch helpers ----------

    /// <summary>
    /// The author for a commit-creating command: explicit arguments win, then the workspace of the
    /// project registered at <paramref name="repoPath"/>, then <c>(null, null)</c> — which the git
    /// layer reads as "use the repo's configured signature" (GIT-028/GIT-036, WS-008).
    /// </summary>
    private static async ValueTask<(string? Name, string? Email)> ResolveAuthor(
        Database database,
        string repoPath,
        string? explicitName,
        string? explicitEmail,
        CancellationToken cancellationToken) =>
        explicitName is not null && explicitEmail is not null
            ? (explicitName, explicitEmail)
            : await database.ReadAsync(c => WorkspaceStore.ResolveGitIdentity(c, repoPath), cancellationToken)
                .ConfigureAwait(false);

    /// <summary>Runs blocking work off the pump thread and serialises what it returned.</summary>
    private static async ValueTask<ReadOnlyMemory<byte>> Run<T>(
        Func<T> work, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var result = await Task.Run(work, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(result, type);
    }

    /// <summary>Runs blocking work off the pump thread for a command that returns nothing.</summary>
    private static async ValueTask<ReadOnlyMemory<byte>> RunUnit(Action work, CancellationToken cancellationToken)
    {
        await Task.Run(work, cancellationToken).ConfigureAwait(false);
        return Unit();
    }

    // ---------- argument helpers ----------
    //
    // Arguments are read by their camelCase names: that is what the renderer sends. Returned
    // shapes are snake_case — see GitJsonContext.

    private static string Arg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"missing required parameter '{name}'");

    /// <summary>An argument the renderer sends as <c>null</c> when the user left it out.</summary>
    private static string? OptionalArg(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static bool Bool(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException($"missing required parameter '{name}'");

    private static ReadOnlyMemory<byte> Unit() => "null"u8.ToArray();
}
