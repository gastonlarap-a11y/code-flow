using CodeFlow.Git;
using Xunit;

namespace CodeFlow.Tests.Git;

/// <summary>
/// Diffs, staging and discarding (GIT-012, GIT-013, GIT-029), plus commit (GIT-028).
/// </summary>
public sealed class DiffTests
{
    private const string Fixture = "git_diff.vectors.json";

    [Fact]
    public void Discard_all_reverts_tracked_edits_and_removes_untracked_files()
    {
        // git_diff.vectors.json#discard-all-reverts-tracked-removes-untracked
        const string Case = "discard-all-reverts-tracked-removes-untracked";

        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "edited\n");
        repo.Write("nested/new.txt", "brand new\n");

        Diff.DiscardAllChanges(repo.Path);

        Assert.Equal(GitFixtures.String(Fixture, Case, "tracked.txt_content"), repo.Read("tracked.txt"));
        Assert.Equal(GitFixtures.Bool(Fixture, Case, "nested/new.txt_exists"), repo.Exists("nested/new.txt"));

        // The directory the deleted file lived in was untracked too, so it goes with it — git
        // records no empty directories and leaving it would show a stray folder in the file tree.
        Assert.Equal(
            GitFixtures.Bool(Fixture, Case, "nested_dir_exists"),
            Directory.Exists(Path.Combine(repo.Path, "nested")));
    }

    [Fact]
    public void Discard_all_keeps_staged_content()
    {
        // git_diff.vectors.json#discard-all-keeps-staged-content — the guarantee that makes this
        // "discard what the Changes section shows" rather than "reset to HEAD".
        const string Case = "discard-all-keeps-staged-content";

        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "staged version\n");
        repo.Stage("tracked.txt");
        repo.Write("tracked.txt", "unstaged version\n");

        Diff.DiscardAllChanges(repo.Path);

        Assert.Equal(GitFixtures.String(Fixture, Case, "tracked.txt_content"), repo.Read("tracked.txt"));

        var status = RepoStatus.GetStatus(repo.Path);
        Assert.Equal(
            GitFixtures.Bool(Fixture, Case, "status_is_index_modified"),
            status.Staged.Any(e => e.Path == "tracked.txt"));
        Assert.Equal(
            GitFixtures.Bool(Fixture, Case, "status_is_wt_modified"),
            status.Unstaged.Any(e => e.Path == "tracked.txt"));
    }

    [Fact]
    public void Discard_all_leaves_conflicted_and_staged_only_paths_alone()
    {
        using var repo = new TempRepo();
        repo.Write("staged-only.txt", "committed\n");
        repo.Commit("initial", "staged-only.txt");

        repo.Write("staged-only.txt", "staged and not touched since\n");
        repo.Stage("staged-only.txt");

        Diff.DiscardAllChanges(repo.Path);

        // Staged with no edit on top is in neither list, so nothing restores over it.
        Assert.Equal("staged and not touched since\n", repo.Read("staged-only.txt"));
        Assert.Contains(RepoStatus.GetStatus(repo.Path).Staged, e => e.Path == "staged-only.txt");
    }

    [Fact]
    public void Discarding_one_file_restores_it_from_the_index()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "original\n");
        repo.Write("b.txt", "original\n");
        repo.Commit("initial", "a.txt", "b.txt");

        repo.Write("a.txt", "edited\n");
        repo.Write("b.txt", "edited\n");

        Diff.DiscardFileChanges(repo.Path, "a.txt");

        Assert.Equal("original\n", repo.Read("a.txt"));
        Assert.Equal("edited\n", repo.Read("b.txt"));
    }

    [Fact]
    public void Staging_a_file_that_is_gone_from_disk_stages_its_removal()
    {
        // GIT-013: matches `git add <deleted-file>`, which is easy to get backwards — the obvious
        // implementation throws instead.
        using var repo = new TempRepo();
        repo.Write("doomed.txt", "here for now\n");
        repo.Commit("initial", "doomed.txt");

        repo.Delete("doomed.txt");
        Diff.StageFile(repo.Path, "doomed.txt");

        var status = RepoStatus.GetStatus(repo.Path);
        Assert.Equal([new FileStatusEntry("doomed.txt", "deleted")], status.Staged);
        Assert.Empty(status.Unstaged);
    }

    [Fact]
    public void Stage_all_and_unstage_all_are_inverses()
    {
        using var repo = new TempRepo();
        repo.Write("tracked.txt", "original\n");
        repo.Commit("initial", "tracked.txt");

        repo.Write("tracked.txt", "edited\n");
        repo.Write("added.txt", "new\n");

        Diff.StageAll(repo.Path);
        Assert.Equal(2, RepoStatus.GetStatus(repo.Path).Staged.Count);

        Diff.UnstageAll(repo.Path);
        var status = RepoStatus.GetStatus(repo.Path);
        Assert.Empty(status.Staged);
        Assert.Equal([new FileStatusEntry("tracked.txt", "modified")], status.Unstaged);
        Assert.Equal([new FileStatusEntry("added.txt", "untracked")], status.Untracked);
    }

    [Fact]
    public void Unstaging_one_path_resets_only_that_path()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "original\n");
        repo.Write("b.txt", "original\n");
        repo.Commit("initial", "a.txt", "b.txt");

        repo.Write("a.txt", "edited\n");
        repo.Write("b.txt", "edited\n");
        Diff.StageAll(repo.Path);

        Diff.UnstageFile(repo.Path, "a.txt");

        var status = RepoStatus.GetStatus(repo.Path);
        Assert.Equal([new FileStatusEntry("b.txt", "modified")], status.Staged);
        Assert.Equal([new FileStatusEntry("a.txt", "modified")], status.Unstaged);
    }

    [Fact]
    public void Unstaging_without_a_head_fails()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "never committed\n");
        repo.Stage("a.txt");

        Assert.ThrowsAny<Exception>(() => Diff.UnstageAll(repo.Path));
        Assert.ThrowsAny<Exception>(() => Diff.UnstageFile(repo.Path, "a.txt"));
    }

    [Fact]
    public void The_working_diff_shows_every_line_of_an_untracked_file()
    {
        // show_untracked_content is what makes a new file arrive with its lines rather than as a
        // bare delta with no hunks, and recurse_untracked_dirs is what reports the file inside a
        // brand-new directory instead of just the directory.
        using var repo = new TempRepo();
        repo.Write("seed.txt", "so HEAD exists\n");
        repo.Commit("initial", "seed.txt");

        repo.Write("nested/fresh.txt", "one\ntwo\n");

        var file = Assert.Single(Diff.Working(repo.Path));

        Assert.Equal("nested/fresh.txt", file.NewPath);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(["one", "two"], hunk.Lines.Select(l => l.Content));
        Assert.All(hunk.Lines, l => Assert.Equal("+", l.Origin));
        Assert.Equal([1, 2], hunk.Lines.Select(l => l.NewLineno));
        Assert.All(hunk.Lines, l => Assert.Null(l.OldLineno));
    }

    [Fact]
    public void A_hunk_carries_full_file_context_with_both_line_numbers()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\ntwo\nthree\nfour\n");
        repo.Commit("initial", "a.txt");

        repo.Write("a.txt", "one\nTWO\nthree\nfour\n");

        var file = Assert.Single(Diff.Working(repo.Path));
        var hunk = Assert.Single(file.Hunks);

        Assert.Equal("modified", file.Status);
        Assert.StartsWith("@@", hunk.Header, StringComparison.Ordinal);

        // Every line of the file, not just the edit with a few lines around it.
        Assert.Equal(
            [(" ", "one"), ("-", "two"), ("+", "TWO"), (" ", "three"), (" ", "four")],
            hunk.Lines.Select(l => (l.Origin, l.Content)));

        // A context line advances both sides; a deletion only the old one, an addition only the new.
        Assert.Equal([1, 2, null, 3, 4], hunk.Lines.Select(l => l.OldLineno));
        Assert.Equal([1, null, 2, 3, 4], hunk.Lines.Select(l => l.NewLineno));
    }

    [Fact]
    public void A_staged_rename_is_one_entry_not_two()
    {
        // BUG-GIT-a, closed, on the diff side: Similarity is pinned to Renames explicitly —
        // stated rather than inherited from the user's diff.renames config — so a staged rename
        // is one "renamed" entry instead of an unrelated delete plus add.
        using var repo = new TempRepo();
        repo.Write("before.txt", "identical content\n");
        repo.Commit("initial", "before.txt");

        repo.Delete("before.txt");
        repo.Write("after.txt", "identical content\n");
        Diff.StageAll(repo.Path);

        var staged = Assert.Single(Diff.Staged(repo.Path));

        Assert.Equal("renamed", staged.Status);
        Assert.Equal(("before.txt", "after.txt"), (staged.OldPath, staged.NewPath));
    }

    [Fact]
    public void The_staged_diff_compares_the_index_against_head()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "original\n");
        repo.Commit("initial", "a.txt");

        repo.Write("a.txt", "staged\n");
        repo.Stage("a.txt");
        repo.Write("a.txt", "and edited again\n");

        var staged = Assert.Single(Diff.Staged(repo.Path));

        // The unstaged edit on top is invisible here; it belongs to the working diff.
        Assert.Contains(staged.Hunks.SelectMany(h => h.Lines), l => l is { Origin: "+", Content: "staged" });
        Assert.DoesNotContain(
            staged.Hunks.SelectMany(h => h.Lines), l => l.Content == "and edited again");
    }

    [Fact]
    public void A_commit_diff_compares_against_its_first_parent()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("first", "a.txt");
        repo.Write("a.txt", "two\n");
        var second = repo.Commit("second", "a.txt");

        var file = Assert.Single(Diff.Commit(repo.Path, second.Sha));

        Assert.Equal("modified", file.Status);
        Assert.Contains(file.Hunks.SelectMany(h => h.Lines), l => l is { Origin: "+", Content: "two" });
    }

    [Fact]
    public void A_root_commit_diffs_against_nothing()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        var root = repo.Commit("first", "a.txt");

        var file = Assert.Single(Diff.Commit(repo.Path, root.Sha));

        Assert.Equal("added", file.Status);
    }

    [Fact]
    public void A_committed_rename_is_one_renamed_entry_carrying_both_paths()
    {
        // BUG-GIT-a, closed: with rename detection on, a pure rename is a single entry whose
        // old and new paths are both present — the renderer's diff header shows new ?? old.
        using var repo = new TempRepo();
        repo.Write("before.txt", "same content\n");
        repo.Commit("first", "before.txt");

        repo.Delete("before.txt");
        repo.Write("after.txt", "same content\n");
        repo.Stage("before.txt", "after.txt");
        var renaming = repo.Commit("rename", []);

        var file = Assert.Single(Diff.Commit(repo.Path, renaming.Sha));

        Assert.Equal("renamed", file.Status);
        Assert.Equal("before.txt", file.OldPath);
        Assert.Equal("after.txt", file.NewPath);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void The_file_list_of_a_commit_names_every_path_without_its_content()
    {
        // GIT-035: this is what the graph expands a commit into, so it must stay content-free —
        // the whole-file context GIT-029 mandates is only paid for once a file is picked.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Write("keep.txt", "keep\n");
        repo.Commit("first", "a.txt", "keep.txt");

        repo.Write("a.txt", "two\n");
        repo.Write("b.txt", "new\n");
        var second = repo.Commit("second", "a.txt", "b.txt");

        var files = Diff.CommitFiles(repo.Path, second.Sha);

        Assert.Equal(
            [("a.txt", "modified"), ("b.txt", "added")],
            files.Select(f => (f.NewPath, f.Status)));
        // Untouched by this commit, so it is not in the list at all.
        Assert.DoesNotContain(files, f => f.NewPath == "keep.txt");
    }

    [Fact]
    public void A_commit_file_diff_returns_only_the_file_it_was_asked_for()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Write("b.txt", "one\n");
        repo.Commit("first", "a.txt", "b.txt");

        repo.Write("a.txt", "two\n");
        repo.Write("b.txt", "two\n");
        var second = repo.Commit("second", "a.txt", "b.txt");

        var file = Assert.Single(Diff.CommitFile(repo.Path, second.Sha, "b.txt", null));

        Assert.Equal("b.txt", file.NewPath);
        Assert.Contains(file.Hunks.SelectMany(h => h.Lines), l => l is { Origin: "+", Content: "two" });
    }

    [Fact]
    public void A_renamed_file_stays_renamed_only_when_both_of_its_paths_are_given()
    {
        // GIT-035, and the reason `oldPath` is a parameter at all: libgit2 applies the pathspec
        // before rename detection, so filtering by the new path alone leaves the matching delete
        // out and what survives is an "added" file whose diff claims every line is new.
        using var repo = new TempRepo();
        repo.Write("before.txt", "same content\n");
        repo.Commit("first", "before.txt");

        repo.Delete("before.txt");
        repo.Write("after.txt", "same content\n");
        repo.Stage("before.txt", "after.txt");
        var renaming = repo.Commit("rename", []);

        var paired = Assert.Single(Diff.CommitFile(repo.Path, renaming.Sha, "after.txt", "before.txt"));
        Assert.Equal("renamed", paired.Status);
        Assert.Equal("before.txt", paired.OldPath);

        var alone = Assert.Single(Diff.CommitFile(repo.Path, renaming.Sha, "after.txt", null));
        Assert.Equal("added", alone.Status);
    }

    [Fact]
    public void Committing_uses_the_explicit_author_only_when_both_halves_are_given()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Stage("a.txt");

        var sha = Diff.CommitIndex(repo.Path, "with an author", "Ada", "ada@example.com");

        using var handle = repo.Open();
        var commit = handle.Head.Tip;
        Assert.Equal(sha, commit.Id.Sha);
        Assert.Equal("Ada", commit.Author.Name);
        Assert.Equal("ada@example.com", commit.Author.Email);

        // Author and committer are the same signature.
        Assert.Equal(commit.Author.Name, commit.Committer.Name);
    }

    [Fact]
    public void A_half_supplied_author_falls_back_entirely_to_the_configured_identity()
    {
        // Not merged with the configured identity — the partial override is discarded whole.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Stage("a.txt");

        var sha = Diff.CommitIndex(repo.Path, "half an author", "Ada", authorEmail: null);

        using var handle = repo.Open();
        Assert.Equal(sha, handle.Head.Tip.Id.Sha);
        Assert.Equal("CodeFlow Test", handle.Head.Tip.Author.Name);
    }

    [Fact]
    public void Committing_writes_exactly_what_is_staged_and_moves_the_branch()
    {
        using var repo = new TempRepo();
        repo.Write("staged.txt", "in the commit\n");
        repo.Stage("staged.txt");
        repo.Write("unstaged.txt", "not in the commit\n");

        var sha = Diff.CommitIndex(repo.Path, "first", null, null);

        using var handle = repo.Open();
        Assert.Equal(sha, handle.Head.Tip.Id.Sha);
        Assert.Equal("first", handle.Head.Tip.Message);
        Assert.Empty(handle.Head.Tip.Parents);
        Assert.NotNull(handle.Head.Tip["staged.txt"]);
        Assert.Null(handle.Head.Tip["unstaged.txt"]);
    }

    [Fact]
    public void The_commit_message_is_never_rewritten()
    {
        // prettifyMessage would collapse the blank lines and strip the '#' line — silently editing
        // text the user typed.
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\n");
        repo.Stage("a.txt");

        const string message = "subject\n\n\nbody\n# not a comment here\n";
        var sha = Diff.CommitIndex(repo.Path, message, null, null);

        using var handle = repo.Open();
        Assert.Equal(sha, handle.Head.Tip.Id.Sha);
        Assert.Equal(message, handle.Head.Tip.Message);
    }
}
