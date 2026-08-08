using CodeFlow.Files;
using CodeFlow.Tests.Git;
using Xunit;

namespace CodeFlow.Tests.Files;

/// <summary>
/// The explorer's file operations, against the scenarios.
/// See <c>docs/business-rules/11-files-search-terminal.md</c>, <c>FILE-001</c>–<c>FILE-005</c>.
/// </summary>
/// <remarks>
/// These fixtures are <c>kind: "scenario"</c>: the steps are followed by hand, and only the
/// expected values are read back out of the JSON — so the fixture stays the source of truth.
/// No git repository is needed; <c>fsops</c> has no git dependency at all.
/// </remarks>
public sealed class FileOpsTests
{
    private const string Vectors = "fsops.vectors.json";

    [Fact]
    public void Creating_a_nested_path_makes_every_missing_parent_in_one_call()
    {
        using var repo = new TempDirectory();

        FileOps.CreateDir(repo.Path, "src/nested");
        Assert.Equal(
            GitFixtures.Expected(Vectors, "creates-nested-file-and-dir").GetProperty("srcNestedIsDir").GetBoolean(),
            Directory.Exists(Path.Combine(repo.Path, "src", "nested")));

        FileOps.CreateFile(repo.Path, "src/nested/new.ts");

        Assert.Equal(
            GitFixtures.String(Vectors, "creates-nested-file-and-dir", "readFileTextResult"),
            FileOps.ReadFileText(repo.Path, "src/nested/new.ts"));
    }

    [Fact]
    public void A_move_lands_where_it_was_dropped()
    {
        using var repo = new TempDirectory();
        Seed(repo);

        var expected = GitFixtures.Expected(Vectors, "move-refuses-destructive-cases");

        Assert.Equal("src/nested/a.ts", FileOps.MovePath(repo.Path, "src/a.ts", "src/nested"));
        Assert.True(File.Exists(Path.Combine(repo.Path, Relative(expected, "moveIntoSiblingFolder"))));

        // An empty destination is the repo root, not a rejected argument.
        Assert.Equal("a.ts", FileOps.MovePath(repo.Path, "src/nested/a.ts", ""));
        Assert.True(File.Exists(Path.Combine(repo.Path, Relative(expected, "moveBackToRoot"))));
    }

    [Fact]
    public void A_name_already_taken_at_the_destination_is_refused_rather_than_overwritten()
    {
        using var repo = new TempDirectory();
        Seed(repo);
        FileOps.MovePath(repo.Path, "src/a.ts", "");

        var failure = Assert.Throws<InvalidOperationException>(
            () => FileOps.MovePath(repo.Path, "other/a.ts", ""));

        Assert.Equal("a.ts already exists here", failure.Message);

        // The source is still where it was: a refused move moves nothing.
        Assert.True(File.Exists(Path.Combine(repo.Path, "other", "a.ts")));
    }

    [Theory]
    [InlineData("src", "src")]
    [InlineData("src", "src/nested")]
    public void A_folder_cannot_swallow_itself_directly_or_through_a_descendant(string from, string destination)
    {
        using var repo = new TempDirectory();
        Seed(repo);

        var failure = Assert.Throws<InvalidOperationException>(
            () => FileOps.MovePath(repo.Path, from, destination));

        Assert.Equal("cannot move a folder into itself", failure.Message);
        Assert.True(Directory.Exists(Path.Combine(repo.Path, "src", "nested")));
    }

    [Fact]
    public void Dropping_something_back_where_it_already_lives_is_a_no_op_not_a_failure()
    {
        using var repo = new TempDirectory();
        Seed(repo);

        Assert.Equal("other/a.ts", FileOps.MovePath(repo.Path, "other/a.ts", "other"));
    }

    [Fact]
    public void Nothing_may_leave_the_repository()
    {
        using var repo = new TempDirectory();
        Seed(repo);
        FileOps.MovePath(repo.Path, "src/a.ts", "");

        var failure = Assert.Throws<InvalidOperationException>(() => FileOps.MovePath(repo.Path, "a.ts", ".."));

        Assert.Equal("path escapes the repository root", failure.Message);
    }

