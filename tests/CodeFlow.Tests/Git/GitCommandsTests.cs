using System.Text.Json;
using CodeFlow.Git;
using CodeFlow.Ipc;
using CodeFlow.Tests.Ai;
using CodeFlow.Tests.Workspaces;
using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// The command surface itself: that all 46 names exist, spelled exactly as the contract says, and
/// that errors reach the frontend unwrapped.
/// </summary>
public sealed class GitCommandsTests : IDisposable
{
    // The registry needs a database since commit-creating commands resolve the workspace
    // identity through it (WS-008). One per test class, like TempDatabase's other users.
    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();
    /// <summary>
    /// Every command this domain owns, from <c>01-ipc-surface.md</c>.
    /// </summary>
    /// <remarks>
    /// 41 from the implementation plus 3 from the implementation. Written
    /// out rather than derived: a typo in a name is invisible until the feature is used in the
    /// real app, where it surfaces as "unknown command" and nothing else.
    /// </remarks>
    private static readonly string[] Expected =
    [
        "get_status", "list_commits", "list_unpushed_commits", "list_branches", "create_branch",
        "delete_branch", "checkout_local_branch", "checkout_detached", "checkout_remote_tracking",
        "list_stashes", "stash_save", "stash_apply", "stash_pop", "stash_drop", "rename_stash",
        "get_working_diff", "get_staged_diff", "get_commit_diff", "list_commit_files",
        "get_commit_file_diff", "stage_file", "stage_all",
        "unstage_file", "unstage_all", "discard_file_changes", "discard_all_changes", "commit",
        "reset_to_commit", "list_remotes", "set_remote_url", "get_git_identity", "set_git_identity",
        "merge_branch", "is_merging", "list_conflicts", "resolve_conflict_side",
        "mark_conflict_resolved", "complete_merge", "abort_merge", "git_clone", "git_fetch",
        "git_pull", "git_push", "list_ai_checkpoints", "restore_ai_checkpoint",
        "delete_ai_checkpoint",
    ];

