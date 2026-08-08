using CodeFlow.Git;
using LibGit2Sharp;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// History walks (GIT-020, GIT-021).
/// </summary>
public sealed class CommitGraphTests
{
    [Fact]
    public void Commits_come_back_newest_first_with_their_parents_and_refs()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        var first = repo.Commit("first", "a.txt");
        repo.Write("a.txt", "two\n");
        var second = repo.Commit("second", "a.txt");

        string branch;
        using (var handle = repo.Open())
        {
            branch = handle.Head.FriendlyName;
            handle.ApplyTag("v1.0", first.Sha);
        }

        var commits = CommitGraph.List(repo.Path, allRefs: false, limit: 10);

        Assert.Equal([second.Sha, first.Sha], commits.Select(c => c.Id));
        Assert.Equal(["second", "first"], commits.Select(c => c.Summary));
        Assert.Equal([first.Sha], commits[0].ParentIds);
        Assert.Empty(commits[1].ParentIds);

        Assert.Equal(second.Sha[..7], commits[0].ShortId);
        Assert.Equal("CodeFlow Test", commits[0].AuthorName);
        Assert.Equal("test@codeflow.local", commits[0].AuthorEmail);
        Assert.Equal(CommitterTime(repo, second), commits[0].Timestamp);

        // Branch names and tag names both land on whatever they point at.
        Assert.Equal([branch], commits[0].Refs);
        Assert.Equal(["v1.0"], commits[1].Refs);
    }

    /// <summary>The commit time — which is the committer's, not the author's.</summary>
    private static long CommitterTime(TempRepo repo, ObjectId id)
    {
        using var handle = repo.Open();
        return handle.Lookup<Commit>(id).Committer.When.ToUnixTimeSeconds();
    }

    [Fact]
    public void A_limit_of_zero_returns_nothing_rather_than_failing()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first", "a.txt");

        Assert.Empty(CommitGraph.List(repo.Path, allRefs: false, limit: 0));
    }

    [Fact]
    public void A_repository_with_no_commits_walks_nothing()
    {
        using var repo = new TempRepo();

        Assert.Empty(CommitGraph.List(repo.Path, allRefs: false, limit: 10));
        Assert.Empty(CommitGraph.Unpushed(repo.Path));
    }

    [Fact]
    public void Head_only_misses_what_another_branch_holds_and_all_refs_finds_it()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("shared", "a.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            Commands.Checkout(handle, handle.CreateBranch("side"));
        }

        repo.Write("b.txt", "only on side\n");
        var sideOnly = repo.Commit("side work", "b.txt");
        Branches.CheckoutLocal(repo.Path, baseBranch);

        Assert.DoesNotContain(
            CommitGraph.List(repo.Path, allRefs: false, limit: 10), c => c.Id == sideOnly.Sha);
        Assert.Contains(
            CommitGraph.List(repo.Path, allRefs: true, limit: 10), c => c.Id == sideOnly.Sha);
    }

    [Fact]
    public void Unpushed_is_empty_without_an_upstream()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first", "a.txt");

        Assert.Empty(CommitGraph.Unpushed(repo.Path));
    }

    [Fact]
    public void Unpushed_is_empty_while_detached()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        var first = repo.Commit("first", "a.txt");

        using (var handle = repo.Open())
        {
            handle.Refs.UpdateTarget("HEAD", first.Sha);
        }

        Assert.Empty(CommitGraph.Unpushed(repo.Path));
    }

    [Fact]
    public void Unpushed_is_what_head_has_and_its_upstream_does_not()
    {
        var origin = new TempRepo();
        using var _origin = origin;
        origin.Write("a.txt", "one\n");
        origin.Commit("shared", "a.txt");

        using var clone = new TempRepo();
        string branch;
        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
            Commands.Fetch(handle, "origin", [], null, null);

            using var originHandle = origin.Open();
            branch = originHandle.Head.FriendlyName;

            var remote = handle.Branches[$"origin/{branch}"];
            var local = handle.CreateBranch(branch, remote.Tip);
            handle.Branches.Update(local, b => b.TrackedBranch = remote.CanonicalName);
            Commands.Checkout(handle, local);
        }

        Assert.Empty(CommitGraph.Unpushed(clone.Path));

        clone.Write("local.txt", "not pushed\n");
        var local1 = clone.Commit("local one", "local.txt");
        clone.Write("local.txt", "still not pushed\n");
        var local2 = clone.Commit("local two", "local.txt");

        // Newest first, and only the two the upstream has never seen.
        Assert.Equal([local2.Sha, local1.Sha], CommitGraph.Unpushed(clone.Path).Select(c => c.Id));
    }
}