    [Fact]
    public void An_existing_name_is_reported_back_instead_of_being_silently_truncated()
    {
        using var repo = new TempDirectory();

        FileOps.CreateFile(repo.Path, "dup.txt");
        File.WriteAllText(Path.Combine(repo.Path, "dup.txt"), "not empty");

        var file = Assert.Throws<InvalidOperationException>(() => FileOps.CreateFile(repo.Path, "dup.txt"));
        Assert.Equal(Error(Vectors, "rejects-duplicates-empty-traversal", "duplicateCreateFile"), file.Message);

        // The point of create_new: the file that was already there is untouched.
        Assert.Equal("not empty", File.ReadAllText(Path.Combine(repo.Path, "dup.txt")));

        FileOps.CreateDir(repo.Path, "dir");
        var directory = Assert.Throws<InvalidOperationException>(() => FileOps.CreateDir(repo.Path, "dir"));
        Assert.Equal(Error(Vectors, "rejects-duplicates-empty-traversal", "duplicateCreateDir"), directory.Message);
    }

    [Fact]
    public void A_whitespace_only_name_is_rejected_as_empty_before_the_component_check()
    {
        using var repo = new TempDirectory();

        var failure = Assert.Throws<InvalidOperationException>(() => FileOps.CreateFile(repo.Path, "   "));

        Assert.Equal(Error(Vectors, "rejects-duplicates-empty-traversal", "blankName"), failure.Message);
    }

    [Fact]
    public void A_creation_naming_a_parent_directory_is_rejected_and_writes_nothing()
    {
        using var repo = new TempDirectory();

        var file = Assert.Throws<InvalidOperationException>(() => FileOps.CreateFile(repo.Path, "../escaped.txt"));
        Assert.Equal(Error(Vectors, "rejects-duplicates-empty-traversal", "dotDotCreateFile"), file.Message);

        var directory = Assert.Throws<InvalidOperationException>(() => FileOps.CreateDir(repo.Path, "../escaped"));
        Assert.Equal(Error(Vectors, "rejects-duplicates-empty-traversal", "dotDotCreateDir"), directory.Message);

        Assert.False(File.Exists(Path.Combine(Directory.GetParent(repo.Path)!.FullName, "escaped.txt")));
    }

    /// <summary>
    /// <c>BUG-FILE-a</c>, closed: the not-yet-on-disk half of the guard now normalises too.
    /// </summary>
    /// <remarks>
    /// 1.7.2's guard only caught an escape it could canonicalise — a write through <c>..</c> to a
    /// file that did not exist yet fell back to the raw join and landed outside the repository.
    /// The fallback is now a lexical <c>Path.GetFullPath</c>, so both halves refuse, and nothing
    /// is written. A benign <c>..</c> that stays inside keeps working.
    /// </remarks>
    [Fact]
    public void A_write_to_a_path_that_does_not_exist_yet_is_refused_before_touching_disk()
    {
        using var repo = new TempDirectory();
        var outside = Path.Combine(Directory.GetParent(repo.Path)!.FullName, $"escaped-{Guid.NewGuid():N}.txt");

        var failure = Assert.Throws<InvalidOperationException>(
            () => FileOps.WriteFileText(repo.Path, $"../{Path.GetFileName(outside)}", "escaped"));

        Assert.Equal("path escapes the repository root", failure.Message);
        Assert.False(File.Exists(outside), "the guard must refuse before writing anything");

        // The existing-path half behaves the same as it always did.
        File.WriteAllText(outside, "escaped");
        try
        {
            var read = Assert.Throws<InvalidOperationException>(
                () => FileOps.ReadFileText(repo.Path, $"../{Path.GetFileName(outside)}"));

            Assert.Equal("path escapes the repository root", read.Message);
        }
        finally
        {
            File.Delete(outside);
        }

        // A `..` that never leaves the repository is not an escape — new path or not.
        Directory.CreateDirectory(Path.Combine(repo.Path, "sub"));
        FileOps.WriteFileText(repo.Path, "sub/../inside.txt", "kept");
        Assert.Equal("kept", File.ReadAllText(Path.Combine(repo.Path, "inside.txt")));
    }