    [Fact]
    public void All_forty_six_commands_are_registered_under_their_contract_names()
    {
        var registry = Registry();

        Assert.Equal(46, Expected.Length);
        Assert.Equal(
            Expected.OrderBy(n => n, StringComparer.Ordinal),
            registry.Names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    // One per family that opens a repository. A path that is not a repository fails the same way
    // everywhere, which is what makes this cheap to assert across the whole surface.
    [InlineData("get_status")]
    [InlineData("list_branches")]
    [InlineData("list_stashes")]
    [InlineData("get_working_diff")]
    [InlineData("get_staged_diff")]
    [InlineData("list_remotes")]
    [InlineData("is_merging")]
    [InlineData("list_conflicts")]
    [InlineData("list_ai_checkpoints")]
    [InlineData("unstage_all")]
    [InlineData("discard_all_changes")]
    public async Task An_error_reaches_the_caller_verbatim_and_unprefixed(string command)
    {
        // GIT-031: CHECKOUT_CONFLICT is the only prefix in this domain. Everything else is
        // libgit2's own message, forwarded by IpcServer without decoration — so a UI parser has
        // exactly one string contract to know about, and no more.
        var registry = Registry();
        Assert.True(registry.TryGet(command, out var handler));

        var notARepository = Path.Combine(Path.GetTempPath(), $"codeflow-not-a-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notARepository);

        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await handler(Args(new { repoPath = notARepository }), TestContext.Current.CancellationToken));

            Assert.DoesNotContain(Branches.CheckoutConflictPrefix, error.Message, StringComparison.Ordinal);
            Assert.NotEmpty(error.Message);
        }
        finally
        {
            Directory.Delete(notARepository, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_argument_is_named_in_the_error()
    {
        var registry = Registry();
        Assert.True(registry.TryGet("get_status", out var handler));

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await handler(Args(new { }), TestContext.Current.CancellationToken));

        Assert.Equal("missing required parameter 'repoPath'", error.Message);
    }

    [Fact]
    public async Task Arguments_are_read_by_the_camel_case_names_the_renderer_sends()
    {
        // The asymmetry that governs this whole feature: arguments arrive camelCase because the renderer
        // translated them, while returned shapes stay snake_case because the wire policy does not.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial", "a.txt");
        repo.Write("a.txt", "two\n");

        var registry = Registry();
        Assert.True(registry.TryGet("get_status", out var handler));

        var result = await handler(Args(new { repoPath = repo.Path }), TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(result);
        var names = payload.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(
            ["staged", "unstaged", "untracked", "conflicted", "current_branch", "is_detached"], names);
        Assert.Equal("modified", payload.RootElement.GetProperty("unstaged")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_commit_file_commands_read_their_arguments_and_take_an_absent_old_path()
    {
        // GIT-035. `oldPath` is left out here exactly as the renderer leaves it out for a file that
        // was not renamed — `OptionalArg` has to read that as null rather than as a missing argument.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        var first = repo.Commit("first", "a.txt");

        var registry = Registry();
        Assert.True(registry.TryGet("list_commit_files", out var listFiles));
        Assert.True(registry.TryGet("get_commit_file_diff", out var fileDiff));

        var listed = await listFiles(
            Args(new { repoPath = repo.Path, oid = first.Sha }), TestContext.Current.CancellationToken);

        using var files = JsonDocument.Parse(listed);
        var entry = files.RootElement[0];
        Assert.Equal(["old_path", "new_path", "status"], entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal("a.txt", entry.GetProperty("new_path").GetString());
        Assert.Equal("added", entry.GetProperty("status").GetString());

        var patched = await fileDiff(
            Args(new { repoPath = repo.Path, oid = first.Sha, filePath = "a.txt" }),
            TestContext.Current.CancellationToken);

        using var diff = JsonDocument.Parse(patched);
        Assert.Equal("a.txt", diff.RootElement[0].GetProperty("new_path").GetString());
        Assert.NotEmpty(diff.RootElement[0].GetProperty("hunks").EnumerateArray());
    }

    [Fact]
    public async Task Commit_signs_with_the_workspace_identity_of_the_project_at_the_repo_path()
    {
        // WS-008: the workspace override beats the repo's configured user.name/user.email, and the
        // resolution happens inside the command — the renderer sent no author arguments.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Stage("a.txt");

        var workspace = _database.Use(c => WorkspaceStore.Create(c, "Work", "folder", "#111111"));
        _database.Do(c =>
        {
            ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id) with { LocalPath = repo.Path });
            WorkspaceStore.UpdateGitIdentity(c, workspace.Id, "Work Person", "work@company.com");
        });

        var registry = Registry();
        Assert.True(registry.TryGet("commit", out var handler));
        await handler(
            Args(new { repoPath = repo.Path, message = "from the workspace" }),
            TestContext.Current.CancellationToken);

        using var handle = repo.Open();
        Assert.Equal("Work Person", handle.Head.Tip.Author.Name);
        Assert.Equal("work@company.com", handle.Head.Tip.Author.Email);
        Assert.Equal("Work Person", handle.Head.Tip.Committer.Name);
    }

    [Fact]
    public async Task An_explicit_author_still_wins_over_the_workspace_identity()
    {
        // The precedence's top tier: explicit arguments predate WS-008 and must keep working.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Stage("a.txt");

        var workspace = _database.Use(c => WorkspaceStore.Create(c, "Work", "folder", "#111111"));
        _database.Do(c =>
        {
            ProjectStore.Create(c, WorkspaceStoreTests.NewProjectIn(workspace.Id) with { LocalPath = repo.Path });
            WorkspaceStore.UpdateGitIdentity(c, workspace.Id, "Work Person", "work@company.com");
        });

        var registry = Registry();
        Assert.True(registry.TryGet("commit", out var handler));
        await handler(
            Args(new
            {
                repoPath = repo.Path,
                message = "explicit author",
                authorName = "Explicit Person",
                authorEmail = "explicit@example.com",
            }),
            TestContext.Current.CancellationToken);

        using var handle = repo.Open();
        Assert.Equal("Explicit Person", handle.Head.Tip.Author.Name);
        Assert.Equal("explicit@example.com", handle.Head.Tip.Author.Email);
    }

    [Fact]
    public async Task Commit_falls_back_to_the_repo_signature_when_no_project_matches_the_path()
    {
        // The pre-WS-008 behaviour, untouched: no author arguments and no registered project means
        // the repo's own configured identity signs (GIT-028).
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Stage("a.txt");

        var registry = Registry();
        Assert.True(registry.TryGet("commit", out var handler));
        await handler(
            Args(new { repoPath = repo.Path, message = "global fallback" }),
            TestContext.Current.CancellationToken);

        using var handle = repo.Open();
        Assert.Equal("CodeFlow Test", handle.Head.Tip.Author.Name);
        Assert.Equal("test@codeflow.local", handle.Head.Tip.Author.Email);
    }

    private CommandRegistry Registry()
    {
        var (publish, _) = Recorder.Create();
        return new CommandRegistry().AddGitCommands(new GitNetwork(publish), _database.Handle);
    }

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToDocument(value, value.GetType()).RootElement.Clone();
}
