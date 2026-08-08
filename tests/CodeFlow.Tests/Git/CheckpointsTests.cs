using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// AI-run undo (GIT-022 … GIT-025).
/// </summary>
public sealed class CheckpointsTests
{
    private const string Fixture = "git_checkpoint.vectors.json";

    [Fact]
    public void Restore_reverts_edits_and_deletes_the_files_the_run_created()
    {
        // git_checkpoint.vectors.json#restore-reverts-edits-and-deletes-created-files
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        var id = Checkpoints.Create(repo.Path, "fix-finding");

        // What an AI run would do: edit a tracked file and add a new one.
        repo.Write("tracked.txt", "rewritten by the agent\n");
        repo.Write("new.txt", "created by the agent\n");

        Assert.Equal(["new.txt", "tracked.txt"], Checkpoints.List(repo.Path).Single().ChangedPaths);

        var restored = Checkpoints.Restore(repo.Path, id);

        Assert.Equal(["new.txt", "tracked.txt"], restored);
        Assert.Equal("original\n", repo.Read("tracked.txt"));
        Assert.False(repo.Exists("new.txt"));
    }

    [Fact]
    public void Taking_a_snapshot_leaves_the_real_index_alone()
    {
        // git_checkpoint.vectors.json#snapshot-leaves-index-alone — the property the whole design
        // rests on. CodeFlow 1.7.2 gets it from an in-memory index attached to its own handle;
        // LibGit2Sharp cannot do that, so the tree is built through the object database instead,
        // and this is what proves the substitution kept the guarantee.
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("staged.txt", "staged content\n");
        repo.Stage("staged.txt");
        repo.Write("untracked.txt", "untracked content\n");

        var id = Checkpoints.Create(repo.Path, "chat");

        using (var handle = repo.Open())
        {
            Assert.Equal(
                ["staged.txt", "tracked.txt"],
                handle.Index.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal));
        }

        // Both went into the snapshot, staged or not, so both come back.
        repo.Delete("untracked.txt");
        repo.Write("staged.txt", "clobbered\n");

        Checkpoints.Restore(repo.Path, id);

        Assert.Equal("untracked content\n", repo.Read("untracked.txt"));
        Assert.Equal("staged content\n", repo.Read("staged.txt"));
    }

    [Fact]
    public void A_run_that_changed_nothing_drops_its_checkpoint()
    {
        // git_checkpoint.vectors.json#unchanged-checkpoint-auto-drops
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        var id = Checkpoints.Create(repo.Path, "chat");

        Assert.True(Checkpoints.RemoveIfUnchanged(repo.Path, id));
        Assert.Empty(Checkpoints.List(repo.Path));
    }

    [Fact]
    public void A_run_that_changed_something_keeps_its_checkpoint()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        var id = Checkpoints.Create(repo.Path, "chat");
        repo.Write("tracked.txt", "the agent wrote this\n");

        Assert.False(Checkpoints.RemoveIfUnchanged(repo.Path, id));
        Assert.Single(Checkpoints.List(repo.Path));
    }

    [Fact]
    public void Deleting_a_checkpoint_that_is_not_there_is_not_an_error_but_asking_about_one_is()
    {
        // The asymmetry in GIT-025, which is easy to invert: delete is a user pressing a button,
        // remove-if-unchanged runs automatically after a run and a missing id means trouble.
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        Checkpoints.Remove(repo.Path, "never-existed");

        var error = Assert.ThrowsAny<Exception>(() => Checkpoints.RemoveIfUnchanged(repo.Path, "never-existed"));
        Assert.Equal("checkpoint 'never-existed' no longer exists", error.Message);
    }

    [Fact]
    public void Restoring_a_checkpoint_that_is_gone_says_so()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        var id = Checkpoints.Create(repo.Path, "chat");
        Checkpoints.Remove(repo.Path, id);

        var error = Assert.ThrowsAny<Exception>(() => Checkpoints.Restore(repo.Path, id));
        Assert.Equal($"checkpoint '{id}' no longer exists", error.Message);
    }

    [Fact]
    public void A_checkpoint_never_shows_up_as_a_branch_or_moves_head()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        var head = repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "uncommitted\n");
        var id = Checkpoints.Create(repo.Path, "chat");

        Assert.DoesNotContain(Branches.List(repo.Path), b => b.Name.Contains("checkpoint", StringComparison.Ordinal));

        using var handle = repo.Open();
        Assert.Equal(head, handle.Head.Tip.Id);
        Assert.NotNull(handle.Refs[$"refs/codeflow/checkpoints/{id}"]);

        // git status reads exactly the same before and after.
        Assert.Equal([new FileStatusEntry("tracked.txt", "modified")], RepoStatus.GetStatus(repo.Path).Unstaged);
    }

    [Fact]
    public void Restoring_twice_is_idempotent()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        var id = Checkpoints.Create(repo.Path, "chat");
        repo.Write("tracked.txt", "changed\n");

        Assert.Single(Checkpoints.Restore(repo.Path, id));

        // Nothing differs any more, so the second call has nothing to put back.
        Assert.Empty(Checkpoints.Restore(repo.Path, id));
        Assert.Equal("original\n", repo.Read("tracked.txt"));
    }

    [Fact]
    public void Changed_paths_are_recomputed_against_the_tree_as_it_is_now()
    {
        // Not a record of what changed when the checkpoint was taken: a file the user has since
        // put back by hand drops out of the list, because the comparison is content-based.
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        Checkpoints.Create(repo.Path, "chat");

        repo.Write("tracked.txt", "changed\n");
        Assert.Equal(["tracked.txt"], Checkpoints.List(repo.Path).Single().ChangedPaths);

        repo.Write("tracked.txt", "original\n");
        Assert.Empty(Checkpoints.List(repo.Path).Single().ChangedPaths);
    }

    [Fact]
    public void Only_the_twenty_newest_checkpoints_are_kept()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            // Each snapshot needs distinct content, or the ids would be the only thing telling
            // them apart and the prune order would be arbitrary.
            repo.Write("tracked.txt", $"revision {i}\n");
            ids.Add(Checkpoints.Create(repo.Path, "chat"));
        }

        var kept = Checkpoints.List(repo.Path);

        Assert.Equal(20, kept.Count);
        Assert.All(kept, c => Assert.Contains(c.Id, ids));

        // Which five were dropped is not asserted, and that is not laziness. Pruning ranks by the
        // commit's own timestamp, whose resolution is one second, so a burst created inside the
        // same second ties and the order among them is undefined — in 1.7.2 exactly as
        // much as here. The cap is the contract; the tie-break is not.
    }

    [Fact]
    public void The_kind_is_carried_through_untranslated()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        Checkpoints.Create(repo.Path, "replace-all");

        Assert.Equal("replace-all", Checkpoints.List(repo.Path).Single().Kind);
    }
}
