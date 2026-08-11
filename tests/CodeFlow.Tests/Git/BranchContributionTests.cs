using CodeFlow.Git;
using LibGit2Sharp;
using Xunit;

// LibGit2Sharp has a `Diff` of its own, and this file needs `Commands.Checkout` from that namespace
// as well as the one under test. The alias picks a winner instead of spelling out the full name at
// every call site.
using Diff = CodeFlow.Git.Diff;

namespace CodeFlow.Tests.Git;

/// <summary>
/// <see cref="Diff.BranchContribution"/> — everything a branch adds over its base, committed or not.
/// </summary>
/// <remarks>
/// No fixture file: this unit has no CodeFlow 1.7.2 ancestor to have been extracted from, and
/// <c>extractedFrom</c> is a provenance claim rather than a formality. Most of the git suite asserts
/// inline for the same reason.
/// </remarks>
public sealed class BranchContributionTests
{
    /// <summary>
    /// A repository forked into a feature branch, left checked out on it.
    /// </summary>
    /// <remarks>
    /// The base branch's name is read rather than assumed: <c>init.defaultBranch</c> is a host
    /// setting, and the rest of the git suite is careful not to hardcode <c>main</c> either.
    /// </remarks>
    private static (TempRepo Repo, string Base) Fork()
    {
        var repo = new TempRepo();
        repo.Write("base.txt", "shared\n");
        repo.Commit("initial", "base.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            Commands.Checkout(handle, handle.CreateBranch("feature"));
        }

        return (repo, baseBranch);
    }

    [Fact]
    public void A_file_committed_then_edited_again_appears_once_with_its_cumulative_change()
    {
        // The whole reason this method exists rather than concatenating BranchDiff and Working:
        // those are two comparisons against two baselines, so this file would arrive twice.
        var (repo, baseBranch) = Fork();
        using var _ = repo;

        repo.Write("a.txt", "first\n");
        repo.Commit("committed on the branch", "a.txt");

        repo.Write("a.txt", "first\nsecond, still uncommitted\n");

        var file = Assert.Single(Diff.BranchContribution(repo.Path, baseBranch), f => f.NewPath == "a.txt");

        Assert.Equal("added", file.Status);
        var hunk = Assert.Single(file.Hunks);

        // Both lines, as additions: the baseline is the merge base, where the file did not exist.
        Assert.Equal(["first", "second, still uncommitted"], hunk.Lines.Select(l => l.Content));
        Assert.All(hunk.Lines, l => Assert.Equal("+", l.Origin));
    }

    [Fact]
    public void An_untracked_file_is_part_of_what_the_branch_contributes()
    {
        // Not asked for at the call site: LibGit2Sharp derives IncludeUntracked from the
        // WorkingDirectory target on its own. A brand-new file nobody staged is usually the most
        // important thing a branch adds, so this pins the behaviour rather than trusting it.
        var (repo, baseBranch) = Fork();
        using var _ = repo;

        repo.Write("nested/fresh.txt", "brand new\n");

        var file = Assert.Single(
            Diff.BranchContribution(repo.Path, baseBranch), f => f.NewPath == "nested/fresh.txt");

        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(["brand new"], hunk.Lines.Select(l => l.Content));
    }

    [Fact]
    public void Staged_but_uncommitted_work_counts_too()
    {
        var (repo, baseBranch) = Fork();
        using var _ = repo;

        repo.Write("staged.txt", "in the index\n");
        repo.Stage("staged.txt");

        Assert.Contains(Diff.BranchContribution(repo.Path, baseBranch), f => f.NewPath == "staged.txt");
    }

    [Fact]
    public void What_the_base_branch_gained_after_the_fork_is_not_the_branchs_doing()
    {
        // The baseline is the merge base, not the base branch's tip — otherwise every commit
        // someone else landed on main would be reported as this branch's work, inverted.
        var (repo, baseBranch) = Fork();
        using var _ = repo;

        repo.Write("mine.txt", "branch work\n");
        repo.Commit("on the branch", "mine.txt");

        Branches.CheckoutLocal(repo.Path, baseBranch);
        repo.Write("theirs.txt", "somebody else's commit\n");
        repo.Commit("moved on", "theirs.txt");
        Branches.CheckoutLocal(repo.Path, "feature");

        var paths = Diff.BranchContribution(repo.Path, baseBranch).Select(f => f.NewPath).ToList();

        Assert.Contains("mine.txt", paths);
        Assert.DoesNotContain("theirs.txt", paths);
    }

    [Fact]
    public void With_a_clean_tree_it_reports_exactly_what_the_committed_branch_diff_does()
    {
        // The two agree when there is nothing uncommitted, which is what makes this a superset of
        // BranchDiff rather than a different answer to the same question.
        var (repo, baseBranch) = Fork();
        using var _ = repo;

        repo.Write("a.txt", "committed\n");
        repo.Commit("on the branch", "a.txt");

        Assert.Equal(
            Diff.BranchDiff(repo.Path, baseBranch, "feature").Select(f => f.NewPath),
            Diff.BranchContribution(repo.Path, baseBranch).Select(f => f.NewPath));
    }

    [Fact]
    public void An_unknown_base_reports_the_branch_by_name()
    {
        var (repo, _) = Fork();
        using var __ = repo;

        var error = Assert.ThrowsAny<Exception>(() => Diff.BranchContribution(repo.Path, "no-such-branch"));

        Assert.Contains("no-such-branch", error.Message, StringComparison.Ordinal);
    }
}
