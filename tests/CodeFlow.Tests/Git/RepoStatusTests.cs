using CodeFlow.Git;
using LibGit2Sharp;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// Status bucketing (GIT-001) and reset (GIT-002).
/// </summary>
/// <remarks>
/// CodeFlow 1.7.2 has no tests over the implementation, so none of these come from an extracted
/// fixture. They exist because both rules are ones a reasonable developer would get wrong in the
/// same direction: the priority order looks like it could be a set, and <c>reset</c>'s unvalidated
/// mode looks like a missing guard.
/// </remarks>
public sealed class RepoStatusTests
{
    [Fact]
    public void A_path_staged_and_then_modified_again_is_reported_once_as_staged()
    {
        // The order in GIT-001 is the behaviour, not an implementation detail: the index checks
        // run first, so the working-tree branches are never reached for this path.
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "staged\n");
        repo.Stage("tracked.txt");
        repo.Write("tracked.txt", "and then edited again\n");

        var status = RepoStatus.GetStatus(repo.Path);

        Assert.Equal([new FileStatusEntry("tracked.txt", "modified")], status.Staged);
        Assert.Empty(status.Unstaged);
    }

    [Fact]
    public void Every_bucket_gets_its_own_label()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Write("doomed.txt", "delete me\n");
        repo.Commit("initial", "tracked.txt", "doomed.txt");

        repo.Write("added.txt", "new and staged\n");
        repo.Stage("added.txt");
        repo.Write("tracked.txt", "edited\n");
        repo.Delete("doomed.txt");
        repo.Write("nested/untracked.txt", "not staged\n");

        var status = RepoStatus.GetStatus(repo.Path);

        Assert.Equal([new FileStatusEntry("added.txt", "added")], status.Staged);
        Assert.Equal(
            [new FileStatusEntry("doomed.txt", "deleted"), new FileStatusEntry("tracked.txt", "modified")],
            status.Unstaged.OrderBy(e => e.Path).ToList());

        // recurse_untracked_dirs: the file itself, not the directory containing it.
        Assert.Equal([new FileStatusEntry("nested/untracked.txt", "untracked")], status.Untracked);
        Assert.Empty(status.Conflicted);
    }

    [Fact]
    public void A_staged_rename_is_reported_as_renamed()
    {
        // BUG-GIT-a, closed: rename detection is on, so a staged rename is one "renamed" entry
        // under the new path instead of an unrelated delete plus add. The renderer's label and
        // colour for it predate the fix and were dead code until now.
        using var repo = new TempRepo();
        repo.Write("before.txt", "same content\n");
        repo.Commit("initial", "before.txt");

        repo.Delete("before.txt");
        repo.Write("after.txt", "same content\n");
        repo.Stage("before.txt", "after.txt");

        var status = RepoStatus.GetStatus(repo.Path);

        Assert.Equal([new FileStatusEntry("after.txt", "renamed")], status.Staged);
        Assert.DoesNotContain(status.Staged, e => e.Status is "added" or "deleted");
    }

    [Fact]
    public void An_ignored_file_is_not_reported_at_all()
    {
        using var repo = new TempRepo();
        repo.Write(".gitignore", "ignored.txt\n");
        repo.Commit("initial", ".gitignore");

        repo.Write("ignored.txt", "invisible\n");

        var status = RepoStatus.GetStatus(repo.Path);

        Assert.Empty(status.Untracked);
        Assert.Empty(status.Staged);
        Assert.Empty(status.Unstaged);
    }

    [Fact]
    public void A_repository_with_no_commits_has_no_current_branch()
    {
        // `repo.head()` fails while HEAD is unborn, which leaves the name unset rather than
        // erroring. LibGit2Sharp would happily report the branch name here, so this is a real
        // difference and not a formality.
        using var repo = new TempRepo();

        var status = RepoStatus.GetStatus(repo.Path);

        Assert.Null(status.CurrentBranch);
        Assert.False(status.IsDetached);
    }

    [Fact]
    public void A_detached_head_reports_no_branch()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        var first = repo.Commit("initial", "tracked.txt");

        using (var handle = repo.Open())
        {
            handle.Refs.UpdateTarget("HEAD", first.Sha);
        }

        var status = RepoStatus.GetStatus(repo.Path);

        Assert.Null(status.CurrentBranch);
        Assert.True(status.IsDetached);
    }

    [Theory]
    [InlineData("soft", true, false)]
    [InlineData("mixed", false, true)]
    [InlineData("hard", false, false)]
    // Not a typo: an unrecognised mode is silently treated as mixed, with no validation anywhere.
    // The frontend only ever sends "mixed", so this branch is reachable only by a caller mistake —
    // and it must stay a silent fallback rather than becoming an error.
    [InlineData("MIXED", false, true)]
    [InlineData("not-a-mode", false, true)]
    public void Reset_moves_head_and_the_mode_decides_what_follows(
        string mode, bool expectStaged, bool expectUnstaged)
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "first\n");
        var first = repo.Commit("first", "tracked.txt");
        repo.Write("tracked.txt", "second\n");
        repo.Commit("second", "tracked.txt");

        RepoStatus.ResetToCommit(repo.Path, first.Sha, mode);

        var status = RepoStatus.GetStatus(repo.Path);
        Assert.Equal(expectStaged, status.Staged.Any(e => e.Path == "tracked.txt"));
        Assert.Equal(expectUnstaged, status.Unstaged.Any(e => e.Path == "tracked.txt"));

        // hard is the only one that rewrites the working tree.
        Assert.Equal(mode == "hard" ? "first\n" : "second\n", repo.Read("tracked.txt"));

        using var handle = repo.Open();
        Assert.Equal(first, handle.Head.Tip.Id);
    }

    [Fact]
    public void Resetting_to_an_unknown_commit_fails()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "first\n");
        repo.Commit("first", "tracked.txt");

        var missing = new string('0', 39) + "1";

        Assert.ThrowsAny<Exception>(() => RepoStatus.ResetToCommit(repo.Path, missing, "mixed"));
    }
}
