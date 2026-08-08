using CodeFlow.Git;
using LibGit2Sharp;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// The four merge outcomes (GIT-016), conflict resolution (GIT-017) and finishing or throwing
/// away a merge (GIT-018).
/// </summary>
/// <remarks>
/// CodeFlow 1.7.2 has no tests here at all, and this is the part of the domain where a mistake
/// destroys work: an abort that does not clear the merge state leaves the conflict banner up
/// forever, and a complete that commits one parent silently loses the other side's history.
/// </remarks>
public sealed class MergeTests
{
    /// <summary>Two branches that both changed the same line, so merging them conflicts.</summary>
    private static (TempRepo Repo, string Base) Conflicting()
    {
        var repo = new TempRepo();
        repo.Write("shared.txt", "original\n");
        repo.Commit("initial", "shared.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            Commands.Checkout(handle, handle.CreateBranch("feature"));
        }

        repo.Write("shared.txt", "theirs\n");
        repo.Commit("feature edit", "shared.txt");

        Branches.CheckoutLocal(repo.Path, baseBranch);
        repo.Write("shared.txt", "ours\n");
        repo.Commit("base edit", "shared.txt");

        return (repo, baseBranch);
    }

    /// <summary>Two branches that touched different files, so merging them is clean.</summary>
    private static (TempRepo Repo, string Base) Divergent()
    {
        var repo = new TempRepo();
        repo.Write("shared.txt", "original\n");
        repo.Commit("initial", "shared.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            Commands.Checkout(handle, handle.CreateBranch("feature"));
        }

        repo.Write("theirs.txt", "from the feature branch\n");
        repo.Commit("feature file", "theirs.txt");

        Branches.CheckoutLocal(repo.Path, baseBranch);
        repo.Write("ours.txt", "from the base branch\n");
        repo.Commit("base file", "ours.txt");

        return (repo, baseBranch);
    }

    [Fact]
    public void Merging_something_already_contained_is_up_to_date()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial", "a.txt");

        using (var handle = repo.Open())
        {
            handle.CreateBranch("behind");
        }

        repo.Write("a.txt", "two\n");
        repo.Commit("ahead", "a.txt");

        var outcome = Merge.Branch(repo.Path, "behind");

