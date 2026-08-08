using CodeFlow.Workspaces;
using Xunit;

namespace CodeFlow.Tests.Workspaces;

/// <summary>
/// A skill's folder: its traversal guard, its name sanitiser and the two creation paths that do not
/// go through <c>npx</c>. See <c>docs/business-rules/09-workspace-scoped.md</c>, <c>WS-007</c>.
/// </summary>
/// <remarks>
/// No vectors exist for any of this — <c>09-workspace-scoped.md</c> says outright that none of its
/// four files has a single extracted case. The rules are the specification, and these
/// run the real code against a temporary root rather than the user's own <c>~/CodeFlow</c>.
/// </remarks>
public sealed class SkillFilesTests
{
    /// <summary>
    /// <c>WS-007</c>: the guard is purely syntactic, and that is the whole of it.
    /// </summary>
    /// <remarks>
    /// Deliberately not one of the two guards in <c>Files/PathGuards.cs</c>: there is no
    /// canonicalisation and no containment check after the split, so a symlink inside a skill still
    /// leads wherever it leads. Tightening it here would make this codebase stricter than the app it
    /// replaces.
    /// </remarks>
    [Theory]
    [InlineData("../escaped.md")]
    [InlineData("references/../../escaped.md")]
    [InlineData("..\\escaped.md")]
    [InlineData("references//notes.md")]
    [InlineData("")]
    public void A_path_that_could_leave_the_skill_is_refused(string relPath)
    {
        using var root = new TempSkillsRoot();

        var failure = Assert.Throws<InvalidOperationException>(
            () => SkillFiles.ReadFile(root.Path, "demo", relPath));

        Assert.Equal("invalid file path", failure.Message);
    }

    [Fact]
    public void A_file_can_be_written_read_listed_and_deleted_inside_a_skill()
    {
        using var root = new TempSkillsRoot();
        SkillFiles.CreateCustom(root.Path, "demo", "# Demo\n");

        SkillFiles.WriteFile(root.Path, "demo", "references/notes.md", "note");

        Assert.Equal("note", SkillFiles.ReadFile(root.Path, "demo", "references/notes.md"));
        Assert.Equal(["SKILL.md", "references/notes.md"], SkillFiles.ListFiles(root.Path, "demo"));

        SkillFiles.DeleteFile(root.Path, "demo", "references/notes.md");
        Assert.Equal(["SKILL.md"], SkillFiles.ListFiles(root.Path, "demo"));
    }

    [Fact]
    public void Writing_a_nested_file_creates_the_directories_it_needs()
    {
        using var root = new TempSkillsRoot();
        SkillFiles.CreateCustom(root.Path, "demo", "# Demo\n");

        SkillFiles.WriteFile(root.Path, "demo", "scripts/deep/run.sh", "#!/bin/sh\n");

        Assert.Equal("#!/bin/sh\n", SkillFiles.ReadFile(root.Path, "demo", "scripts/deep/run.sh"));
    }

    [Fact]
    public void A_skill_that_does_not_exist_lists_nothing_rather_than_failing()
    {
        using var root = new TempSkillsRoot();

        Assert.Empty(SkillFiles.ListFiles(root.Path, "never-installed"));
    }

    [Fact]
    public void A_custom_skill_is_a_folder_with_the_markdown_the_user_wrote()
    {
        using var root = new TempSkillsRoot();

        var name = SkillFiles.CreateCustom(root.Path, "My Skill", "# My Skill\n");

        // Sanitised into one usable path segment: the space became a dash.
        Assert.Equal("My-Skill", name);
        Assert.Equal("# My Skill\n", SkillFiles.ReadFile(root.Path, name, "SKILL.md"));
    }

    [Fact]
    public void A_custom_skill_needs_a_name_that_survives_sanitising()
    {
        using var root = new TempSkillsRoot();

        var failure = Assert.Throws<InvalidOperationException>(
            () => SkillFiles.CreateCustom(root.Path, "  ---  ", "# x\n"));

        Assert.Equal("Please give the skill a name", failure.Message);
    }

    /// <summary>
    /// Both non-<c>npx</c> creation paths refuse an existing folder.
    /// </summary>
    /// <remarks>
    /// Worth pinning as a pair, because installing from skills.sh does <em>not</em> — that
    /// inconsistency is <c>BUG-WS-b</c>, and it is only a bug because these two do check.
    /// </remarks>
    [Fact]
    public void Creating_over_an_existing_skill_is_refused()
    {
        using var root = new TempSkillsRoot();
        SkillFiles.CreateCustom(root.Path, "demo", "# Demo\n");

        var failure = Assert.Throws<InvalidOperationException>(
            () => SkillFiles.CreateCustom(root.Path, "demo", "# Other\n"));

        Assert.Equal("A skill named \"demo\" already exists in this workspace", failure.Message);
    }

    [Fact]
    public void Importing_a_folder_copies_it_in_under_its_own_name()
    {
        using var root = new TempSkillsRoot();
        using var source = new TempSkillsRoot();

        var folder = Path.Combine(source.Path, "imported-skill");
        Directory.CreateDirectory(Path.Combine(folder, "references"));
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), "# Imported\n");
        File.WriteAllText(Path.Combine(folder, "references", "notes.md"), "note");

        var name = SkillFiles.ImportFromFolder(root.Path, folder);

        Assert.Equal("imported-skill", name);
        Assert.Equal(["SKILL.md", "references/notes.md"], SkillFiles.ListFiles(root.Path, name));
    }

    [Fact]
    public void A_folder_with_no_skill_markdown_is_not_a_skill()
    {
        using var root = new TempSkillsRoot();
        using var source = new TempSkillsRoot();

        var folder = Path.Combine(source.Path, "not-a-skill");
        Directory.CreateDirectory(folder);

        var failure = Assert.Throws<InvalidOperationException>(
            () => SkillFiles.ImportFromFolder(root.Path, folder));

        Assert.Equal("That folder isn't a skill — it has no SKILL.md", failure.Message);
    }

    /// <summary>
    /// The sanitiser is what stops a skill <em>name</em> from becoming a path.
    /// </summary>
    /// <remarks>
    /// Note <c>../escape</c>: the separator becomes a dash and the leading dots are then trimmed, so
    /// the traversal disappears entirely rather than surviving as a literal <c>..-</c> prefix. That
    /// is the reason a skill name needs no guard of its own, unlike the file paths inside one.
    /// </remarks>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "with-space")]
    [InlineData("a/b", "a-b")]
    [InlineData("../escape", "escape")]
    [InlineData("-trimmed-", "trimmed")]
    [InlineData("kebab-case_and.dots", "kebab-case_and.dots")]
    [InlineData("###", "")]
    public void A_name_is_reduced_to_one_usable_path_segment(string name, string expected) =>
        Assert.Equal(expected, SkillFiles.SanitizeName(name));
}

/// <summary>A throwaway stand-in for a workspace's skills root.</summary>
/// <remarks>
/// The production root is under <c>AppPaths.BaseDirectory</c> — the user's real <c>~/CodeFlow</c> —
/// which is exactly why every function here takes its root as an argument.
/// </remarks>
internal sealed class TempSkillsRoot : IDisposable
{
    public TempSkillsRoot() =>
        Path = System.IO.Path.Combine(
            Directory.CreateTempSubdirectory("codeflow-skills-").FullName, ".claude", "skills");

    public string Path { get; }

    public void Dispose()
    {
        var root = Directory.GetParent(System.IO.Path.GetDirectoryName(Path)!)!.FullName;

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
