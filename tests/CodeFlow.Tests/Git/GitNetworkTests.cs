using System.Linq;
using System.Text.Json;
using CodeFlow.Git;
using CodeFlow.Tests.Ai;
using LibGit2Sharp;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// The four streamed operations and their two events (GIT-034).
/// </summary>
/// <remarks>
/// Everything here runs against a second repository on disk used as the remote, so no test needs
/// the network or a credential. What is being checked is the framing — a <c>git:progress</c> per
/// line of both streams, one <c>git:done</c>, and the exact fallback message — not git's own
/// transfer behaviour.
/// </remarks>
public sealed class GitNetworkTests
{
    [Fact]
    public async Task Cloning_streams_progress_and_reports_success()
    {
        using var origin = new TempRepo();
        origin.Write("a.txt", "one\n");
        origin.Commit("initial", "a.txt");

        var (publish, recorded) = Recorder.Create();
        var network = new GitNetwork(publish);

        var destination = Path.Combine(Path.GetTempPath(), $"codeflow-clone-{Guid.NewGuid():N}");
        try
        {
            await network.CloneAsync(origin.Path, destination, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(destination, "a.txt")));

            // git writes its progress to stderr, and both streams feed the same event — treating
            // stderr as an error channel would leave the progress log empty.
            var progress = recorded.Payloads("git:progress");
            Assert.NotEmpty(progress);
            Assert.All(progress, p => Assert.Equal("clone", p.GetProperty("op").GetString()));

            var done = Assert.Single(recorded.Payloads("git:done"));
            Assert.Equal("clone", done.GetProperty("op").GetString());
            Assert.True(done.GetProperty("success").GetBoolean());
            Assert.Equal("ok", done.GetProperty("message").GetString());
        }
        finally
        {
            Delete(destination);
        }
    }

    [Fact]
    public async Task A_failed_operation_reports_the_reason_on_both_paths()
    {
        var (publish, recorded) = Recorder.Create();
        var network = new GitNetwork(publish);

        var destination = Path.Combine(Path.GetTempPath(), $"codeflow-clone-{Guid.NewGuid():N}");
        var missing = Path.Combine(Path.GetTempPath(), $"codeflow-missing-{Guid.NewGuid():N}");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => network.CloneAsync(missing, destination, TestContext.Current.CancellationToken));

        var done = Assert.Single(recorded.Payloads("git:done"));
        Assert.False(done.GetProperty("success").GetBoolean());

        var message = done.GetProperty("message").GetString()!;
        Assert.NotEqual("ok", message);

        // Related but not identical: the rejected promise adds the operation prefix, the event
        // carries the bare detail.
        Assert.Equal($"git clone failed: {message}", error.Message);
    }

    [Fact]
    public async Task Fetch_defaults_to_origin_and_updates_the_remote_tracking_refs()
    {
        using var origin = new TempRepo();
        origin.Write("a.txt", "one\n");
        origin.Commit("initial", "a.txt");

        using var clone = new TempRepo();
        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
        }

        var (publish, recorded) = Recorder.Create();
        var network = new GitNetwork(publish);

        await network.FetchAsync(clone.Path, remote: null, TestContext.Current.CancellationToken);

        Assert.Contains(Branches.List(clone.Path), b => b.IsRemote);
        Assert.All(recorded.Payloads("git:progress"), p => Assert.Equal("fetch", p.GetProperty("op").GetString()));
        Assert.Single(recorded.Payloads("git:done"));
    }

    [Fact]
    public async Task Pulling_a_divergent_branch_completes_the_merge_without_an_editor()
    {
        using var origin = new TempRepo();
        origin.Write("a.txt", "one\n");
        origin.Commit("initial", "a.txt");

        var (publish, recorded) = Recorder.Create();
        var network = new GitNetwork(publish);

        var destination = Path.Combine(Path.GetTempPath(), $"codeflow-pull-{Guid.NewGuid():N}");
        try
        {
            await network.CloneAsync(origin.Path, destination, TestContext.Current.CancellationToken);

            var clone = new TempRepo(destination);

            // A local commit that origin doesn't have, and a separate origin commit the clone
            // doesn't have — non-conflicting files, so the merge is clean and the only thing left
            // to do is write the merge commit's message.
            clone.Write("clone-only.txt", "mine\n");
            clone.Commit("clone-only", "clone-only.txt");
            origin.Write("origin-only.txt", "theirs\n");
            origin.Commit("origin-only", "origin-only.txt");

            using (var repo = clone.Open())
            {
                // Proves --no-edit: if git ever tried to invoke an editor, it would try to exec
                // this nonexistent path and the whole pull would fail immediately — a hard, fast,
                // cross-platform-safe signal, with no risk of the test hanging on a TTY prompt.
                repo.Config.Set("core.editor", "/no/such/editor-binary", ConfigurationLevel.Local);

                // Without an explicit reconciliation strategy, a git whose global config has none
                // configured either (exactly this test environment) refuses a divergent pull outright
                // ("Need to specify how to reconcile divergent branches") — a real, separate gate
                // this test isn't about, so it's pinned to the historical default (merge).
                repo.Config.Set("pull.rebase", false, ConfigurationLevel.Local);
            }

            await network.PullAsync(destination, TestContext.Current.CancellationToken);

            using (var merged = clone.Open())
            {
                Assert.Equal(2, merged.Head.Tip.Parents.Count());
            }

            Assert.True(clone.Exists("clone-only.txt"));
            Assert.True(clone.Exists("origin-only.txt"));

            // Shares `recorded` with the clone above, so it carries clone's own "git:done" too —
            // filtered to pull's.
            var done = Assert.Single(recorded.Payloads("git:done"), p => p.GetProperty("op").GetString() == "pull");
            Assert.True(done.GetProperty("success").GetBoolean());
        }
        finally
        {
            Delete(destination);
        }
    }

    [Fact]
    public async Task Pushing_reaches_the_other_repository()
    {
        // A bare repository as the remote: pushing to a checked-out branch is refused by git
        // itself, which would test the wrong thing.
        var bare = Path.Combine(Path.GetTempPath(), $"codeflow-bare-{Guid.NewGuid():N}");
        LibGit2Sharp.Repository.Init(bare, isBare: true);

        try
        {
            using var repo = new TempRepo();
            repo.Write("a.txt", "one\n");
            repo.Commit("initial", "a.txt");

            using (var handle = repo.Open())
            {
                handle.Network.Remotes.Add("origin", bare);
            }

            var (publish, recorded) = Recorder.Create();
            var network = new GitNetwork(publish);

            await network.PushAsync(repo.Path, setUpstream: true, TestContext.Current.CancellationToken);

            using var remote = new LibGit2Sharp.Repository(bare);
            Assert.NotEmpty(remote.Branches);

            var done = Assert.Single(recorded.Payloads("git:done"));
            Assert.Equal("push", done.GetProperty("op").GetString());
            Assert.True(done.GetProperty("success").GetBoolean());

            // -u wrote the upstream, so the branch now has one to compare against.
            Assert.Contains(Branches.List(repo.Path), b => !b.IsRemote && b.Upstream is not null);
        }
        finally
        {
            Delete(bare);
        }
    }

    [Fact]
    public async Task Pushing_upstream_from_a_detached_head_refuses()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        var first = repo.Commit("initial", "a.txt");

        using (var handle = repo.Open())
        {
            handle.Refs.UpdateTarget("HEAD", first.Sha);
        }

        var (publish, _) = Recorder.Create();
        var network = new GitNetwork(publish);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => network.PushAsync(repo.Path, setUpstream: true, TestContext.Current.CancellationToken));

        Assert.Equal("cannot push -u from a detached HEAD", error.Message);
    }

    [Fact]
    public async Task Every_line_of_both_streams_becomes_its_own_event()
    {
        using var origin = new TempRepo();
        origin.Write("a.txt", "one\n");
        origin.Commit("initial", "a.txt");

        using var clone = new TempRepo();
        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
        }

        var (publish, recorded) = Recorder.Create();
        var network = new GitNetwork(publish);

        // --verbose guarantees output, so an empty progress log would be a real failure rather
        // than git happening to stay quiet.
        await network.FetchRefspecAsync(
            clone.Path, "origin", "+refs/heads/*:refs/remotes/origin/*", TestContext.Current.CancellationToken);

        var progress = recorded.Payloads("git:progress");
        Assert.All(progress, p =>
        {
            Assert.Equal("fetch", p.GetProperty("op").GetString());

            // One event per line: no payload carries an embedded newline.
            Assert.DoesNotContain('\n', p.GetProperty("line").GetString()!);
        });
    }

    [Fact]
    public void The_event_payloads_are_spelled_the_way_the_renderer_reads_them()
    {
        // domain.ts declares { op, line } and { op, success, message }. All three names happen to
        // be identical in either casing, so this pins the shape rather than the policy.
        using var progress = JsonSerializer.SerializeToDocument(
            new GitProgressEvent("clone", "Receiving objects"), GitJsonContext.Default.GitProgressEvent);
        using var done = JsonSerializer.SerializeToDocument(
            new GitDoneEvent("clone", true, "ok"), GitJsonContext.Default.GitDoneEvent);

        Assert.Equal(["op", "line"], progress.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["op", "success", "message"], done.RootElement.EnumerateObject().Select(p => p.Name));
    }

    private static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