        Assert.Equal("up_to_date", outcome.Status);
        Assert.Empty(outcome.Conflicts);
        Assert.False(Merge.IsMerging(repo.Path));
    }

    [Fact]
    public void Merging_a_branch_ahead_of_head_fast_forwards_without_a_merge_commit()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        var start = repo.Commit("initial", "a.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            Commands.Checkout(handle, handle.CreateBranch("feature"));
        }

        repo.Write("a.txt", "two\n");
        var ahead = repo.Commit("feature edit", "a.txt");
        Branches.CheckoutLocal(repo.Path, baseBranch);

        var outcome = Merge.Branch(repo.Path, "feature");

        Assert.Equal("fast_forward", outcome.Status);
        using var handle2 = repo.Open();
        Assert.Equal(ahead, handle2.Head.Tip.Id);
        Assert.Single(handle2.Head.Tip.Parents);
        Assert.Equal(start, handle2.Head.Tip.Parents.Single().Id);
        Assert.Equal("two\n", repo.Read("a.txt"));
        Assert.False(Merge.IsMerging(repo.Path));
    }

    [Fact]
    public void A_clean_merge_commits_two_parents_and_clears_the_merge_state()
    {
        var (repo, baseBranch) = Divergent();
        using var _repo = repo;

        var outcome = Merge.Branch(repo.Path, "feature");

        Assert.Equal("merged", outcome.Status);
        Assert.Empty(outcome.Conflicts);

        using var handle = repo.Open();
        var tip = handle.Head.Tip;
        Assert.Equal(2, tip.Parents.Count());
        Assert.Equal("Merge branch 'feature'", tip.Message);
        Assert.Equal(baseBranch, handle.Head.FriendlyName);

        // Both sides' files are present, and nothing thinks a merge is still in progress.
        Assert.True(repo.Exists("ours.txt"));
        Assert.True(repo.Exists("theirs.txt"));
        Assert.False(Merge.IsMerging(repo.Path));
        Assert.Empty(RepoStatus.GetStatus(repo.Path).Staged);
    }

    [Fact]
    public void A_clean_merge_signs_with_the_supplied_author_when_both_halves_are_given()
    {
        // GIT-036: the resolved workspace identity arrives here as the optional pair; without it
        // the repo's configured signature signs, which the other merge tests already pin.
        var (repo, _) = Divergent();
        using var _repo = repo;

        var outcome = Merge.Branch(repo.Path, "feature", "Work Person", "work@company.com");

        Assert.Equal("merged", outcome.Status);
        using var handle = repo.Open();
        Assert.Equal("Work Person", handle.Head.Tip.Author.Name);
        Assert.Equal("work@company.com", handle.Head.Tip.Author.Email);
        Assert.Equal("Work Person", handle.Head.Tip.Committer.Name);
    }

    [Fact]
    public void A_conflicting_merge_returns_conflicts_as_a_result_not_an_error()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        var outcome = Merge.Branch(repo.Path, "feature");

        Assert.Equal("conflicts", outcome.Status);
        Assert.Equal(["shared.txt"], outcome.Conflicts);

        // Left mid-merge on purpose: this is the state the conflict UI works on.
        Assert.True(Merge.IsMerging(repo.Path));
        Assert.Equal([new ConflictFile("shared.txt")], Merge.ListConflicts(repo.Path));
    }

    [Fact]
    public void The_three_conflicting_sides_are_readable_from_the_index()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");

        var versions = Merge.Versions(repo.Path, "shared.txt");

        Assert.Equal("original\n", versions.Base);
        Assert.Equal("ours\n", versions.Ours);
        Assert.Equal("theirs\n", versions.Theirs);
    }

    [Fact]
    public void A_side_that_does_not_exist_reads_as_empty_rather_than_failing()
    {
        // Added on one side only: there is no ancestor stage for it.
        using var repo = new TempRepo();
        repo.Write("seed.txt", "shared history\n");
        repo.Commit("initial", "seed.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            Commands.Checkout(handle, handle.CreateBranch("feature"));
        }

        repo.Write("added.txt", "theirs\n");
        repo.Commit("added on feature", "added.txt");

        Branches.CheckoutLocal(repo.Path, baseBranch);
        repo.Write("added.txt", "ours\n");
        repo.Commit("added on base", "added.txt");

        Merge.Branch(repo.Path, "feature");

        var versions = Merge.Versions(repo.Path, "added.txt");
        Assert.Equal(string.Empty, versions.Base);
        Assert.Equal("ours\n", versions.Ours);
        Assert.Equal("theirs\n", versions.Theirs);

        var error = Assert.ThrowsAny<Exception>(() => Merge.ResolveSide(repo.Path, "added.txt", "base"));
        Assert.Equal("side must be 'ours' or 'theirs'", error.Message);
    }

    [Fact]
    public void Taking_one_side_writes_it_to_disk_and_clears_the_conflict()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");
        Merge.ResolveSide(repo.Path, "shared.txt", "theirs");

        Assert.Equal("theirs\n", repo.Read("shared.txt"));
        Assert.Empty(Merge.ListConflicts(repo.Path));
        Assert.Contains(RepoStatus.GetStatus(repo.Path).Staged, e => e.Path == "shared.txt");
    }

    [Fact]
    public void Marking_resolved_stages_whatever_the_user_left_on_disk()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");
        repo.Write("shared.txt", "a hand-written blend\n");
        Merge.MarkResolved(repo.Path, "shared.txt");

        Assert.Empty(Merge.ListConflicts(repo.Path));
        Assert.Equal("a hand-written blend\n", repo.Read("shared.txt"));
    }

    [Fact]
    public void Completing_refuses_while_conflicts_remain()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");

        var error = Assert.ThrowsAny<Exception>(() => Merge.Complete(repo.Path, "done"));

        Assert.Equal("There are still unresolved conflicts", error.Message);
        Assert.True(Merge.IsMerging(repo.Path));
    }

    [Fact]
    public void Completing_commits_both_parents_and_ends_the_merge()
    {
        var (repo, baseBranch) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");
        Merge.ResolveSide(repo.Path, "shared.txt", "ours");

        var sha = Merge.Complete(repo.Path, "merged by hand");

        using var handle = repo.Open();
        var tip = handle.Head.Tip;
        Assert.Equal(sha, tip.Id.Sha);
        Assert.Equal("merged by hand", tip.Message);
        Assert.Equal(2, tip.Parents.Count());
        Assert.Equal(baseBranch, handle.Head.FriendlyName);

        // The whole point of the cleanup: the conflict banner has to go away.
        Assert.False(Merge.IsMerging(repo.Path));
        Assert.Empty(Merge.ListConflicts(repo.Path));
    }

    [Fact]
    public void Completing_signs_with_the_supplied_author_when_both_halves_are_given()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");
        Merge.ResolveSide(repo.Path, "shared.txt", "ours");

        var sha = Merge.Complete(repo.Path, "merged as work", "Work Person", "work@company.com");

        using var handle = repo.Open();
        Assert.Equal(sha, handle.Head.Tip.Id.Sha);
        Assert.Equal("Work Person", handle.Head.Tip.Author.Name);
        Assert.Equal("work@company.com", handle.Head.Tip.Author.Email);
    }

    [Fact]
    public void Completing_still_works_after_the_process_restarted_mid_conflict()
    {
        // MERGE_HEAD is read fresh rather than remembered from the merge call, which is what makes
        // resolving a conflict across an application restart possible at all.
        var (repo, _) = Conflicting();
        using var _repo = repo;

        Merge.Branch(repo.Path, "feature");
        Merge.ResolveSide(repo.Path, "shared.txt", "theirs");

        // Nothing is carried over between these calls; each opens the repository from scratch.
        var sha = Merge.Complete(repo.Path, "resolved later");

        using var handle = repo.Open();
        Assert.Equal(sha, handle.Head.Tip.Id.Sha);
        Assert.Equal(2, handle.Head.Tip.Parents.Count());
    }

    [Fact]
    public void Aborting_restores_head_and_ends_the_merge()
    {
        var (repo, _) = Conflicting();
        using var _repo = repo;

        var before = RepoStatus.GetStatus(repo.Path);
        Merge.Branch(repo.Path, "feature");

        Merge.Abort(repo.Path);

        Assert.False(Merge.IsMerging(repo.Path));
        Assert.Empty(Merge.ListConflicts(repo.Path));
        Assert.Equal("ours\n", repo.Read("shared.txt"));
        Assert.Equal(before.CurrentBranch, RepoStatus.GetStatus(repo.Path).CurrentBranch);
        Assert.Empty(RepoStatus.GetStatus(repo.Path).Staged);
    }

    [Fact]
    public void Aborting_a_clean_uncommitted_merge_also_discards_it()
    {
        // No conflict check first: this throws away a merge that succeeded but was not committed,
        // which is easy to mistake for a bug and is the documented behaviour.
        var (repo, _) = Divergent();
        using var _repo = repo;

        using (var handle = repo.Open())
        {
            var theirs = handle.Branches["feature"].Tip;
            handle.Merge(theirs, handle.Config.BuildSignature(DateTimeOffset.Now), new MergeOptions
            {
                CommitOnSuccess = false,
                FastForwardStrategy = FastForwardStrategy.NoFastForward,
            });
        }

        Merge.Abort(repo.Path);

        Assert.False(Merge.IsMerging(repo.Path));
        Assert.False(repo.Exists("theirs.txt"));
        Assert.True(repo.Exists("ours.txt"));
    }

    [Fact]
    public void A_local_branch_wins_over_a_remote_one_with_the_same_name()
    {
        var (origin, originBase) = Divergent();
        using var _origin = origin;

        using var clone = new TempRepo();
        clone.Write("seed.txt", "so HEAD exists\n");
        clone.Commit("seed", "seed.txt");

        using (var handle = clone.Open())
        {
            handle.Network.Remotes.Add("origin", origin.Path);
            Commands.Fetch(handle, "origin", [], null, null);
        }

        // A local "feature" that is already an ancestor of HEAD, so merging it is up_to_date —
        // while origin/feature is not, and would have merged. The outcome tells them apart.
        using (var handle = clone.Open())
        {
            handle.CreateBranch("feature", handle.Head.Tip);
        }

        Assert.Equal("up_to_date", Merge.Branch(clone.Path, "feature").Status);
    }
}
