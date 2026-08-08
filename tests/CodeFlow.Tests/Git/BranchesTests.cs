using CodeFlow.Git;
using LibGit2Sharp;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// Branch listing, creation, deletion and the three checkouts (GIT-004 … GIT-009).
/// </summary>
public sealed class BranchesTests
{
    private const string Fixture = "git_branch.vectors.json";

    /// <summary>
    /// 1.7.2's own fixture: two branches whose tip differs on the
    /// same file, left checked out on the base branch.
    /// </summary>
    /// <remarks>
    /// The base branch's name is captured rather than assumed — <c>init.defaultBranch</c> is a
    /// host setting, and 1.7.2 is careful not to hardcode <c>main</c> either.
    /// </remarks>
    private static (TempRepo Repo, string Base) Fork()
    {
        var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial", "a.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            var feature = handle.CreateBranch("feature");
            Commands.Checkout(handle, feature);
        }

        repo.Write("a.txt", "feature\n");
        repo.Commit("feature edit", "a.txt");

        Branches.CheckoutLocal(repo.Path, baseBranch);
        return (repo, baseBranch);
    }

    [Fact]
    public void Checkout_blocked_by_uncommitted_changes_is_tagged_for_the_ui()
    {
        // git_branch.vectors.json#checkout-blocked-by-uncommitted-changes
        var (repo, _) = Fork();
        using var _repo = repo;

        repo.Write("a.txt", "uncommitted work\n");

        var error = Assert.ThrowsAny<Exception>(() => Branches.CheckoutLocal(repo.Path, "feature"));

        Assert.True(GitFixtures.Bool(Fixture, "checkout-blocked-by-uncommitted-changes", "error"));
        Assert.StartsWith(
            GitFixtures.String(Fixture, "checkout-blocked-by-uncommitted-changes", "error_starts_with"),
            error.Message,
            StringComparison.Ordinal);

        // Only the prefix is the contract; what follows is libgit2's own English message, which
        // 1.7.2's test deliberately does not pin either.
        Assert.Equal(Branches.CheckoutConflictPrefix, "CHECKOUT_CONFLICT: ");
    }

    [Fact]
    public void A_detached_checkout_blocked_the_same_way_is_tagged_the_same_way()
    {
        // CodeFlow 1.7.2 maps both checkouts through the same error mapper, but only the local one
        // has an extracted fixture. The frontend's guard cannot tell them apart, so a difference
        // here would strand the detached case with no way out.
        var (repo, _) = Fork();
        using var _repo = repo;

        repo.Write("a.txt", "uncommitted work\n");

        var error = Assert.ThrowsAny<Exception>(() => Branches.CheckoutDetached(repo.Path, "feature"));

        Assert.StartsWith(Branches.CheckoutConflictPrefix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_checkout_that_fails_for_any_other_reason_is_not_tagged()
    {
        var (repo, _) = Fork();
        using var _repo = repo;

        var error = Assert.ThrowsAny<Exception>(() => Branches.CheckoutLocal(repo.Path, "no-such-branch"));

        Assert.DoesNotContain(Branches.CheckoutConflictPrefix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Checking_out_a_branch_moves_head_and_the_working_tree()
    {
        var (repo, baseBranch) = Fork();
        using var _repo = repo;

        Branches.CheckoutLocal(repo.Path, "feature");

        Assert.Equal("feature\n", repo.Read("a.txt"));
        Assert.Equal("feature", Branches.List(repo.Path).Single(b => b.IsHead).Name);

        Branches.CheckoutLocal(repo.Path, baseBranch);
        Assert.Equal("one\n", repo.Read("a.txt"));
    }

    [Fact]
    public void A_detached_checkout_leaves_every_branch_pointer_alone()
    {
        var (repo, baseBranch) = Fork();
        using var _repo = repo;

        var before = Branches.List(repo.Path).ToDictionary(b => b.Name, b => b.Target);

        Branches.CheckoutDetached(repo.Path, "feature");

        Assert.Equal("feature\n", repo.Read("a.txt"));
        Assert.All(Branches.List(repo.Path), b => Assert.Equal(before[b.Name], b.Target));

        // HEAD points at the commit itself, so no branch claims it.
        Assert.DoesNotContain(Branches.List(repo.Path), b => b.IsHead);
        Assert.True(RepoStatus.GetStatus(repo.Path).IsDetached);
        Assert.Null(RepoStatus.GetStatus(repo.Path).CurrentBranch);

        Branches.CheckoutLocal(repo.Path, baseBranch);
    }

    [Fact]
    public void Creating_a_branch_targets_the_start_point_and_does_not_check_it_out()
    {
        var (repo, baseBranch) = Fork();
        using var _repo = repo;

        Branches.Create(repo.Path, "from-feature", "feature");

        var branches = Branches.List(repo.Path);
        Assert.Equal(
            branches.Single(b => b.Name == "feature").Target,
            branches.Single(b => b.Name == "from-feature").Target);
        Assert.Equal(baseBranch, branches.Single(b => b.IsHead).Name);
    }

    [Fact]
    public void Creating_a_branch_without_a_start_point_targets_head()
    {
        var (repo, baseBranch) = Fork();
        using var _repo = repo;

        Branches.Create(repo.Path, "from-head", startPoint: null);

        var branches = Branches.List(repo.Path);
        Assert.Equal(
            branches.Single(b => b.Name == baseBranch).Target,
            branches.Single(b => b.Name == "from-head").Target);
    }

    [Fact]
    public void Creating_a_branch_that_already_exists_fails_rather_than_moving_it()
    {
        // The force flag is false in 1.7.2, and that is the whole behaviour: an existing
        // branch is never silently repointed.
        var (repo, _) = Fork();
        using var _repo = repo;

        Assert.ThrowsAny<Exception>(() => Branches.Create(repo.Path, "feature", startPoint: null));
    }

    [Fact]
    public void Deleting_the_checked_out_branch_fails_and_is_not_tagged()
    {
        var (repo, baseBranch) = Fork();
        using var _repo = repo;

        var error = Assert.ThrowsAny<Exception>(() => Branches.Delete(repo.Path, baseBranch, isRemote: false));

        Assert.DoesNotContain(Branches.CheckoutConflictPrefix, error.Message, StringComparison.Ordinal);
        Assert.Contains(Branches.List(repo.Path), b => b.Name == baseBranch);
    }

    [Fact]
    public void Deleting_a_branch_removes_it()
    {
        var (repo, _) = Fork();
        using var _repo = repo;

        Branches.Delete(repo.Path, "feature", isRemote: false);

        Assert.DoesNotContain(Branches.List(repo.Path), b => b.Name == "feature");
    }

    [Fact]
    public void Ahead_and_behind_are_counted_only_against_a_branchs_own_upstream()
    {
        // A second repository standing in for a remote, so the counters have something real to
        // compare against without any network.
        var (origin, originBase) = Fork();
        using var _origin = origin;

        using var clone = new TempRepo();
        string cloneBase;
        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
            Commands.Fetch(handle, "origin", [], null, null);

            var remote = handle.Branches[$"origin/{originBase}"];
            var local = handle.CreateBranch(originBase, remote.Tip);
            handle.Branches.Update(local, b => b.TrackedBranch = remote.CanonicalName);
            Commands.Checkout(handle, local);
            cloneBase = originBase;
        }

        Assert.All(
            Branches.List(clone.Path).Where(b => b.Name == cloneBase),
            b =>
            {
                Assert.Equal(0, b.Ahead);
                Assert.Equal(0, b.Behind);
                Assert.Equal($"origin/{cloneBase}", b.Upstream);
            });

        clone.Write("local.txt", "only here\n");
        clone.Commit("local work", "local.txt");

        var tracked = Branches.List(clone.Path).Single(b => b.Name == cloneBase);
        Assert.Equal(1, tracked.Ahead);
        Assert.Equal(0, tracked.Behind);

        // Remote branches never carry counters or an upstream of their own.
        Assert.All(
            Branches.List(clone.Path).Where(b => b.IsRemote),
            b =>
            {
                Assert.Equal(0, b.Ahead);
                Assert.Equal(0, b.Behind);
                Assert.Null(b.Upstream);
            });
    }

    [Fact]
    public void Connecting_to_a_remote_branch_creates_a_tracking_branch()
    {
        var (origin, originBase) = Fork();
        using var _origin = origin;

        using var clone = new TempRepo();
        clone.Write("seed.txt", "so HEAD exists\n");
        clone.Commit("seed", "seed.txt");

        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
            Commands.Fetch(handle, "origin", [], null, null);
        }

        var local = Branches.CheckoutRemoteTracking(clone.Path, "origin/feature");

        Assert.Equal("feature", local);
        var created = Branches.List(clone.Path).Single(b => b.Name == "feature");
        Assert.Equal("origin/feature", created.Upstream);
        Assert.True(created.IsHead);
    }

    [Fact]
    public void Connecting_reuses_an_existing_local_branch_without_repairing_its_upstream()
    {
        // AMBIGUOUS-GIT-a, pinned rather than resolved. A pre-existing local branch of the same
        // name is checked out as-is: its upstream is not set, not verified and not corrected, and
        // its tip is not moved to the remote's. The source does not say whether that was intended,
        // so the behaviour is fixed here as a contract instead of being quietly "fixed".
        var (origin, _) = Fork();
        using var _origin = origin;

        using var clone = new TempRepo();
        clone.Write("seed.txt", "so HEAD exists\n");
        clone.Commit("seed", "seed.txt");

        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
            Commands.Fetch(handle, "origin", [], null, null);
            handle.CreateBranch("feature");
        }

        var tipBefore = Branches.List(clone.Path).Single(b => b.Name == "feature").Target;

        var local = Branches.CheckoutRemoteTracking(clone.Path, "origin/feature");

        var reused = Branches.List(clone.Path).Single(b => b.Name == "feature");
        Assert.Equal("feature", local);
        Assert.True(reused.IsHead);
        Assert.Equal(tipBefore, reused.Target);
        Assert.Null(reused.Upstream);
    }

    [Fact]
    public void Connecting_needs_a_remote_qualified_name()
    {
        using var repo = new TempRepo();

        var error = Assert.ThrowsAny<Exception>(() => Branches.CheckoutRemoteTracking(repo.Path, "feature"));

        Assert.Equal("expected a name like 'origin/feature-x'", error.Message);
    }
}
