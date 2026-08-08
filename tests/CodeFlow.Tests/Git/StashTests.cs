using System.Diagnostics;
using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// The stash stack (GIT-015) and the rename trick that reorders it (GIT-014).
/// </summary>
public sealed class StashTests
{
    private const string Fixture = "git_stash.vectors.json";

    /// <summary>
    /// Two stashes created by the real <c>git</c> CLI, exactly as 1.7.2's fixture does.
    /// </summary>
    /// <remarks>
    /// Shelling out is deliberate and is copied from 1.7.2: it is what
    /// gives the messages git's own <c>"On &lt;branch&gt;: "</c> prefix. Creating them through
    /// libgit2 would produce messages that already look like what rename writes, and the tests
    /// would stop being able to tell the two apart.
    /// </remarks>
    private static TempRepo WithTwoStashes()
    {
        var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "init");

        repo.Write("a.txt", "two\n");
        Git(repo, "stash", "push", "-q", "-m", "first stash");
        repo.Write("a.txt", "three\n");
        Git(repo, "stash", "push", "-q", "-m", "second stash");

        return repo;
    }

    [Fact]
    public void Renaming_the_top_stash_keeps_the_order()
    {
        // git_stash.vectors.json#rename-top-stash-keeps-order
        const string Case = "rename-top-stash-keeps-order";

        using var repo = WithTwoStashes();

        var before = Stash.List(repo.Path);
        Assert.Equal(2, before.Count);
        Assert.Contains("second stash", before[0].Message, StringComparison.Ordinal);
        Assert.Contains("first stash", before[1].Message, StringComparison.Ordinal);

        Stash.Rename(repo.Path, 0, "renamed second");

        var after = Stash.List(repo.Path);
        Assert.Equal(GitFixtures.Int(Fixture, Case, "length_after"), after.Count);

        // Exact, not a containment check: rename writes the message verbatim, with none of the
        // "On <branch>: " prefix the CLI adds.
        Assert.Equal(GitFixtures.String(Fixture, Case, "stash_0_message_after"), after[0].Message);
        Assert.Contains(
            GitFixtures.String(Fixture, Case, "stash_1_message_after_contains"),
            after[1].Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_lower_stash_moves_it_to_the_top()
    {
        // git_stash.vectors.json#rename-non-top-stash-moves-to-top — DIVERGENCE-GIT-a. The
        // reordering is the documented side effect of drop-and-reappend and must be preserved:
        // the frontend has to re-read the whole list after a rename, not just patch one row.
        const string Case = "rename-non-top-stash-moves-to-top";

        using var repo = WithTwoStashes();

        Stash.Rename(repo.Path, 1, "renamed first");

        var after = Stash.List(repo.Path);
        Assert.Equal(GitFixtures.Int(Fixture, Case, "length_after"), after.Count);
        Assert.Equal(GitFixtures.String(Fixture, Case, "stash_0_message_after"), after[0].Message);
        Assert.Contains(
            GitFixtures.String(Fixture, Case, "stash_1_message_after_contains"),
            after[1].Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_stash_that_is_not_there_says_so()
    {
        using var repo = WithTwoStashes();

        var error = Assert.ThrowsAny<Exception>(() => Stash.Rename(repo.Path, 7, "nowhere"));

        Assert.Equal("Stash not found", error.Message);
        Assert.Equal(2, Stash.List(repo.Path).Count);
    }

    [Fact]
    public void Saving_defaults_the_message_and_can_include_untracked_files()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "edited\n");
        repo.Write("untracked.txt", "not staged\n");

        Stash.Save(repo.Path, message: null, includeUntracked: true);

        // libgit2 composes the reflog entry as "On <branch>: <message>" itself, so the default is
        // visible inside it rather than as the whole string. Rename is the one path that writes a
        // message verbatim, which is exactly what makes its output distinguishable from this one.
        Assert.Contains("WIP", Assert.Single(Stash.List(repo.Path)).Message, StringComparison.Ordinal);

        // Both the tracked edit and the untracked file went into the stash.
        Assert.Equal("original\n", repo.Read("tracked.txt"));
        Assert.False(repo.Exists("untracked.txt"));
    }

    [Fact]
    public void Untracked_files_stay_put_when_they_are_not_asked_for()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "edited\n");
        repo.Write("untracked.txt", "not staged\n");

        Stash.Save(repo.Path, "just the tracked one", includeUntracked: false);

        Assert.Equal("original\n", repo.Read("tracked.txt"));
        Assert.True(repo.Exists("untracked.txt"));
    }

    [Fact]
    public void Applying_keeps_the_stash_and_popping_removes_it()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "original\n");
        repo.Commit("initial", "a.txt");

        repo.Write("a.txt", "edited\n");
        Stash.Save(repo.Path, "work", includeUntracked: false);

        Assert.Equal("applied", Stash.Apply(repo.Path, 0));
        Assert.Equal("edited\n", repo.Read("a.txt"));
        Assert.Single(Stash.List(repo.Path));

        // Pop needs a clean tree to apply onto, so put back what apply just restored.
        Diff.DiscardAllChanges(repo.Path);
        Assert.Equal("applied", Stash.Pop(repo.Path, 0));
        Assert.Equal("edited\n", repo.Read("a.txt"));
        Assert.Empty(Stash.List(repo.Path));
    }

    [Fact]
    public void A_pop_that_conflicts_says_so_and_keeps_the_stash()
    {
        // The bug this closes: LibGit2Sharp returns a StashApplyStatus and throws nothing, so
        // discarding it reported a conflicted pop to the UI as a success — conflict markers on
        // disk, entries in the index, and not a word about it (GIT-015).
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial", "a.txt");

        repo.Write("a.txt", "mine\n");
        Stash.Save(repo.Path, "work", includeUntracked: false);

        // A committed change to the same line, so the stash cannot apply cleanly.
        repo.Write("a.txt", "theirs\n");
        repo.Commit("theirs", "a.txt");

        Assert.Equal("conflicts", Stash.Pop(repo.Path, 0));

        // Both halves of what makes this recoverable: the conflict is visible in the index, and the
        // entry is still in the stash list — LibGit2Sharp's own Pop drops it even here.
        Assert.Contains("a.txt", Merge.ListConflicts(repo.Path).Select(c => c.Path));
        Assert.Single(Stash.List(repo.Path));
    }

    [Fact]
    public void A_conflicted_stash_leaves_the_repository_out_of_a_merge()
    {
        // Why the conflict UI could not be gated on `is_merging`: this state has conflicts to
        // resolve and no MERGE_HEAD at all (GIT-019).
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial", "a.txt");

        repo.Write("a.txt", "mine\n");
        Stash.Save(repo.Path, "work", includeUntracked: false);
        repo.Write("a.txt", "theirs\n");
        repo.Commit("theirs", "a.txt");
        Stash.Pop(repo.Path, 0);

        Assert.False(Merge.IsMerging(repo.Path));
        Assert.NotEmpty(Merge.ListConflicts(repo.Path));
    }

    [Fact]
    public void Dropping_removes_a_stash_without_applying_it()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "original\n");
        repo.Commit("initial", "a.txt");

        repo.Write("a.txt", "edited\n");
        Stash.Save(repo.Path, "work", includeUntracked: false);

        Stash.Drop(repo.Path, 0);

        Assert.Empty(Stash.List(repo.Path));
        Assert.Equal("original\n", repo.Read("a.txt"));
    }

    [Fact]
    public void Stashing_unblocks_a_checkout_that_local_changes_were_blocking()
    {
        // git_branch.vectors.json#stash-then-checkout-succeeds. It lives here because it is the
        // only extracted scenario that spans both files, and it is the frontend's real recovery
        // path for GIT-003: checkoutGuarded stashes and retries the identical call.
        const string BranchFixture = "git_branch.vectors.json";
        const string Case = "stash-then-checkout-succeeds";

        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial", "a.txt");

        string baseBranch;
        using (var handle = repo.Open())
        {
            baseBranch = handle.Head.FriendlyName;
            LibGit2Sharp.Commands.Checkout(handle, handle.Branches.Add("feature", handle.Head.Tip));
        }

        repo.Write("a.txt", "feature\n");
        repo.Commit("feature edit", "a.txt");
        Branches.CheckoutLocal(repo.Path, baseBranch);

        repo.Write("a.txt", "uncommitted work\n");

        Assert.ThrowsAny<Exception>(() => Branches.CheckoutLocal(repo.Path, "feature"));

        Stash.Save(repo.Path, "auto stash", includeUntracked: true);
        Branches.CheckoutLocal(repo.Path, "feature");

        Assert.False(GitFixtures.Bool(BranchFixture, Case, "second_checkout_error"));
        Assert.Equal(GitFixtures.String(BranchFixture, Case, "a.txt_content"), repo.Read("a.txt"));

        // The stash is left in place, not popped — recovering the work is the user's call.
        Assert.Equal(GitFixtures.Int(BranchFixture, Case, "stash_count_after"), Stash.List(repo.Path).Count);
    }

    private static void Git(TempRepo repo, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repo.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }
}