    [Fact]
    public void A_folder_asked_for_as_a_file_says_so_instead_of_surfacing_the_os_error()
    {
        using var repo = new TempDirectory();
        FileOps.CreateDir(repo.Path, "src");

        var failure = Assert.Throws<InvalidOperationException>(() => FileOps.ReadFileText(repo.Path, "src"));

        Assert.Equal("src is a folder, not a file", failure.Message);
    }

    [Fact]
    public void The_tree_lists_directories_first_then_case_insensitively_by_name()
    {
        using var repo = new TempDirectory();
        foreach (var name in new[] { "Zebra.ts", "apple.ts", "Beta.ts" })
        {
            FileOps.CreateFile(repo.Path, name);
        }

        foreach (var name in new[] { "zulu", "Alpha" })
        {
            FileOps.CreateDir(repo.Path, name);
        }

        Directory.CreateDirectory(Path.Combine(repo.Path, ".git"));

        var entries = FileOps.ListDir(repo.Path, subPath: null);

        Assert.Equal(["Alpha", "zulu", "apple.ts", "Beta.ts", "Zebra.ts"], entries.Select(e => e.Name));
        Assert.Equal([true, true, false, false, false], entries.Select(e => e.IsDir));
    }

    [Fact]
    public void A_subdirectory_listing_reports_repo_relative_paths()
    {
        using var repo = new TempDirectory();
        FileOps.CreateFile(repo.Path, "src/nested/one.ts");

        var entries = FileOps.ListDir(repo.Path, "src/nested");

        Assert.Equal("src/nested/one.ts", Assert.Single(entries).Path);
    }

    [Fact]
    public void An_export_needs_an_absolute_path_and_an_existing_folder()
    {
        using var repo = new TempDirectory();

        var relative = Assert.Throws<InvalidOperationException>(
            () => FileOps.WriteFileBytes("snap.png", [1, 2, 3]));
        Assert.Equal("expected an absolute path, got: snap.png", relative.Message);

        var missing = Path.Combine(repo.Path, "no-such-folder");
        var failure = Assert.Throws<InvalidOperationException>(
            () => FileOps.WriteFileBytes(Path.Combine(missing, "snap.png"), [1, 2, 3]));
        Assert.Equal($"no such folder: {missing}", failure.Message);
    }

    /// <summary>
    /// <c>DIVERGENCE-FILE-d</c>: the one write that is deliberately not scoped to a repository.
    /// </summary>
    /// <remarks>
    /// The whole point of an export is that it lands wherever the user pointed the save dialog —
    /// Desktop, Downloads, a scratch folder — and the dialog is the authorisation. Adding
    /// containment here would break the only feature that calls it.
    /// </remarks>
    [Fact]
    public void An_export_writes_wherever_the_save_dialog_pointed()
    {
        using var anywhere = new TempDirectory();
        var target = Path.Combine(anywhere.Path, "snap.png");

        FileOps.WriteFileBytes(target, [0x89, 0x50, 0x4E, 0x47]);

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(target));
    }

    private static void Seed(TempDirectory repo)
    {
        FileOps.CreateDir(repo.Path, "src/nested");
        FileOps.CreateDir(repo.Path, "other");
        FileOps.CreateFile(repo.Path, "src/a.ts");
        FileOps.CreateFile(repo.Path, "other/a.ts");
    }

    private static string Relative(System.Text.Json.JsonElement expected, string key) =>
        expected.GetProperty(key).GetProperty("fileExistsAt").GetString()!;

    private static string Error(string file, string caseId, string key) =>
        GitFixtures.Expected(file, caseId).GetProperty(key).GetProperty("error").GetString()!;
}
